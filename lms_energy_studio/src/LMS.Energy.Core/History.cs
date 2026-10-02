using System.Text.Json.Nodes;
namespace LMS.Energy.Core;
public sealed record HistoryPoint(DateTimeOffset Time, double? Value, string Quality);
public sealed record EnergyInterval(DateTimeOffset Start, DateTimeOffset End, double Kwh, string Source, double? CostMinor, double PricedKwh, double SessionKwh = 0);
public static class History
{
    public static IReadOnlyList<EnergyInterval> Energy(IReadOnlyList<HistoryPoint> points, bool counter, JsonObject? tariff, int maxGapSeconds=300, string scope="home", double? sessionCapKwh=null)
    {
        var result=new List<EnergyInterval>();var usedByDay=new Dictionary<string,double>();
        for(var i=1;i<points.Count;i++)
        {
            var a=points[i-1];var b=points[i];var seconds=(b.Time-a.Time).TotalSeconds;
            if(a.Value is null||b.Value is null||a.Quality!="good"||b.Quality!="good"||seconds<=0||(!counter&&seconds>maxGapSeconds)) continue;
            // A reset cannot establish the time at which the counter restarted: leave that interval uncovered.
            var energy=counter?b.Value.Value-a.Value.Value:Math.Max(0,a.Value.Value)*seconds/3600;
            if(energy<0) continue;
            double cost=0,priced=0,sessionEnergy=0;
            var boundaries=Tariffs.Boundaries(tariff,a.Time,b.Time);
            for(var j=1;j<boundaries.Count;j++) { var start=boundaries[j-1];var end=boundaries[j];var part=energy*(end-start).TotalSeconds/seconds;var rate=Tariffs.Rate(tariff,start,scope);if(rate is not null){
                    var baseRate=Tariffs.Rate(tariff,start,scope,excludeOverrides:true);
                    if(scope=="ev"&&sessionCapKwh is double cap&&rate!=baseRate&&(baseRate is null||rate<baseRate)){
                        var zone=TimeZoneInfo.FindSystemTimeZoneById(tariff?["timezone"]?.ToString()??"Europe/London");var date=TimeZoneInfo.ConvertTime(start,zone).ToString("yyyy-MM-dd");var used=usedByDay.GetValueOrDefault(date);var eligible=Math.Min(part,Math.Max(0,cap-used));usedByDay[date]=used+eligible;sessionEnergy+=eligible;cost+=eligible*rate.Value;priced+=eligible;if(baseRate is not null){cost+=(part-eligible)*baseRate.Value;priced+=part-eligible;}
                    }else{cost+=part*rate.Value;priced+=part;}
                } }
            result.Add(new(a.Time,b.Time,energy,counter?"counter":"power-estimate",priced>0?cost:null,priced,sessionEnergy));
        }
        return result;
    }
}
public static class Tariffs
{
    public static void Validate(JsonObject t)
    {
        if(t["mode"]?.ToString() is not ("fixed" or "dynamic")) throw new ArgumentException("Invalid tariff mode.");
        try{TimeZoneInfo.FindSystemTimeZoneById(t["timezone"]?.ToString()??"Europe/London");}catch(TimeZoneNotFoundException){throw new ArgumentException("Unknown tariff timezone.");}
        foreach(var key in new[]{"peak","offPeak"}) if(t[key] is not null && (!double.TryParse(t[key]!.ToString(),out var rate)||!double.IsFinite(rate))) throw new ArgumentException("Invalid price.");
        if(t["currency"]?.ToString() is not ("GBP" or "EUR" or "USD"))throw new ArgumentException("Invalid tariff currency.");
        foreach(var key in new[]{"windows","sessions","dynamic","intervals"}) if(t[key] is JsonArray entries) { if(entries.Count>3000) throw new ArgumentException("Too many price intervals.");foreach(var e in entries){if(e?["price"] is not null && !double.TryParse(e["price"]!.ToString(),out _))throw new ArgumentException("Invalid interval price.");if(key=="intervals"){if(!DateTimeOffset.TryParse(e?["start"]?.ToString(),out var start)||!DateTimeOffset.TryParse(e?["end"]?.ToString(),out var end)||end<=start)throw new ArgumentException("Invalid UTC interval.");}else {var start=ParseClock(e?["start"]?.ToString());var end=ParseClock(e?["end"]?.ToString());if(start==end||start>=1440||key=="dynamic"&&end<start)throw new ArgumentException("Invalid tariff window.");if(key is "sessions" or "dynamic" && !DateOnly.TryParseExact(e?["date"]?.ToString(),"yyyy-MM-dd",out _))throw new ArgumentException("Invalid tariff date.");}} }
    }
    static int ParseClock(string? clock) { if(clock=="24:00")return 1440;if(TimeOnly.TryParseExact(clock,"HH:mm",out var time))return time.Hour*60+time.Minute;throw new ArgumentException("Invalid clock time."); }
    public static double? Rate(JsonObject? t, DateTimeOffset instant,string scope="home",bool excludeOverrides=false)
    {
        if(t is null)return null;
        var local=TimeZoneInfo.ConvertTime(instant,TimeZoneInfo.FindSystemTimeZoneById(t["timezone"]?.ToString()??"Europe/London"));var minute=local.Hour*60+local.Minute;var date=local.ToString("yyyy-MM-dd");
        if(!excludeOverrides)foreach(var e in (t["intervals"]?.AsArray()??[]).Reverse())
            if(e?["status"]?.ToString()=="confirmed" && (e["scope"]?.ToString()??"home")==scope && instant>=DateTimeOffset.Parse(e["start"]!.ToString())&&instant<DateTimeOffset.Parse(e["end"]!.ToString())) return e["price"]?.GetValue<double>();
        if(!excludeOverrides)foreach(var e in (t["sessions"]?.AsArray()??[]).Reverse())
        { if(e?["status"]?.ToString()=="scheduled"||(e?["scope"]?.ToString()??"home")!=scope)continue;var start=ParseClock(e?["start"]?.ToString());var end=ParseClock(e?["end"]?.ToString());var d=e?["date"]?.ToString();if((date==d&&(start<end?minute>=start&&minute<end:minute>=start))||(start>end&&DateOnly.TryParse(d,out var day)&&date==day.AddDays(1).ToString("yyyy-MM-dd")&&minute<end))return e?["price"]?.GetValue<double>(); }
        if(t["mode"]?.ToString()=="dynamic")
        { foreach(var e in t["dynamic"]?.AsArray()??[])if(e?["date"]?.ToString()==date&&minute>=ParseClock(e["start"]?.ToString())&&minute<ParseClock(e["end"]?.ToString()))return e["price"]?.GetValue<double>();return null; }
        var cheap=(t["windows"]?.AsArray()??[]).Any(e=>{var start=ParseClock(e?["start"]?.ToString());var end=ParseClock(e?["end"]?.ToString());return start<end?minute>=start&&minute<end:minute>=start||minute<end;});
        return t[cheap?"offPeak":"peak"]?.GetValue<double>();
    }
    public static List<DateTimeOffset> Boundaries(JsonObject? t,DateTimeOffset start,DateTimeOffset end)
    {
        // UTC iteration preserves both occurrences of a repeated local hour and skips nonexistent hours.
        var set=new SortedSet<DateTimeOffset>{start,end};
        if(t is not null){for(var at=DateTimeOffset.FromUnixTimeSeconds(start.ToUnixTimeSeconds()/60*60+60);at<end;at=at.AddMinutes(1))set.Add(at);foreach(var e in t["intervals"]?.AsArray()??[])foreach(var key in new[]{"start","end"})if(DateTimeOffset.TryParse(e?[key]?.ToString(),out var time)&&time>start&&time<end)set.Add(time);}
        return set.ToList();
    }
}
