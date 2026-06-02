sealed class TeslaFleetStateMapper
{
    public LmsTeslaFleetState Map(TeslaFleetSnapshot snapshot, string fleetApiAudience)
    {
        var vehicles = snapshot.Vehicles
            .Select(MapVehicle)
            .OrderBy(vehicle => vehicle.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var energySites = snapshot.EnergySites
            .Select(MapEnergySite)
            .OrderBy(site => site.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var properties = vehicles
            .SelectMany(vehicle => BuildProperties("vehicle", vehicle.Vin, vehicle.DisplayName, vehicle.RawProperties))
            .Concat(energySites.SelectMany(site => BuildProperties("energy", site.SiteId, site.DisplayName, site.RawProperties)))
            .OrderBy(property => property.Scope, StringComparer.OrdinalIgnoreCase)
            .ThenBy(property => property.ResourceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(property => property.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new LmsTeslaFleetState(
            snapshot.CapturedUtc,
            new LmsTeslaUserState(ResolveRegion(fleetApiAudience), fleetApiAudience),
            vehicles,
            energySites,
            properties);
    }

    private static LmsTeslaVehicleState MapVehicle(TeslaVehicleSnapshot vehicle)
    {
        var values = vehicle.Values;
        return new LmsTeslaVehicleState(
            vehicle.Vin,
            vehicle.Id,
            vehicle.DisplayName,
            FirstNonEmpty(vehicle.State, "unknown"),
            new LmsTeslaChargeState(
                ReadInt(values, "charge_state.battery_level"),
                ReadInt(values, "charge_state.usable_battery_level"),
                ReadString(values, "charge_state.charging_state"),
                ReadInt(values, "charge_state.charge_limit_soc"),
                ReadDouble(values, "charge_state.battery_range", "charge_state.est_battery_range", "charge_state.ideal_battery_range"),
                ReadString(values, "charge_state.conn_charge_cable"),
                ReadBool(values, "charge_state.fast_charger_present"),
                ReadBool(values, "charge_state.charge_port_door_open")),
            new LmsTeslaClimateState(
                ReadDouble(values, "climate_state.inside_temp"),
                ReadDouble(values, "climate_state.outside_temp"),
                ReadBool(values, "climate_state.is_climate_on"),
                ReadDouble(values, "climate_state.driver_temp_setting"),
                ReadDouble(values, "climate_state.passenger_temp_setting")),
            new LmsTeslaDriveState(
                ReadDouble(values, "drive_state.latitude", "location_data.latitude"),
                ReadDouble(values, "drive_state.longitude", "location_data.longitude"),
                ReadInt(values, "drive_state.heading"),
                ReadDouble(values, "drive_state.speed"),
                ReadString(values, "drive_state.shift_state")),
            new LmsTeslaVehicleMetaState(
                ReadString(values, "vehicle_state.car_version", "fleet_status.firmware_version"),
                ReadDouble(values, "vehicle_state.odometer"),
                ReadBool(values, "vehicle_state.locked"),
                ReadBool(values, "vehicle_state.sentry_mode")),
            new LmsTeslaFleetKeyState(
                ReadBool(values, "fleet_status.vehicle_command_protocol_required"),
                ReadInt(values, "fleet_status.total_number_of_keys"),
                ReadBool(values, "fleet_status.key_paired", "fleet_status.virtual_key_paired"),
                ResolveFleetKeyStatus(values)),
            values);
    }

    private static LmsTeslaEnergySiteState MapEnergySite(TeslaEnergySiteSnapshot site)
    {
        var values = site.Values;
        return new LmsTeslaEnergySiteState(
            site.SiteId,
            site.DisplayName,
            site.ResourceType,
            new LmsTeslaEnergyLiveState(
                ReadString(values, "grid_status"),
                ReadInt(values, "percentage_charged", "battery_percentage"),
                ReadDouble(values, "solar_power"),
                ReadDouble(values, "load_power"),
                ReadDouble(values, "battery_power"),
                ReadDouble(values, "grid_power"),
                ReadInt(values, "backup_reserve_percent")),
            values);
    }

    private static IEnumerable<LmsTeslaApiProperty> BuildProperties(
        string scope,
        string resourceId,
        string resourceName,
        IReadOnlyDictionary<string, object?> values) =>
        values.Select(item =>
        {
            var valueType = item.Value switch
            {
                null => "null",
                bool => "boolean",
                byte or short or int or long => "integer",
                float or double or decimal => "number",
                _ => "string"
            };
            var suggestion = SuggestEntity(item.Key, valueType);
            return new LmsTeslaApiProperty(
                scope,
                resourceId,
                resourceName,
                item.Key,
                valueType,
                item.Value,
                suggestion.EntityType,
                suggestion.DeviceClass,
                suggestion.Unit);
        });

    private static string ResolveFleetKeyStatus(IReadOnlyDictionary<string, object?> values)
    {
        var required = ReadBool(values, "fleet_status.vehicle_command_protocol_required");
        var paired = ReadBool(values, "fleet_status.key_paired", "fleet_status.virtual_key_paired");
        return (required, paired) switch
        {
            (true, true) => "signed_commands_ready",
            (true, false) => "virtual_key_required",
            (false, _) => "legacy_or_not_required",
            _ => "unknown"
        };
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

        return new TeslaEntitySuggestion("sensor", null, null);
    }

    private static string ResolveRegion(string fleetApiAudience)
    {
        var value = fleetApiAudience.ToLowerInvariant();
        if (value.Contains(".prd.eu.", StringComparison.Ordinal))
        {
            return "EU";
        }

        if (value.Contains(".prd.cn.", StringComparison.Ordinal))
        {
            return "CN";
        }

        return "NA";
    }

    private static string ReadString(IReadOnlyDictionary<string, object?> values, params string[] keys) =>
        FirstNonEmpty(keys.Select(key => values.TryGetValue(key, out var value) ? value?.ToString() : null).ToArray());

    private static int? ReadInt(IReadOnlyDictionary<string, object?> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!values.TryGetValue(key, out var value) || value is null)
            {
                continue;
            }

            if (value is int intValue)
            {
                return intValue;
            }

            if (value is long longValue && longValue is <= int.MaxValue and >= int.MinValue)
            {
                return (int)longValue;
            }

            if (int.TryParse(value.ToString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static double? ReadDouble(IReadOnlyDictionary<string, object?> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!values.TryGetValue(key, out var value) || value is null)
            {
                continue;
            }

            if (value is double doubleValue)
            {
                return doubleValue;
            }

            if (value is float floatValue)
            {
                return floatValue;
            }

            if (value is decimal decimalValue)
            {
                return (double)decimalValue;
            }

            if (double.TryParse(value.ToString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static bool? ReadBool(IReadOnlyDictionary<string, object?> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!values.TryGetValue(key, out var value) || value is null)
            {
                continue;
            }

            if (value is bool boolValue)
            {
                return boolValue;
            }

            if (bool.TryParse(value.ToString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}
