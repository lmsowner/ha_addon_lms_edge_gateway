using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HA.LMS.EnergyStudio;
using LMS.Energy.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace LMS.Energy.Core.Tests;
public class ConnectionTests
{
    [Fact]public async Task BootstrapOverlayRegistryRenameReconnectAndPersistence()
    {
        var listener=new System.Net.Sockets.TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
        var builder=WebApplication.CreateBuilder();builder.WebHost.UseUrls($"http://127.0.0.1:{port}");var app=builder.Build();app.UseWebSockets();
        var sessions=0;var abort=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var now=DateTimeOffset.UtcNow;
        JsonObject State(string id,string value)=>new(){["entity_id"]=id,["state"]=value,["last_updated"]=now.ToString("O"),["attributes"]=new JsonObject{["unit_of_measurement"]="W",["access_token"]="never-return"}};
        app.Map("/api/websocket",async context=>
        {
            using var ws=await context.WebSockets.AcceptWebSocketAsync();var session=Interlocked.Increment(ref sessions);var name=session==1?"sensor.meter":"sensor.renamed";
            async Task Send(JsonObject o)=>await ws.SendAsync(Encoding.UTF8.GetBytes(o.ToJsonString()),WebSocketMessageType.Text,true,context.RequestAborted);
            await Send(new(){["type"]="auth_required"});var buffer=new byte[16384];var auth=await ws.ReceiveAsync(buffer,context.RequestAborted);Assert.Equal("fixture-token",JsonNode.Parse(buffer.AsSpan(0,auth.Count))!["access_token"]!.ToString());await Send(new(){["type"]="auth_ok"});
            try{while(ws.State==WebSocketState.Open)
            {
                var read=await ws.ReceiveAsync(buffer,context.RequestAborted);if(read.MessageType==WebSocketMessageType.Close)break;var request=JsonNode.Parse(buffer.AsSpan(0,read.Count))!;var id=request["id"]!.GetValue<int>();var type=request["type"]!.ToString();JsonNode? result=null;
                if(type=="get_states")
                {
                    await Send(new(){["id"]=1,["type"]="event",["event"]=new JsonObject{["event_type"]="state_changed",["data"]=new JsonObject{["entity_id"]=name,["new_state"]=State(name,"2300")}}});var energy=State("sensor.energy","100");energy["attributes"]!["unit_of_measurement"]="kWh";var snapshotStates=new JsonArray(State(name,"1000"),energy);foreach(var (sensor,value) in new[]{("home","4000"),("grid","5000"),("inv","3000"),("dc","1000"),("ac","1000"),("solar","4000"),("ev","2000")})snapshotStates.Add(State("sensor."+sensor,value));result=snapshotStates;
                }
                if(type=="recorder/statistics_during_period") {Assert.Equal("kWh",request["units"]!["energy"]!.ToString());result=new JsonObject{["sensor.energy"]=new JsonArray(new JsonObject{["end"]=now.ToUnixTimeMilliseconds(),["sum"]=10},new JsonObject{["end"]=now.AddHours(1).ToUnixTimeMilliseconds(),["sum"]=12})};}
                if(type=="config/entity_registry/list")result=new JsonArray(new JsonObject{["id"]="stable-registry-id",["entity_id"]=name,["device_id"]="device-1"});
                if(type=="config/device_registry/list")result=new JsonArray(new JsonObject{["id"]="device-1",["name"]="Meter",["area_id"]="area-1"});
                if(type=="config/area_registry/list")result=new JsonArray(new JsonObject{["area_id"]="area-1",["name"]="Utility"});
                await Send(new(){["id"]=id,["type"]=type=="ping"?"pong":"result",["success"]=true,["result"]=result});
                if(type=="config/area_registry/list"&&session==1){await abort.Task.WaitAsync(context.RequestAborted);await ws.CloseAsync(WebSocketCloseStatus.NormalClosure,"fixture reconnect",context.RequestAborted);break;}
            }}catch(OperationCanceledException){}catch(WebSocketException){}
        });
        app.MapGet("/api/history/period/{time}",()=>Results.Json(new[]{Array.Empty<object>()}));
        await app.StartAsync();var dir=Path.Combine(Path.GetTempPath(),"energy-test-"+Guid.NewGuid());
        var settings=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["EnergyStudio:HomeAssistantUrl"]=$"http://127.0.0.1:{port}",["EnergyStudio:HomeAssistantToken"]="fixture-token",["EnergyStudio:DataRoot"]=dir}).Build();
        var store=new ConfigurationStore(settings);var ha=new HomeAssistantConnection(settings,store,NullLogger<HomeAssistantConnection>.Instance);
        try
        {
            await ha.StartAsync(default);await Until(()=>ha.Connected);var catalogue=ha.Catalogue();Assert.Equal("2300",catalogue["sensor.meter"]["state"]!.ToString());Assert.Equal("Utility",catalogue["sensor.meter"]["area_name"]!.ToString());Assert.Null(catalogue["sensor.meter"]["attributes"]!["access_token"]);
            await store.Save(new(new(),[],new(){["home"]=new("sensor.meter"),["grid.importEnergy"]=new("sensor.energy")},0),catalogue,default);
            await Assert.ThrowsAsync<RevisionConflictException>(()=>store.Save(new(new(),[],[],0),catalogue,default));Assert.Equal("stable-registry-id",new ConfigurationStore(settings).Get().Mappings["home"].RegistryId);
            abort.TrySetResult();await Until(()=>sessions>=2&&ha.Connected&&ha.Catalogue().ContainsKey("sensor.renamed"));Assert.Equal("sensor.renamed",ha.ResolvedConfiguration().Mappings["home"].EntityId);
            var recorded=await ha.History("grid.importEnergy",now,now.AddHours(1),default);Assert.Equal("ha-statistics-sum",recorded.Source);Assert.Equal(2,History.Energy(recorded.Points,true,null).Sum(i=>i.Kwh));
            var snapshot=JsonSerializer.SerializeToNode(ha.Snapshot(),ConfigurationStore.Json)!;Assert.Equal(2.3,snapshot["readings"]!["home"]!["value"]!.GetValue<double>(),6);
            var layout=JsonNode.Parse("""{"cars":1,"chargers":1,"batteries":2,"arrays":1,"vehicles":[{"id":"car-one","v2g":false,"charger":0}],"chargerV2G":[false],"inverters":[{"id":"inv-one"}],"arrayRoutes":[{"id":"roof","inverter":"inv-one"}],"storage":[{"id":"dc-bank","connection":"inverter","inverter":"inv-one"},{"id":"ac-bank","connection":"ac"}]}""")!.AsObject();
            var mappings=new Dictionary<string,Binding>{["home"]=new("sensor.home"),["grid"]=new("sensor.grid"),["inverters.inv-one"]=new("sensor.inv"),["arrays.roof"]=new("sensor.solar"),["batteries.dc-bank.power"]=new("sensor.dc"),["batteries.ac-bank.power"]=new("sensor.ac"),["ev.car-one"]=new("sensor.ev")};
            await store.Save(new(layout,[],mappings,1),ha.Catalogue(),default);snapshot=JsonSerializer.SerializeToNode(ha.Snapshot(),ConfigurationStore.Json)!;
            Assert.Equal(3,snapshot["residual"]!["value"]!.GetValue<double>()); // DC arrays/storage are already represented at the inverter's AC output.
            Assert.Equal(3,snapshot["readings"]!["inverters.inv-one"]!["value"]!.GetValue<double>());
            mappings["ev.car-one"]=new("sensor.ev",Invert:true);await store.Save(new(layout,[],mappings,2),ha.Catalogue(),default);snapshot=JsonSerializer.SerializeToNode(ha.Snapshot(),ConfigurationStore.Json)!;Assert.Null(snapshot["readings"]!["ev.car-one"]!["value"]);Assert.Equal("unsupported-v2g",snapshot["readings"]!["ev.car-one"]!["quality"]!.ToString());
            layout["vehicles"]![0]!["v2g"]=true;layout["chargerV2G"]![0]=true;await store.Save(new(layout,[],mappings,3),ha.Catalogue(),default);snapshot=JsonSerializer.SerializeToNode(ha.Snapshot(),ConfigurationStore.Json)!;Assert.Equal(-2,snapshot["readings"]!["ev.car-one"]!["value"]!.GetValue<double>());
        }
        finally{abort.TrySetResult();await ha.StopAsync(default);await app.StopAsync();await app.DisposeAsync();if(Directory.Exists(dir))Directory.Delete(dir,true);}
    }
    static async Task Until(Func<bool> condition){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));while(!condition())await Task.Delay(20,timeout.Token);}
}
