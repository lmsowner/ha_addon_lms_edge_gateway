using System.Text.Json;

sealed class TeslaFleetPropertyHarness(
    TeslaFleetTokenCoordinator tokenCoordinator,
    TeslaFleetDataClient dataClient)
{
    public async Task<TeslaPropertyDiscoveryRun> DiscoverAsync(
        TeslaFleetState state,
        CancellationToken cancellationToken)
    {
        var token = await tokenCoordinator.EnsureUsableAsync(state, cancellationToken);
        var snapshot = await dataClient.FetchSnapshotAsync(token.State, cancellationToken);
        var properties = BuildProperties(snapshot);
        var checks = token.Checks.Concat(snapshot.Checks).ToList();
        checks.Add($"Discovered {properties.Count} sanitized API propert{(properties.Count == 1 ? "y" : "ies")}.");
        return new TeslaPropertyDiscoveryRun(
            token.State,
            snapshot,
            properties,
            checks,
            $"Discovered {properties.Count} Tesla API propert{(properties.Count == 1 ? "y" : "ies")} from {snapshot.Vehicles.Count} vehicle(s) and {snapshot.EnergySites.Count} energy site(s).");
    }

    private static List<TeslaDiscoveredProperty> BuildProperties(TeslaFleetSnapshot snapshot)
    {
        var capturedUtc = snapshot.CapturedUtc;
        var properties = new List<TeslaDiscoveredProperty>();
        if (snapshot.User.HasValue)
        {
            properties.AddRange(BuildJsonProperties(
                "user",
                "Tesla User",
                "current",
                "Current Tesla user",
                snapshot.User.Value,
                capturedUtc));
        }

        if (snapshot.Region.HasValue)
        {
            properties.AddRange(BuildJsonProperties(
                "region",
                "Tesla Region",
                "current",
                "Current Tesla region",
                snapshot.Region.Value,
                capturedUtc));
        }

        foreach (var vehicle in snapshot.Vehicles)
        {
            properties.AddRange(vehicle.Values
                .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => BuildProperty(
                    "vehicle",
                    vehicle.DisplayName,
                    MaskVin(vehicle.Vin),
                    item.Key,
                    item.Value,
                    capturedUtc)));
        }

        foreach (var site in snapshot.EnergySites)
        {
            properties.AddRange(site.Values
                .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => BuildProperty(
                    "energy",
                    site.DisplayName,
                    site.SiteId,
                    item.Key,
                    item.Value,
                    capturedUtc)));
        }

        return properties
            .GroupBy(property => $"{property.Scope}|{property.ResourceId}|{property.Path}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(property => property.Scope, StringComparer.OrdinalIgnoreCase)
            .ThenBy(property => property.ResourceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(property => property.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<TeslaDiscoveredProperty> BuildJsonProperties(
        string scope,
        string resourceName,
        string resourceId,
        string prefix,
        JsonElement root,
        DateTimeOffset capturedUtc)
    {
        var values = new List<TeslaDiscoveredProperty>();
        FlattenJson(root, prefix, values, scope, resourceName, resourceId, capturedUtc);
        return values;
    }

    private static void FlattenJson(
        JsonElement element,
        string path,
        List<TeslaDiscoveredProperty> values,
        string scope,
        string resourceName,
        string resourceId,
        DateTimeOffset capturedUtc)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var childPath = string.IsNullOrWhiteSpace(path) ? property.Name : $"{path}.{property.Name}";
                FlattenJson(property.Value, childPath, values, scope, resourceName, resourceId, capturedUtc);
            }

            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            values.Add(BuildProperty(scope, resourceName, resourceId, path, element.GetRawText(), capturedUtc));
            return;
        }

        values.Add(BuildProperty(scope, resourceName, resourceId, path, ReadJsonValue(element), capturedUtc));
    }

    private static object? ReadJsonValue(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var longValue)
                ? longValue
                : element.TryGetDouble(out var doubleValue) ? doubleValue : element.GetRawText(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            _ => element.GetRawText()
        };

    private static TeslaDiscoveredProperty BuildProperty(
        string scope,
        string resourceName,
        string resourceId,
        string path,
        object? value,
        DateTimeOffset capturedUtc)
    {
        var sanitizedValue = SanitizeValue(path, value);
        var valueType = sanitizedValue switch
        {
            null => "null",
            bool => "boolean",
            byte or short or int or long => "integer",
            float or double or decimal => "number",
            _ => "string"
        };
        var suggestion = SuggestEntity(path, valueType);
        return new TeslaDiscoveredProperty(
            scope,
            resourceName,
            resourceId,
            path,
            HumanizePath(path),
            valueType,
            sanitizedValue?.ToString() ?? string.Empty,
            suggestion.EntityType,
            suggestion.DeviceClass,
            suggestion.Unit,
            capturedUtc);
    }

    private static object? SanitizeValue(string path, object? value)
    {
        if (value is null)
        {
            return null;
        }

        var lowerPath = path.ToLowerInvariant();
        if (lowerPath.Contains("token", StringComparison.Ordinal) ||
            lowerPath.Contains("secret", StringComparison.Ordinal) ||
            lowerPath.Contains("password", StringComparison.Ordinal) ||
            lowerPath.Contains("private", StringComparison.Ordinal) ||
            lowerPath.Contains("private_key", StringComparison.Ordinal) ||
            lowerPath.Contains("public_key", StringComparison.Ordinal) ||
            lowerPath.Equals("key", StringComparison.Ordinal) ||
            lowerPath.EndsWith(".key", StringComparison.Ordinal))
        {
            return "[redacted]";
        }

        var text = value.ToString() ?? string.Empty;
        if (lowerPath.EndsWith("vin", StringComparison.OrdinalIgnoreCase) ||
            lowerPath.Contains(".vin", StringComparison.OrdinalIgnoreCase))
        {
            return MaskVin(text);
        }

        return text.Length <= 180 ? value : $"{text[..180]}...";
    }

    private static TeslaEntitySuggestion SuggestEntity(string path, string valueType)
    {
        var key = path.ToLowerInvariant();
        if (valueType == "boolean")
        {
            return new TeslaEntitySuggestion("binary_sensor", null, null);
        }

        if (key.Contains("battery_level", StringComparison.Ordinal) ||
            key.Contains("charge_limit", StringComparison.Ordinal) ||
            key.Contains("percentage", StringComparison.Ordinal) ||
            key.Contains("percent", StringComparison.Ordinal))
        {
            return new TeslaEntitySuggestion("sensor", "battery", "%");
        }

        if (key.Contains("temp", StringComparison.Ordinal))
        {
            return new TeslaEntitySuggestion("sensor", "temperature", "\u00b0C");
        }

        if (key.Contains("power", StringComparison.Ordinal))
        {
            return new TeslaEntitySuggestion("sensor", "power", "W");
        }

        if (key.Contains("latitude", StringComparison.Ordinal) ||
            key.Contains("longitude", StringComparison.Ordinal))
        {
            return new TeslaEntitySuggestion("device_tracker_hint", null, null);
        }

        return new TeslaEntitySuggestion("sensor", null, null);
    }

    private static string HumanizePath(string path)
    {
        var name = path.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? path;
        return string.Join(' ', name
            .Replace('_', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..]));
    }

    private static string MaskVin(string value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length <= 6)
        {
            return string.IsNullOrWhiteSpace(trimmed) ? "unknown" : trimmed;
        }

        return $"...{trimmed[^6..]}";
    }
}

sealed record TeslaPropertyDiscoveryRun(
    TeslaFleetState State,
    TeslaFleetSnapshot Snapshot,
    List<TeslaDiscoveredProperty> Properties,
    List<string> Checks,
    string Summary);

sealed record TeslaDiscoveredProperty(
    string Scope,
    string ResourceName,
    string ResourceId,
    string Path,
    string DisplayName,
    string ValueType,
    string Value,
    string SuggestedEntityType,
    string? SuggestedDeviceClass,
    string? SuggestedUnit,
    DateTimeOffset LastSeenUtc);

sealed record TeslaEntitySuggestion(
    string EntityType,
    string? DeviceClass,
    string? Unit);
