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
                ReadInt(values, "charge_state.battery_level", "battery_level"),
                ReadInt(values, "charge_state.usable_battery_level", "usable_battery_level"),
                ReadString(values, "charge_state.charging_state", "charging_state"),
                ReadInt(
                    values,
                    "charge_state.charge_limit_soc",
                    "charge_state.ChargeLimitSoc",
                    "charge_limit_soc",
                    "ChargeLimitSoc"),
                ReadInt(
                    values,
                    "charge_state.charge_current_request",
                    "charge_state.ChargeCurrentRequest",
                    "charge_state.charge_amps",
                    "charge_state.ChargeAmps",
                    "charge_state.charging_amps",
                    "charge_state.charger_actual_current",
                    "charge_state.ChargerActualCurrent",
                    "charge_current_request",
                    "ChargeCurrentRequest",
                    "charge_amps",
                    "ChargeAmps",
                    "charging_amps",
                    "charger_actual_current",
                    "ChargerActualCurrent"),
                ReadInt(
                    values,
                    "charge_state.charge_current_request_max",
                    "charge_state.ChargeCurrentRequestMax",
                    "charge_state.max_charging_amps",
                    "charge_state.MaxChargingAmps",
                    "charge_current_request_max",
                    "ChargeCurrentRequestMax",
                    "max_charging_amps",
                    "MaxChargingAmps"),
                ReadDouble(values, "charge_state.battery_range", "battery_range", "charge_state.est_battery_range", "est_battery_range", "charge_state.ideal_battery_range", "ideal_battery_range"),
                ReadString(values, "charge_state.conn_charge_cable", "conn_charge_cable", "connected_charge_cable"),
                ReadBool(values, "charge_state.fast_charger_present", "fast_charger_present"),
                ReadBool(values, "charge_state.charge_port_door_open", "charge_port_door_open")),
            new LmsTeslaClimateState(
                ReadDouble(values, "climate_state.inside_temp", "inside_temp"),
                ReadDouble(values, "climate_state.outside_temp", "outside_temp"),
                ReadBool(values, "climate_state.is_climate_on", "is_climate_on"),
                ReadDouble(values, "climate_state.driver_temp_setting", "driver_temp_setting"),
                ReadDouble(values, "climate_state.passenger_temp_setting", "passenger_temp_setting")),
            new LmsTeslaDriveState(
                ReadDouble(values, "drive_state.latitude", "latitude", "location_data.latitude"),
                ReadDouble(values, "drive_state.longitude", "longitude", "location_data.longitude"),
                ReadInt(values, "drive_state.heading", "heading"),
                ReadDouble(values, "drive_state.speed", "speed"),
                ReadString(values, "drive_state.shift_state", "shift_state")),
            new LmsTeslaVehicleMetaState(
                ReadString(values, "vehicle_state.car_version", "car_version", "fleet_status.firmware_version", "firmware_version"),
                ReadDouble(values, "vehicle_state.odometer", "odometer"),
                ReadBool(values, "vehicle_state.locked", "locked"),
                ReadBool(values, "vehicle_state.sentry_mode", "sentry_mode")),
            new LmsTeslaFleetKeyState(
                ReadBool(values, "fleet_status.vehicle_command_protocol_required", "vehicle_command_protocol_required", "command_protocol_required"),
                ReadInt(values, "fleet_status.total_number_of_keys", "total_number_of_keys", "total_keys"),
                ReadBool(values, "fleet_status.key_paired", "fleet_status.virtual_key_paired", "key_paired", "virtual_key_paired"),
                ResolveFleetKeyStatus(values)),
            values);
    }

    private static LmsTeslaEnergySiteState MapEnergySite(TeslaEnergySiteSnapshot site)
    {
        var values = site.Values;
        var batteryPercentageValue = ReadDouble(values, "percentage_charged", "battery_percentage");
        var batteryPercentage = batteryPercentageValue.HasValue
            ? (int?)Math.Round(batteryPercentageValue.Value)
            : ReadInt(values, "percentage_charged", "battery_percentage");
        var nameplateEnergyWh = ReadDouble(values, "site_info.nameplate_energy", "nameplate_energy");
        return new LmsTeslaEnergySiteState(
            site.SiteId,
            site.DisplayName,
            site.ResourceType,
            new LmsTeslaEnergySiteCapabilities(
                ReadBool(values, "site_info.components.solar", "components.solar"),
                ReadBool(values, "site_info.components.battery", "components.battery"),
                ReadBool(values, "site_info.components.grid", "components.grid"),
                ReadBool(values, "site_info.components.backup", "components.backup"),
                ReadBool(values, "site_info.components.load_meter", "components.load_meter"),
                ReadString(values, "site_info.components.gateway", "components.gateway"),
                ReadString(values, "site_info.components.battery_type", "components.battery_type"),
                ReadInt(
                    values,
                    "site_info.battery_count",
                    "battery_count",
                    "site_info.powerwall_count",
                    "powerwall_count",
                    "site_info.powerwall_count_on_site",
                    "powerwall_count_on_site"),
                nameplateEnergyWh),
            new LmsTeslaEnergyLiveState(
                ReadString(values, "grid_status"),
                batteryPercentage,
                ReadDouble(values, "solar_power"),
                ReadDouble(values, "load_power"),
                ReadDouble(values, "battery_power"),
                ReadDouble(values, "grid_power"),
                ReadDouble(values, "generator_power"),
                ReadInt(values, "backup_reserve_percent", "backup.backup_reserve_percent", "site_info.backup_reserve_percent", "site_info.backup.backup_reserve_percent"),
                CalculateEnergyRemainingWh(
                    nameplateEnergyWh,
                    batteryPercentageValue ?? batteryPercentage,
                    ReadDouble(values, "energy_left", "total_pack_energy")),
                ReadString(values, "island_status"),
                ReadBool(values, "grid_services_active"),
                ReadBool(values, "storm_mode_active")),
            values);
    }

    private static double? CalculateEnergyRemainingWh(double? nameplateEnergyWh, double? batteryPercentage, double? totalPackEnergyWh)
    {
        if (nameplateEnergyWh.HasValue && batteryPercentage.HasValue)
        {
            return Math.Round(nameplateEnergyWh.Value * batteryPercentage.Value / 100.0);
        }

        return totalPackEnergyWh.HasValue ? Math.Round(totalPackEnergyWh.Value) : null;
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
        FirstNonEmpty(keys.Select(key => TryGetRawValue(values, key, out var value) ? value?.ToString() : null).ToArray());

    private static int? ReadInt(IReadOnlyDictionary<string, object?> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!TryGetRawValue(values, key, out var value) || value is null)
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

            if (value is double doubleValue && doubleValue is <= int.MaxValue and >= int.MinValue)
            {
                return (int)Math.Round(doubleValue);
            }

            if (value is float floatValue && floatValue is <= int.MaxValue and >= int.MinValue)
            {
                return (int)Math.Round(floatValue);
            }

            if (value is decimal decimalValue && decimalValue is <= int.MaxValue and >= int.MinValue)
            {
                return (int)Math.Round(decimalValue);
            }

            if (double.TryParse(value.ToString(), out var doubleParsed) &&
                doubleParsed is <= int.MaxValue and >= int.MinValue)
            {
                return (int)Math.Round(doubleParsed);
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
            if (!TryGetRawValue(values, key, out var value) || value is null)
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
            if (!TryGetRawValue(values, key, out var value) || value is null)
            {
                continue;
            }

            if (value is bool boolValue)
            {
                return boolValue;
            }

            if (value is int intValue)
            {
                return intValue != 0;
            }

            if (value is long longValue)
            {
                return longValue != 0;
            }

            if (bool.TryParse(value.ToString(), out var parsed))
            {
                return parsed;
            }

            var text = value.ToString()?.Trim();
            if (text == "1")
            {
                return true;
            }

            if (text == "0")
            {
                return false;
            }
        }

        return null;
    }

    private static bool TryGetRawValue(
        IReadOnlyDictionary<string, object?> values,
        string requestedKey,
        out object? value)
    {
        if (values.TryGetValue(requestedKey, out value))
        {
            return true;
        }

        var normalizedRequestedKey = NormalizeLookupKey(requestedKey);
        if (normalizedRequestedKey.Length < 3)
        {
            value = null;
            return false;
        }

        foreach (var item in values)
        {
            var normalizedActualKey = NormalizeLookupKey(item.Key);
            if (normalizedActualKey.Equals(normalizedRequestedKey, StringComparison.OrdinalIgnoreCase) ||
                normalizedActualKey.EndsWith(normalizedRequestedKey, StringComparison.OrdinalIgnoreCase))
            {
                value = item.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static string NormalizeLookupKey(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}
