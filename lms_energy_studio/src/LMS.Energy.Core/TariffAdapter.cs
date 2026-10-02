using System.Text.Json.Nodes;
namespace LMS.Energy.Core;
// User-selected schema adapter. No entity name or supplier schema is assumed.
public static class TariffAdapter
{
    public static JsonArray Read(JsonObject adapter,JsonObject entity)
    {
        var result=new JsonArray();
        var rows=Measurements.Pointer(entity,adapter["pointer"]?.ToString()) as JsonArray;
        if(rows is null)return result;
        var unit=adapter["sourceUnit"]?.ToString();var scale=Measurements.Scale("price",unit);
        if(scale is null)return result;
        foreach(var row in rows.Take(3000))
        {
            if(row is not JsonObject o)continue;
            var start=o[adapter["startField"]?.ToString()??"start"]?.ToString();var end=o[adapter["endField"]?.ToString()??"end"]?.ToString();
            // Require explicit offsets; supplier local times without an offset are ambiguous on DST days.
            if(start is null||end is null||!(start.EndsWith('Z')||System.Text.RegularExpressions.Regex.IsMatch(start,"[+-][0-9]{2}:[0-9]{2}$"))||!(end.EndsWith('Z')||System.Text.RegularExpressions.Regex.IsMatch(end,"[+-][0-9]{2}:[0-9]{2}$")))continue;
            if(!DateTimeOffset.TryParse(start,out var a)||!DateTimeOffset.TryParse(end,out var b)||b<=a)continue;
            if(!double.TryParse(o[adapter["priceField"]?.ToString()??"price"]?.ToString(),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var price)||!double.IsFinite(price))continue;
            var status=adapter["statusField"]?.ToString() is {Length:>0} field?o[field]?.ToString():adapter["status"]?.ToString();
            if(status is not ("confirmed" or "scheduled" or "applied"))continue;
            result.Add(new JsonObject{["start"]=a.ToString("O"),["end"]=b.ToString("O"),["price"]=price*scale.Value,["status"]=status=="applied"?"confirmed":status,["scope"]=adapter["scope"]?.ToString()??"unspecified",["source"]=adapter["entityId"]?.ToString()});
        }
        return result;
    }
}
