using System.Globalization;
using System.Text.Json.Nodes;
namespace LMS.Energy.Core;
public sealed record Binding(string EntityId, string? RegistryId = null, string? Pointer = null, string? SourceUnit = null, bool Invert = false, int MaxAgeSeconds = 0);
public sealed record Measurement(double? Value, string Unit, string Quality, string? EntityId = null, DateTimeOffset? ObservedAt = null, DateTimeOffset? MeasuredAt = null, bool? Active = null);
public static class Measurements
{
    public static string Unit(string role) => role switch { "power" => "kW", "energy" => "kWh", "voltage" => "V", "current" => "A", "capacity" => "Ah", "soc" => "%", "temperature" => "°C", "resistance" => "Ω", "price" => "minor/kWh", _ => "boolean" };
    public static JsonNode? Pointer(JsonNode entity, string? path)
    {
        if (string.IsNullOrEmpty(path)) return entity["state"];
        if (!path.StartsWith("/attributes/") || path.Length > 256) throw new ArgumentException("Attribute pointer must start /attributes/.");
        JsonNode? value = entity;
        foreach (var piece in path[1..].Split('/'))
        {
            var key = piece.Replace("~1", "/").Replace("~0", "~");
            if (key is "__proto__" or "constructor" or "prototype") throw new ArgumentException("Invalid pointer.");
            value = value switch { JsonObject o => o[key], JsonArray a when int.TryParse(key, out var i) && i >= 0 && i < a.Count => a[i], _ => null };
        }
        return value;
    }
    public static double? Scale(string role, string? unit) => (role, unit) switch
    {
        ("power", "W") or ("energy", "Wh") or ("voltage", "mV") or ("current", "mA") or ("capacity", "mAh") or ("resistance", "mΩ") => .001,
        ("power", "MW") or ("energy", "MWh") or ("capacity", "kAh") => 1000,
        ("power", "kW") or ("energy", "kWh") or ("voltage", "V") or ("current", "A") or ("capacity", "Ah") or ("soc", "%") or ("resistance", "Ω" or "ohm") or ("temperature", "°C" or "C" or "°F") or ("price", "p/kWh" or "¢/kWh" or "minor/kWh") => 1,
        ("price", "GBP/kWh" or "EUR/kWh" or "USD/kWh") => 100,
        _ => null
    };
    public static Measurement Read(Binding? binding, JsonNode? entity, string role, bool connected, DateTimeOffset now)
    {
        var unit = Unit(role);
        DateTimeOffset? measured = DateTimeOffset.TryParse(entity?["last_reported"]?.ToString() ?? entity?["last_updated"]?.ToString(), out var time) ? time : null;
        Measurement Fail(string quality) => new(null, unit, quality, binding?.EntityId, now, measured);
        if (binding is null) return Fail("unmapped");
        if (!connected) return Fail("disconnected");
        if (entity is null) return Fail("missing");
        if (entity["state"]?.ToString() is "unavailable" or "unknown") return Fail("unavailable");
        if (binding.MaxAgeSeconds > 0 && (measured is null || now - measured > TimeSpan.FromSeconds(binding.MaxAgeSeconds))) return Fail("stale");
        JsonNode? raw;
        try { raw = Pointer(entity, binding.Pointer); } catch (ArgumentException) { return Fail("invalid-pointer"); }
        if (raw is null || raw.ToString().Trim().Length == 0) return Fail("missing-value");
        if (role == "boolean")
        {
            bool? active = raw.ToString().ToLowerInvariant() switch { "true" or "on" or "active" or "balancing" or "1" => true, "false" or "off" or "idle" or "0" => false, _ => null };
            return active is null || binding.Invert ? Fail("invalid-boolean") : new(active.Value ? 1 : 0, unit, "good", binding.EntityId, now, measured, active);
        }
        if (!double.TryParse(raw.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value)) return Fail("not-numeric");
        var source = binding.SourceUnit ?? (string.IsNullOrEmpty(binding.Pointer) ? entity["attributes"]?["unit_of_measurement"]?.ToString() : null);
        var scale = Scale(role, source);
        if (scale is null) return Fail("invalid-unit");
        value = source == "°F" && role == "temperature" ? (value - 32) * 5 / 9 : value * scale.Value;
        if (binding.Invert) value = -value;
        if ((role == "soc" && (value < 0 || value > 100)) || (role is "energy" or "voltage" or "capacity" or "resistance" && value < 0)) return Fail("out-of-range");
        return new(value, unit, "good", binding.EntityId, now, measured);
    }
    public static Measurement Sum(IEnumerable<Measurement> source, string unit, DateTimeOffset now, int maxSkewSeconds = 120)
    {
        var values = source.ToArray();
        if (values.Length == 0 || values.Any(v => v.Value is null || v.Quality is not ("good" or "derived"))) return new(null, unit, "incomplete");
        var times = values.Where(v => v.MeasuredAt.HasValue).Select(v => v.MeasuredAt!.Value).ToArray();
        if (times.Length > 1 && (times.Max() - times.Min()).TotalSeconds > maxSkewSeconds) return new(null, unit, "timestamp-skew");
        return new(values.Sum(v => v.Value!.Value), unit, "derived", ObservedAt: now, MeasuredAt: times.Length > 0 ? times.Min() : now);
    }
}
