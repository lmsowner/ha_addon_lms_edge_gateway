using System.Text.Json.Nodes;
using LMS.Energy.Core;
using Xunit;
namespace LMS.Energy.Core.Tests;
public class EnergyTests
{
    static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-10-02T12:00:00Z");
    static JsonObject Entity(string state="1200",string unit="W")=>new(){["entity_id"]="sensor.test",["state"]=state,["last_updated"]=Now.ToString("O"),["attributes"]=new JsonObject{["unit_of_measurement"]=unit,["cells"]=new JsonArray(new JsonObject{["voltage"]=3300})}};
    [Theory]
    [InlineData("W",1200,1.2,"power")][InlineData("MW",.01,10,"power")][InlineData("Wh",2000,2,"energy")][InlineData("mA",-800,-.8,"current")][InlineData("°F",86,30,"temperature")][InlineData("GBP/kWh",-.025,-2.5,"price")]
    public void ConvertsAtBoundary(string unit,double raw,double expected,string role)
    { Assert.Equal(expected,Measurements.Read(new("sensor.test"),Entity(raw.ToString(System.Globalization.CultureInfo.InvariantCulture),unit),role,true,Now).Value!.Value,6); }
    [Theory][InlineData("unavailable","unavailable")][InlineData("unknown","unavailable")][InlineData("","missing-value")][InlineData("NaN","not-numeric")]
    public void MissingNeverBecomesZero(string raw,string quality)
    {var m=Measurements.Read(new("sensor.test"),Entity(raw),"power",true,Now);Assert.Null(m.Value);Assert.Equal(quality,m.Quality);}
    [Fact]public void AttributeArraysRequireTheirOwnUnit()
    {var e=Entity();Assert.Equal(3.3,Measurements.Read(new("sensor.test",Pointer:"/attributes/cells/0/voltage",SourceUnit:"mV"),e,"voltage",true,Now).Value!.Value,6);Assert.Equal("invalid-unit",Measurements.Read(new("sensor.test",Pointer:"/attributes/cells/0/voltage"),e,"voltage",true,Now).Quality);}
    [Fact]public void UsesReportedTimeAndDisconnectQuality()
    {var e=Entity();e["last_updated"]=Now.AddDays(-5).ToString("O");e["last_reported"]=Now.ToString("O");Assert.Equal("good",Measurements.Read(new("sensor.test",MaxAgeSeconds:60),e,"power",true,Now).Quality);Assert.Equal("stale",Measurements.Read(new("sensor.test",MaxAgeSeconds:60),e,"power",true,Now.AddMinutes(2)).Quality);Assert.Null(Measurements.Read(new("sensor.test"),e,"power",false,Now).Value);}
    [Fact]public void SignedReadingsAndSkewAreExplicit()
    {Assert.Equal(-1.2,Measurements.Read(new("sensor.test",Invert:true),Entity(),"power",true,Now).Value);var a=new Measurement(1,"kW","good",MeasuredAt:Now);Assert.Null(Measurements.Sum([a,a with{MeasuredAt=Now.AddMinutes(-10)}],"kW",Now).Value);Assert.Equal(0,Measurements.Sum([a,a with{Value=-1}],"kW",Now).Value);}
    static JsonObject Tariff(string mode="fixed")=>JsonNode.Parse("""{"currency":"GBP","mode":"fixed","timezone":"Europe/London","peak":30,"offPeak":7.5,"windows":[{"start":"23:30","end":"05:30"}],"dynamic":[],"sessions":[]}""")!.AsObject().Also(t=>t["mode"]=mode);
    [Fact]public void CounterResetsAndPowerOutagesAreNotBridged()
    {var p=new[]{new HistoryPoint(Now,10,"good"),new HistoryPoint(Now.AddHours(1),12,"good"),new HistoryPoint(Now.AddHours(2),1,"good"),new HistoryPoint(Now.AddHours(3),2,"good")};var r=History.Energy(p,true,null);Assert.Equal(3,r.Sum(x=>x.Kwh));Assert.Empty(History.Energy(p,false,null));Assert.Single(History.Energy([new(Now,1,"good"),new(Now.AddMinutes(2),1,"good"),new(Now.AddMinutes(3),null,"unavailable"),new(Now.AddMinutes(4),1,"good")],false,null));}
    [Fact]public void MidnightWindowsAndBothDstTransitions()
    {var t=Tariff();Assert.Equal(7.5,Tariffs.Rate(t,DateTimeOffset.Parse("2026-10-01T23:00:00Z")));Assert.Equal(30,Tariffs.Rate(t,DateTimeOffset.Parse("2026-10-02T05:00:00Z")));Assert.Equal(7.5,Tariffs.Rate(t,DateTimeOffset.Parse("2026-10-25T00:30:00Z")));Assert.Equal(7.5,Tariffs.Rate(t,DateTimeOffset.Parse("2026-10-25T01:30:00Z")));var spring=History.Energy([new(DateTimeOffset.Parse("2026-03-29T00:00:00Z"),0,"good"),new(DateTimeOffset.Parse("2026-03-29T05:00:00Z"),5,"good")],true,t);Assert.Equal(48.75,spring[0].CostMinor!.Value,6);}
    [Fact]public void DynamicMissingNegativeAndEvSessions()
    {var t=Tariff("dynamic");t["intervals"]=JsonNode.Parse("""[{"start":"2026-10-02T12:00:00Z","end":"2026-10-02T12:30:00Z","price":-2.5,"scope":"home","status":"confirmed"},{"start":"2026-10-02T12:30:00Z","end":"2026-10-02T13:00:00Z","price":1,"scope":"ev","status":"confirmed"}]""");var r=History.Energy([new(Now,0,"good"),new(Now.AddHours(1),2,"good")],true,t);Assert.Equal(1,r[0].PricedKwh,6);Assert.Equal(-2.5,r[0].CostMinor!.Value,6);Assert.Null(Tariffs.Rate(t,Now.AddMinutes(45)));Assert.Equal(1,Tariffs.Rate(t,Now.AddMinutes(45),"ev"));}
    [Fact]public void SupplierSchemaAndConfirmationAreSelected()
    {var e=Entity();e["attributes"]!["rates"]=JsonNode.Parse("""[{"from":"2026-10-02T12:00:00Z","to":"2026-10-02T13:00:00Z","rate":-0.02,"stage":"applied"},{"from":"2026-10-25T01:00:00","to":"2026-10-25T02:00:00","rate":0.1,"stage":"confirmed"}]""");var adapter=JsonNode.Parse("""{"entityId":"sensor.test","pointer":"/attributes/rates","startField":"from","endField":"to","priceField":"rate","statusField":"stage","scope":"ev","sourceUnit":"GBP/kWh"}""")!.AsObject();var result=TariffAdapter.Read(adapter,e);Assert.Single(result);Assert.Equal(-2,result[0]!["price"]!.GetValue<double>());Assert.Equal("ev",result[0]!["scope"]!.ToString());}
    [Fact]public void EvCapRemainsSeparateFromWholeHome()
    {var t=Tariff();t["intervals"]=JsonNode.Parse("""[{"start":"2026-10-02T12:00:00Z","end":"2026-10-02T13:00:00Z","price":5,"scope":"ev","status":"confirmed"}]""");var points=new[]{new HistoryPoint(Now,0,"good"),new HistoryPoint(Now.AddHours(1),4,"good")};var ev=History.Energy(points,true,t,scope:"ev",sessionCapKwh:2);Assert.Equal(70,ev[0].CostMinor!.Value,6);Assert.Equal(2,ev[0].SessionKwh,6);Assert.Equal(120,History.Energy(points,true,t)[0].CostMinor!.Value,6);}
    [Fact]public void RejectsUnitsUnknownTargetsAndNetPlusSplit()
    {var catalogue=new Dictionary<string,JsonObject>{{"sensor.test",Entity()}};var update=new ConfigurationUpdate(new(),[],new(){["home"]=new("sensor.test")},0);Configuration.Validate(update,catalogue);Assert.Throws<ArgumentException>(()=>Configuration.Validate(update with{Mappings=new(){["home"]=new("sensor.test",SourceUnit:"kWh")}},catalogue));Assert.Throws<ArgumentException>(()=>Configuration.Validate(update with{Mappings=new(){["arbitrary"]=new("sensor.test")}},catalogue));Assert.Throws<ArgumentException>(()=>Configuration.Validate(update with{Mappings=new(){["grid"]=new("sensor.test"),["grid.import"]=new("sensor.test")}},catalogue));}
}
static class Helpers{public static T Also<T>(this T value,Action<T> action){action(value);return value;}}
