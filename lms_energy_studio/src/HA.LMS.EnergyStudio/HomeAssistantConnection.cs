using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using LMS.Energy.Core;
namespace HA.LMS.EnergyStudio;
// One transport per host. Browsers receive only mapped, normalised measurements.
public sealed class HomeAssistantConnection(IConfiguration settings,ConfigurationStore store,ILogger<HomeAssistantConnection> logger):BackgroundService
{
    readonly object stateGate=new();
    readonly SemaphoreSlim sendGate=new(1,1);
    readonly ConcurrentDictionary<int,TaskCompletionSource<JsonNode?>> pending=new();
    readonly Dictionary<string,JsonObject> states=[];
    readonly Dictionary<string,JsonObject?> bootstrapEvents=[];
    JsonArray registry=[],devices=[],areas=[];
    ClientWebSocket? socket;
    bool bootstrapping;
    int sequence;
    volatile bool connected;
    public bool Connected=>connected;
    string Token=>Environment.GetEnvironmentVariable("SUPERVISOR_TOKEN")??settings["EnergyStudio:HomeAssistantToken"]??"";
    Uri Rest=>new(Environment.GetEnvironmentVariable("SUPERVISOR_TOKEN") is not null?"http://supervisor/core/api/":(settings["EnergyStudio:HomeAssistantUrl"]??"http://localhost:8123").TrimEnd('/')+"/api/");
    Uri WebSocket=>Environment.GetEnvironmentVariable("SUPERVISOR_TOKEN") is not null?new("ws://supervisor/core/websocket"):new UriBuilder(new Uri(Rest,"websocket")){Scheme=Rest.Scheme=="https"?"wss":"ws"}.Uri;
    public Dictionary<string,JsonObject> Catalogue()
    {
        lock(stateGate)
        {
            var result=new Dictionary<string,JsonObject>();
            foreach(var (id,state) in states)
            {
                if(!id.StartsWith("sensor.")&&!id.StartsWith("binary_sensor.")&&!id.StartsWith("input_number."))continue;
                var entry=registry.FirstOrDefault(e=>e?["entity_id"]?.ToString()==id);var device=devices.FirstOrDefault(d=>d?["id"]?.ToString()==entry?["device_id"]?.ToString());
                var areaId=entry?["area_id"]?.ToString()??device?["area_id"]?.ToString();var area=areas.FirstOrDefault(a=>a?["area_id"]?.ToString()==areaId);
                var item=(JsonObject)state.DeepClone(); item["registry_id"]=entry?["id"]?.DeepClone(); item["device_id"]=entry?["device_id"]?.DeepClone();item["device_name"]=(device?["name_by_user"]??device?["name"])?.DeepClone();item["area_name"]=area?["name"]?.DeepClone();
                // Bound values and redact common credential attributes before returning a catalogue.
                item.Remove("context");item["attributes"]=Sanitize(item["attributes"],0);
                item["history_capability"]="recorder-if-enabled";item["statistics_capability"]=state["attributes"]?["state_class"]?.DeepClone();
                result[id]=item;
            }
            foreach(var e in registry) { var id=e?["entity_id"]?.ToString();if(id is not null&&id.StartsWith("sensor.")&&!result.ContainsKey(id))result[id]=new JsonObject{["entity_id"]=id,["registry_id"]=e?["id"]?.DeepClone(),["state"]="unavailable",["attributes"]=new JsonObject()}; }
            return result;
        }
    }
    static JsonNode? Sanitize(JsonNode? node,int depth)
    {
        if(depth>8)return null;
        if(node is JsonObject o){var result=new JsonObject();foreach(var (key,value) in o.Take(128))if(!new[]{"token","password","secret","credential","authorization","url","api_key","access_key","private_key"}.Any(s=>key.Contains(s,StringComparison.OrdinalIgnoreCase)))result[key]=Sanitize(value,depth+1);return result;}
        if(node is JsonArray a)return new JsonArray(a.Take(512).Select(v=>Sanitize(v,depth+1)).ToArray());
        if(node is JsonValue v&&v.TryGetValue<string>(out var text)&&text.Length>2048)return JsonValue.Create(text[..2048]);
        return node?.DeepClone();
    }
    public (Binding Binding,JsonObject? Entity) Resolve(Binding binding,Dictionary<string,JsonObject> catalogue)
    {
        if(binding.RegistryId is not null){var matches=catalogue.Values.Where(e=>e["registry_id"]?.ToString()==binding.RegistryId).ToArray();return matches.Length==1?(binding with{EntityId=matches[0]["entity_id"]!.ToString()},matches[0]):(binding,null);}
        return (binding,catalogue.GetValueOrDefault(binding.EntityId));
    }
    public EnergyConfiguration ResolvedConfiguration()
    {
        var c=store.Get();var catalogue=Catalogue();return c with{Mappings=c.Mappings.ToDictionary(p=>p.Key,p=>Resolve(p.Value,catalogue).Binding)};
    }
    public JsonObject? Tariff()
    {
        var tariff=store.Get().Layout["tariff"]?.DeepClone() as JsonObject;
        if(tariff is null || tariff["confirmed"]?.GetValue<bool>()!=true)return null;
        var catalogue=Catalogue();var intervals=tariff["intervals"] as JsonArray??new JsonArray();tariff["intervals"]=intervals;
        if(connected)foreach(var adapter in tariff["adapters"]?.AsArray()??[])if(adapter is JsonObject a && catalogue.TryGetValue(a["entityId"]?.ToString()??"",out var e) && e["state"]?.ToString() is not ("unknown" or "unavailable"))foreach(var row in TariffAdapter.Read(a,e))intervals.Add(row?.DeepClone());
        return tariff;
    }
    public object Snapshot()
    {
        var c=ResolvedConfiguration();var catalogue=Catalogue();var now=DateTimeOffset.UtcNow;
        var readings=Configuration.Fields(c.Layout,c.Cells).ToDictionary(p=>p.Key,p=>{var b=c.Mappings.GetValueOrDefault(p.Key);var e=b is null?null:Resolve(b,catalogue).Entity;var m=Measurements.Read(b,e,p.Value,connected,now);return b?.RegistryId is not null&&e is null?m with{Quality="repair-required"}:m;});
        void Pair(string net,string positive,string negative) { if(c.Mappings.ContainsKey(net)||(!c.Mappings.ContainsKey(positive)&&!c.Mappings.ContainsKey(negative)))return;var a=readings[positive];var b=readings[negative];readings[net]=Measurements.Sum([a,b with{Value=-b.Value}],"kW",now); }
        foreach(var key in readings.Keys.ToArray())if((key is "grid.import" or "grid.export" || key.StartsWith("arrays.")||key.StartsWith("sources.")||key.StartsWith("loads.")||key.EndsWith(".charge")||key.EndsWith(".discharge"))&&readings[key].Value<0)readings[key]=readings[key] with{Value=null,Quality="invalid-direction"};
        Pair("grid","grid.import","grid.export");
        foreach(var battery in c.Layout["storage"]?.AsArray()??[]){var id=battery!["id"]!.ToString();Pair($"batteries.{id}.power",$"batteries.{id}.discharge",$"batteries.{id}.charge");}
        if(!c.Mappings.ContainsKey("battery"))readings["battery"]=Measurements.Sum((c.Layout["storage"]?.AsArray()??[]).Select(b=>readings[$"batteries.{b!["id"]}.power"]),"kW",now);
        if(!c.Mappings.ContainsKey("solar"))readings["solar"]=Measurements.Sum((c.Layout["arrayRoutes"]?.AsArray()??[]).Select(b=>readings[$"arrays.{b!["id"]}"]),"kW",now);
        foreach(var v in (c.Layout["vehicles"]?.AsArray()??[]).Take(c.Layout["cars"]?.GetValue<int>()??0)){var key=$"ev.{v!["id"]}";if(readings[key].Value<0 && !(v["v2g"]?.GetValue<bool>()==true&&c.Layout["chargerV2G"] is JsonArray caps&&v["charger"]?.GetValue<int>() is int i&&i>=0&&i<caps.Count&&caps[i]?.GetValue<bool>()==true))readings[key]=readings[key] with{Value=null,Quality="unsupported-v2g"};}
        if(c.Layout["accounting"]?["homeIncludesEvAndCharging"]?.GetValue<bool>()==true)
        {
            var parts=new List<Measurement>{readings["home"]};
            parts.AddRange((c.Layout["vehicles"]?.AsArray()??[]).Take(c.Layout["cars"]?.GetValue<int>()??0).Select(v=>{var m=readings[$"ev.{v!["id"]}"];return m with{Value=m.Value is null?null:-Math.Max(0,m.Value.Value)};}));
            parts.AddRange((c.Layout["storage"]?.AsArray()??[]).Select(v=>{var m=readings[$"batteries.{v!["id"]}.power"];return m with{Value=m.Value is null?null:Math.Min(0,m.Value.Value)};}));readings["home"]=Measurements.Sum(parts,"kW",now);
        }
        // AC balance uses inverter AC outputs plus AC sources/storage; DC arrays are not counted again.
        var balance=new List<Measurement>{readings["grid"],readings["home"] with{Value=-readings["home"].Value}};
        balance.AddRange((c.Layout["inverters"]?.AsArray()??[]).Select(v=>readings[$"inverters.{v!["id"]}"]));
        balance.AddRange((c.Layout["storage"]?.AsArray()??[]).Where(v=>v?["connection"]?.ToString()!="inverter").Select(v=>readings[$"batteries.{v!["id"]}.power"]));
        balance.AddRange((c.Layout["sources"]?.AsArray()??[]).Where(v=>v?["connection"]?.ToString()=="ac").Select(v=>readings[$"sources.{v!["id"]}"]));
        balance.AddRange((c.Layout["vehicles"]?.AsArray()??[]).Take(c.Layout["cars"]?.GetValue<int>()??0).Select(v=>readings[$"ev.{v!["id"]}"] with{Value=-readings[$"ev.{v!["id"]}"].Value}));
        return new{connection=connected?"connected":"disconnected",revision=c.Revision,observedAt=now,readings,residual=Measurements.Sum(balance,"kW",now),meterToleranceKw=c.Layout["accounting"]?["toleranceKw"]?.GetValue<double>()??.05};
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay=1;
        while(!stoppingToken.IsCancellationRequested)
        {
            Task? reader=null;using var session=CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            try
            {
                if(Token.Length==0)throw new InvalidOperationException("HA credentials are not configured.");
                socket=new ClientWebSocket();socket.Options.KeepAliveInterval=TimeSpan.FromSeconds(20);
                await socket.ConnectAsync(WebSocket,session.Token);
                if((await Receive(session.Token))["type"]?.ToString()!="auth_required")throw new IOException("Unexpected HA authentication handshake.");
                await Send(new JsonObject{["type"]="auth",["access_token"]=Token},session.Token);
                if((await Receive(session.Token))["type"]?.ToString()!="auth_ok")throw new IOException("HA authentication failed.");
                lock(stateGate){bootstrapping=true;bootstrapEvents.Clear();}
                reader=ReadLoop(session.Token);
                await Command(new JsonObject{["type"]="subscribe_events",["event_type"]="state_changed"},session.Token);
                foreach(var eventType in new[]{"entity_registry_updated","device_registry_updated","area_registry_updated"})await Command(new JsonObject{["type"]="subscribe_events",["event_type"]=eventType},session.Token);
                var initial=(await Command(new JsonObject{["type"]="get_states"},session.Token))!.AsArray();
                lock(stateGate){states.Clear();foreach(var e in initial)states[e!["entity_id"]!.ToString()]=(JsonObject)e.DeepClone();foreach(var (id,e) in bootstrapEvents)if(e is null)states.Remove(id);else if(!states.TryGetValue(id,out var snapshot)||Stamp(e)>=Stamp(snapshot))states[id]=e;bootstrapEvents.Clear();bootstrapping=false;}
                await RefreshRegistry(session.Token);connected=true;delay=1;
                while(!reader.IsCompleted){if(await Task.WhenAny(reader,Task.Delay(TimeSpan.FromSeconds(25),session.Token))==reader)break;await Command(new JsonObject{["type"]="ping"},session.Token);await RefreshStates(session.Token);}
                await reader;
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception ex){logger.LogWarning("Home Assistant connection interrupted ({Type}); reconnecting.",ex.GetType().Name);}
            finally{connected=false;session.Cancel();socket?.Dispose();if(reader is not null)try{await reader;}catch{}foreach(var p in pending.Values)p.TrySetException(new IOException("HA disconnected."));pending.Clear();}
            try{await Task.Delay(TimeSpan.FromSeconds(delay),stoppingToken);}catch(OperationCanceledException){break;}delay=Math.Min(delay*2,60);
        }
    }
    static DateTimeOffset Stamp(JsonObject e)=>DateTimeOffset.TryParse(e["last_updated"]?.ToString(),out var t)?t:DateTimeOffset.MinValue;
    async Task RefreshStates(CancellationToken ct)
    {
        lock(stateGate){bootstrapping=true;bootstrapEvents.Clear();}
        var initial=(await Command(new JsonObject{["type"]="get_states"},ct))!.AsArray();
        lock(stateGate){states.Clear();foreach(var e in initial)states[e!["entity_id"]!.ToString()]=(JsonObject)e.DeepClone();foreach(var (id,e) in bootstrapEvents)if(e is null)states.Remove(id);else if(!states.TryGetValue(id,out var snapshot)||Stamp(e)>=Stamp(snapshot))states[id]=e;bootstrapEvents.Clear();bootstrapping=false;}
    }
    async Task RefreshRegistry(CancellationToken ct)
    {
        var r=(await Command(new JsonObject{["type"]="config/entity_registry/list"},ct))!.AsArray();var d=(await Command(new JsonObject{["type"]="config/device_registry/list"},ct))!.AsArray();var a=(await Command(new JsonObject{["type"]="config/area_registry/list"},ct))!.AsArray();lock(stateGate){registry=r;devices=d;areas=a;}
    }
    async Task ReadLoop(CancellationToken ct)
    {
        try{while(!ct.IsCancellationRequested)
        {
            var message=await Receive(ct);var type=message["type"]?.ToString();
            if(type is "result" or "pong") { if(pending.TryRemove(message["id"]!.GetValue<int>(),out var completion)) {if(type=="pong"||message["success"]?.GetValue<bool>()==true)completion.TrySetResult(message["result"]?.DeepClone());else completion.TrySetException(new IOException("HA command rejected."));} }
            else if(type=="event")
            {
                var e=message["event"]!;if(e["event_type"]?.ToString()=="state_changed")
                {var data=e["data"]!;var id=data["entity_id"]!.ToString();var next=data["new_state"] as JsonObject;lock(stateGate){if(bootstrapping)bootstrapEvents[id]=next is null?null:(JsonObject)next.DeepClone();else if(next is null)states.Remove(id);else states[id]=(JsonObject)next.DeepClone();}}
                else _=RefreshRegistrySafe(ct);
            }
        }}finally{connected=false;foreach(var p in pending.Values)p.TrySetException(new IOException("HA receive loop ended."));}
    }
    async Task RefreshRegistrySafe(CancellationToken ct){var currentSocket=socket;try{await RefreshRegistry(ct);}catch{if(ReferenceEquals(currentSocket,socket)){connected=false;currentSocket?.Abort();}}}
    async Task<JsonNode?> Command(JsonObject request,CancellationToken ct)
    {
        var id=Interlocked.Increment(ref sequence);request["id"]=id;var completion=new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);pending[id]=completion;
        try{await Send(request,ct);return await completion.Task.WaitAsync(TimeSpan.FromSeconds(20),ct);}finally{pending.TryRemove(id,out _);}
    }
    async Task Send(JsonObject message,CancellationToken ct){await sendGate.WaitAsync(ct);try{await socket!.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()),WebSocketMessageType.Text,true,ct);}finally{sendGate.Release();}}
    async Task<JsonObject> Receive(CancellationToken ct)
    {
        using var buffer=new MemoryStream();var chunk=new byte[16384];WebSocketReceiveResult result;
        do{result=await socket!.ReceiveAsync(chunk,ct);if(result.MessageType==WebSocketMessageType.Close)throw new IOException("HA closed connection.");buffer.Write(chunk,0,result.Count);if(buffer.Length>16*1024*1024)throw new IOException("HA response exceeded limit.");}while(!result.EndOfMessage);
        return JsonNode.Parse(buffer.ToArray())!.AsObject();
    }
    public async Task<RecordedHistory> History(string key,DateTimeOffset start,DateTimeOffset end,CancellationToken ct)
    {
        var c=ResolvedConfiguration();if(!c.Mappings.TryGetValue(key,out var binding)||!Configuration.Fields(c.Layout,c.Cells).TryGetValue(key,out var role))throw new ArgumentException("Select a mapped reading.");
        if(!connected)throw new IOException("HA is disconnected.");
        using var client=new HttpClient{Timeout=TimeSpan.FromSeconds(30)};client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",Token);
        var path=$"history/period/{Uri.EscapeDataString(start.ToString("O"))}?end_time={Uri.EscapeDataString(end.ToString("O"))}&filter_entity_id={Uri.EscapeDataString(binding.EntityId)}&significant_changes_only=0";
        using var response=await client.GetAsync(new Uri(Rest,path),ct);response.EnsureSuccessStatusCode();
        var data=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!.AsArray();var points=new List<HistoryPoint>();
        foreach(var row in data.SelectMany(a=>a!.AsArray()))
        {if(!DateTimeOffset.TryParse(row?["last_updated"]?.ToString()??row?["last_changed"]?.ToString(),out var time))continue;var m=Measurements.Read(binding with{MaxAgeSeconds=0},row,role,true,time);points.Add(new(time,m.Value,m.Quality));}
        var ordered=points.Where(p=>p.Time<=end).OrderBy(p=>p.Time).DistinctBy(p=>p.Time).ToArray();
        if(ordered.Length>1 || role!="energy" || !string.IsNullOrEmpty(binding.Pointer))return new(ordered,"ha-recorder");
        // Long-term statistics use reset-adjusted sum, not the live counter state.
        var statistics=await Command(new JsonObject{["type"]="recorder/statistics_during_period",["start_time"]=start.AddHours(-1).ToString("O"),["end_time"]=end.ToString("O"),["statistic_ids"]=new JsonArray(binding.EntityId),["period"]="hour",["types"]=new JsonArray("sum"),["units"]=new JsonObject{["energy"]="kWh"}},ct);
        var historical=new List<HistoryPoint>();
        foreach(var row in statistics?[binding.EntityId]?.AsArray()??[]){if(row?["end"] is null)continue;var time=DateTimeOffset.FromUnixTimeMilliseconds(row["end"]!.GetValue<long>());double? sum=row["sum"] is JsonNode n&&double.TryParse(n.ToString(),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var value)&&double.IsFinite(value)?value:null;historical.Add(new(time,binding.Invert?-sum:sum,sum is null?"unavailable":"good"));}
        return historical.Count>1?new(historical.OrderBy(p=>p.Time).ToArray(),"ha-statistics-sum"):new(ordered,"ha-recorder");
    }
}

public sealed record RecordedHistory(IReadOnlyList<HistoryPoint> Points,string Source);
