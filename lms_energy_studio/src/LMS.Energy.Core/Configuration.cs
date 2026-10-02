using System.Text.Json;
using System.Text.Json.Nodes;
namespace LMS.Energy.Core;
public sealed record EnergyConfiguration(int Revision, JsonObject Layout, Dictionary<string, string[]> Cells, Dictionary<string, Binding> Mappings);
public sealed record ConfigurationUpdate(JsonObject Layout, Dictionary<string, string[]> Cells, Dictionary<string, Binding> Mappings, int? ExpectedRevision = null);
public static class Configuration
{
    public static Dictionary<string, string> Fields(JsonObject layout, Dictionary<string, string[]> cells)
    {
        var fields = new Dictionary<string, string> { ["home"]="power", ["grid"]="power", ["grid.import"]="power", ["grid.export"]="power", ["solar"]="power", ["battery"]="power", ["grid.importEnergy"]="energy", ["grid.exportEnergy"]="energy", ["tariff.currentPrice"]="price" };
        foreach (var (group, prefix) in new[] { ("loads","loads"), ("arrayRoutes","arrays"), ("sources","sources"), ("inverters","inverters") })
            foreach (var v in layout[group]?.AsArray() ?? []) { var id=v!["id"]!.ToString(); fields[$"{prefix}.{id}"]="power"; if(group=="loads") fields[$"loadEnergy.{id}"]="energy"; }
        foreach (var v in (layout["vehicles"]?.AsArray() ?? []).Take(layout["cars"]?.GetValue<int>() ?? 0))
        { var id=v!["id"]!.ToString(); foreach(var (prefix,role) in new[]{("ev","power"),("evSoc","soc"),("evEnergy","energy"),("evExportEnergy","energy")}) fields[$"{prefix}.{id}"]=role; }
        for(var i=0;i<(layout["chargers"]?.GetValue<int>()??0);i++) fields[$"chargers.charger-{i+1}"]="power";
        foreach(var v in layout["storage"]?.AsArray() ?? [])
        {
            var id=v!["id"]!.ToString();
            foreach(var (field,role) in new[]{("power","power"),("charge","power"),("discharge","power"),("soc","soc"),("voltage","voltage"),("nominalVoltage","voltage"),("ampHours","capacity"),("capacity","energy"),("current","current"),("temperature","temperature"),("chargeEnergy","energy"),("dischargeEnergy","energy")}) fields[$"batteries.{id}.{field}"]=role;
            foreach(var route in v["balanceRoutes"]?.AsArray()??[]) { var r=route!["id"]!.ToString(); fields[$"routes.{id}.{r}.active"]="boolean";fields[$"routes.{id}.{r}.current"]="current"; }
            foreach(var cell in cells.GetValueOrDefault(id) ?? []) foreach(var (field,role) in new[]{("voltage","voltage"),("soc","soc"),("balancing","boolean"),("balanceCurrent","current"),("balanceResistance","resistance")}) fields[$"cells.{id}.{cell}.{field}"]=role;
        }
        return fields;
    }
    public static void Validate(ConfigurationUpdate update, IReadOnlyDictionary<string, JsonObject> catalogue)
    {
        if(update.Mappings.Count>4096)throw new ArgumentException("Too many mappings.");
        var allowed = "name house housePhoto panels arrays rating batteries soc chargers chargerNames chargerV2G cars vehicles loads demand charge motion inverters arrayRoutes storage sources installedKW topologyVersion tariff accounting".Split(' ').ToHashSet();
        if(update.Layout.Any(p=>!allowed.Contains(p.Key))) throw new ArgumentException("Unexpected layout property.");
        foreach(var (key,max) in new[]{("cars",4),("chargers",4),("arrays",4),("batteries",6)}) if(update.Layout[key] is JsonNode n && (!int.TryParse(n.ToString(),out var count)||count<0||count>max)) throw new ArgumentException("Invalid layout count.");
        foreach(var group in new[]{"vehicles","loads","inverters","arrayRoutes","storage","sources"})
        {
            var ids = new HashSet<string>();
            if(update.Layout[group] is not JsonArray entries) continue;
            if(entries.Count>64) throw new ArgumentException("Too many devices.");
            foreach(var entry in entries) { var id=entry?["id"]?.ToString(); if(id is null || !System.Text.RegularExpressions.Regex.IsMatch(id,"^[a-zA-Z0-9_-]{1,80}$") || !ids.Add(id)) throw new ArgumentException("Devices need unique stable IDs."); }
        }
        if(update.Cells.Count>6||update.Cells.Any(p=>p.Value.Length>512||p.Value.Distinct().Count()!=p.Value.Length||p.Value.Any(id=>!System.Text.RegularExpressions.Regex.IsMatch(id,"^[a-zA-Z0-9_-]{1,80}$")))) throw new ArgumentException("Invalid cell IDs.");
        foreach(var pack in update.Layout["storage"]?.AsArray()??[]) {
            var id=pack!["id"]!.ToString();var routes=pack["balanceRoutes"] as JsonArray??[];var ids=new HashSet<string>();
            foreach(var route in routes)if(route is null||route["id"]?.ToString() is not {Length:>0} rid||!ids.Add(rid)||!(update.Cells.GetValueOrDefault(id)??[]).Contains(route["from"]?.ToString())||!(update.Cells.GetValueOrDefault(id)??[]).Contains(route["to"]?.ToString())||route["from"]?.ToString()==route["to"]?.ToString())throw new ArgumentException("Reported balancing routes require distinct configured cell endpoints and unique IDs.");
        }
        var storage=(update.Layout["storage"]?.AsArray()??[]).Select(v=>v!["id"]!.ToString()).ToHashSet();
        if(update.Cells.Keys.Any(id=>!storage.Contains(id))) throw new ArgumentException("Cell pack is not in layout.");
        if(update.Layout["accounting"]?["toleranceKw"] is JsonNode tolerance && (!double.TryParse(tolerance.ToString(),out var limit)||limit<0||limit>10))throw new ArgumentException("Invalid meter tolerance.");
        RejectSecrets(update.Layout);
        var fields=Fields(update.Layout,update.Cells);
        foreach(var (key,b) in update.Mappings)
        {
            if(!fields.TryGetValue(key,out var role)||b.MaxAgeSeconds<0||b.MaxAgeSeconds>604800) throw new ArgumentException("Invalid mapping target or freshness policy.");
            if(!catalogue.TryGetValue(b.EntityId,out var entity)) throw new ArgumentException($"Entity {b.EntityId} is missing; refresh the catalogue.");
            if(b.RegistryId is not null && b.RegistryId!=entity["registry_id"]?.ToString()) throw new ArgumentException("Registry identity mismatch.");
            if(!string.IsNullOrEmpty(b.Pointer)) { Measurements.Pointer(entity,b.Pointer); if(b.SourceUnit is null && role!="boolean") throw new ArgumentException("Attribute mappings require a source unit."); }
            if(role=="boolean" ? b.Invert : Measurements.Scale(role,b.SourceUnit??entity["attributes"]?["unit_of_measurement"]?.ToString()) is null) throw new ArgumentException("Incompatible unit or sign transform.");
        }
        var acKeys=(update.Layout["inverters"]?.AsArray()??[]).Select(v=>$"inverters.{v!["id"]}")
            .Concat((update.Layout["sources"]?.AsArray()??[]).Where(v=>v?["connection"]?.ToString()=="ac").Select(v=>$"sources.{v!["id"]}"))
            .Concat((update.Layout["storage"]?.AsArray()??[]).Where(v=>v?["connection"]?.ToString()!="inverter").Select(v=>$"batteries.{v!["id"]}.power"));
        var used=new HashSet<string>();foreach(var key in acKeys)if(update.Mappings.TryGetValue(key,out var binding)&&!used.Add(binding.EntityId+"|"+binding.Pointer))throw new ArgumentException("An AC contributor is mapped twice; this would double-count energy.");
        foreach(var prefix in new[]{"grid"}.Concat(storage.Select(id=>$"batteries.{id}")))
        { var net=prefix=="grid"?prefix:prefix+".power"; var pos=prefix+(prefix=="grid"?".import":".discharge");var neg=prefix+(prefix=="grid"?".export":".charge"); if(update.Mappings.ContainsKey(net)&&(update.Mappings.ContainsKey(pos)||update.Mappings.ContainsKey(neg))) throw new ArgumentException("Select net OR split directional readings."); }
        if(update.Layout["tariff"] is JsonObject tariff) {
            Tariffs.Validate(tariff);
            if(tariff["evBilling"] is JsonObject billing)foreach(var (id,rule) in billing){if(!(update.Layout["vehicles"]?.AsArray()??[]).Any(v=>v?["id"]?.ToString()==id)||rule is not JsonObject)throw new ArgumentException("EV billing needs a configured vehicle.");if(rule?["sessionCapKwh"] is JsonNode cap&&(!double.TryParse(cap.ToString(),out var kwh)||!double.IsFinite(kwh)||kwh<0))throw new ArgumentException("Invalid EV daily session cap.");}
            if(tariff["adapters"] is JsonArray adapters) foreach(var a in adapters) {
                if(a is not JsonObject adapter || !catalogue.TryGetValue(adapter["entityId"]?.ToString()??"",out var source))throw new ArgumentException("Select an existing tariff entity.");
                Measurements.Pointer(source,adapter["pointer"]?.ToString());
                if(Measurements.Scale("price",adapter["sourceUnit"]?.ToString()) is null || adapter["scope"]?.ToString() is not ("home" or "ev") || adapter["status"]?.ToString() is not ("confirmed" or "scheduled") && adapter["statusField"] is null)throw new ArgumentException("Tariff adapter needs units, explicit scope and confirmation policy.");
            }
        }
    }
    static void RejectSecrets(JsonNode node)
    {
        if(node is JsonObject o) foreach(var (key,value) in o) { if(key.Contains("token",StringComparison.OrdinalIgnoreCase)||key.Contains("password",StringComparison.OrdinalIgnoreCase)||key.Contains("url",StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Credentials and URLs are not configuration fields.");if(value is not null) RejectSecrets(value); }
        else if(node is JsonArray a) foreach(var value in a) if(value is not null) RejectSecrets(value);
        else if(node is JsonValue v && v.TryGetValue<string>(out var s) && (s.StartsWith("http:")||s.StartsWith("https:")||s.StartsWith("javascript:"))) throw new ArgumentException("Use bundled assets or uploaded data images.");
    }
}
