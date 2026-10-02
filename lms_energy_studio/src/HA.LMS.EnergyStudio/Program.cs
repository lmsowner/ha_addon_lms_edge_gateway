using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HA.LMS.EnergyStudio;
using LMS.Energy.Core;
var builder=WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("/data/options.json",optional:true,reloadOnChange:false);
builder.Services.ConfigureHttpJsonOptions(o=>{o.SerializerOptions.UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow;o.SerializerOptions.RespectRequiredConstructorParameters=true;});
builder.Services.AddSingleton<ConfigurationStore>();builder.Services.AddSingleton<HomeAssistantConnection>();builder.Services.AddHostedService(s=>s.GetRequiredService<HomeAssistantConnection>());
builder.WebHost.ConfigureKestrel(o=>o.Limits.MaxRequestBodySize=12*1024*1024);
var app=builder.Build();
app.Use(async(context,next)=>
{
    var remote=context.Connection.RemoteIpAddress;var ingress=remote?.MapToIPv4().ToString()=="172.30.32.2";
    var proxyIp=builder.Configuration["EnergyStudio:EdgeGatewayProxyIp"]??builder.Configuration["edge_gateway_proxy_ip"];
    var edge=IPAddress.TryParse(proxyIp,out var configuredProxy)&&remote?.MapToIPv4().Equals(configuredProxy.MapToIPv4())==true&&!string.IsNullOrWhiteSpace(context.Request.Headers["X-LMS-User"]);
    var local=app.Environment.IsDevelopment()&&remote is not null&&IPAddress.IsLoopback(remote);
    var secret=builder.Configuration["EnergyStudio:AccessToken"];
    var presented=context.Request.Headers.Authorization.ToString();
    var authorized=secret is {Length:>=32} && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(presented)),SHA256.HashData(Encoding.UTF8.GetBytes("Bearer "+secret)));
    if(context.Request.Path=="/healthz"){await next(context);return;}
    if(!ingress&&!edge&&!local&&!authorized){context.Response.StatusCode=401;return;}
    if(context.Request.Method is not ("GET" or "HEAD"))
    {
        var origin=context.Request.Headers.Origin.ToString();
        if(context.Request.Headers["X-Energy-Studio"].ToString()!="1" || (origin.Length>0&&(!Uri.TryCreate(origin,UriKind.Absolute,out var uri)||uri.Authority!=((ingress||edge)&&context.Request.Headers["X-Forwarded-Host"].ToString() is {Length:>0} forwardedHost?forwardedHost:context.Request.Host.Value)))){context.Response.StatusCode=403;return;}
    }
    var publicPrefix=(builder.Configuration["EnergyStudio:PublicPathPrefix"]??builder.Configuration["public_path_prefix"]??"").TrimEnd('/');
    if(publicPrefix.Length>0&&context.Request.Path.StartsWithSegments(publicPrefix,out var remainder)){
        if(!remainder.HasValue){context.Response.Redirect(context.Request.Path+"/");return;}
        context.Request.PathBase=publicPrefix;context.Request.Path=remainder;
    }
    // Only Supervisor's ingress peer can set the ingress path. External proxies must strip their prefix.
    if((ingress||edge)&&context.Request.Headers["X-Ingress-Path"].ToString() is {Length:>0} prefix&&prefix.StartsWith('/')&&!prefix.Contains(".."))context.Request.PathBase=prefix.TrimEnd('/');
    context.Response.Headers["X-Content-Type-Options"]="nosniff";context.Response.Headers.CacheControl="no-store";
    try{await next(context);}catch(RevisionConflictException){context.Response.StatusCode=409;await context.Response.WriteAsJsonAsync(new{error="Configuration changed; reload before saving."});}catch(ArgumentException ex){context.Response.StatusCode=400;await context.Response.WriteAsJsonAsync(new{error=ex.Message});}catch(IOException){context.Response.StatusCode=503;await context.Response.WriteAsJsonAsync(new{error="Home Assistant data is unavailable."});}
});
app.UseRouting();
app.UseDefaultFiles();app.UseStaticFiles();
app.MapGet("/healthz",()=>Results.Ok(new{status="ok",version=builder.Configuration["EnergyStudio:AddonVersion"]??"2026.10.02.18.44"}));
app.MapGet("/api/energy/entities",(HomeAssistantConnection ha)=>ha.Catalogue().Values);
app.MapGet("/api/energy/config",(HomeAssistantConnection ha)=>ha.ResolvedConfiguration());
app.MapPut("/api/energy/config",async(ConfigurationUpdate update,ConfigurationStore store,HomeAssistantConnection ha,CancellationToken ct)=>await store.Save(update,ha.Catalogue(),ct));
app.MapGet("/api/energy/snapshot",(HomeAssistantConnection ha)=>ha.Snapshot());
app.MapGet("/api/energy/live",async(HttpContext context,HomeAssistantConnection ha)=>
{
    context.Response.ContentType="text/event-stream";context.Response.Headers["X-Accel-Buffering"]="no";
    try{while(!context.RequestAborted.IsCancellationRequested){await context.Response.WriteAsync("data: "+JsonSerializer.Serialize(ha.Snapshot(),ConfigurationStore.Json)+"\n\n",context.RequestAborted);await context.Response.Body.FlushAsync(context.RequestAborted);await Task.Delay(2000,context.RequestAborted);}}catch(OperationCanceledException){}
});
app.MapGet("/api/energy/history",async(string key,DateTimeOffset start,DateTimeOffset end,HomeAssistantConnection ha,ConfigurationStore store,CancellationToken ct)=>
{
    if(end<=start||end-start>TimeSpan.FromDays(7))throw new ArgumentException("History bounds must cover at most seven days.");
    var recorded=await ha.History(key,start,end,ct);var points=recorded.Points;var c=store.Get();var role=Configuration.Fields(c.Layout,c.Cells)[key];var binding=c.Mappings[key];
    var evId=key.StartsWith("evEnergy.")?key["evEnergy.".Length..]:null;var evBilling=evId is null?null:c.Layout["tariff"]?["evBilling"]?[evId];var separatelyPriced=evBilling?["enabled"]?.GetValue<bool>()==true;
    var cap=separatelyPriced?evBilling?["sessionCapKwh"]?.GetValue<double>():null;
    if(cap is not null){var zone=TimeZoneInfo.FindSystemTimeZoneById(c.Layout["tariff"]?["timezone"]?.ToString()??"Europe/London");if(TimeZoneInfo.ConvertTime(start,zone).TimeOfDay!=TimeSpan.Zero)throw new ArgumentException("EV session cap estimates must start at local midnight to track daily eligibility.");}
    var intervals=role is "energy" or "power"?LMS.Energy.Core.History.Energy(points,role=="energy",key=="grid.importEnergy"||key=="grid.import"||key=="grid"||separatelyPriced?ha.Tariff():null,binding.MaxAgeSeconds>0?binding.MaxAgeSeconds:300,separatelyPriced?"ev":"home",cap).Where(i=>i.Start>=start&&i.End<=end).ToArray():[];
    return Results.Ok(new{key,start,end,unit=Measurements.Unit(role),source=recorded.Source,points,intervals,totalKwh=intervals.Sum(i=>i.Kwh),pricedKwh=intervals.Sum(i=>i.PricedKwh),costMinor=intervals.Any(i=>i.CostMinor.HasValue)?intervals.Sum(i=>i.CostMinor??0):(double?)null,billingScope=separatelyPriced?"ev-separate-estimate":"grid-import-only",sessionKwh=intervals.Sum(i=>i.SessionKwh),costAllocation="Counter energy apportioned by elapsed time across tariff boundaries; interval costs are estimates.",coverageSeconds=intervals.Sum(i=>(i.End-i.Start).TotalSeconds)});
});
app.MapGet("/api/energy/tariffs",(HomeAssistantConnection ha)=>ha.Tariff());
app.Run();
public partial class Program;
