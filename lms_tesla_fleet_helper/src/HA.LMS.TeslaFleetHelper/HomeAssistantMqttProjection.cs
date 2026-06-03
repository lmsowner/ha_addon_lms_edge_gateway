sealed record HomeAssistantMqttProjection(
    IReadOnlyList<HomeAssistantMqttDeviceProjection> Devices,
    IReadOnlyList<HomeAssistantMqttEntityProjection> Entities,
    IReadOnlyList<HomeAssistantMqttStateProjection> States);

sealed record HomeAssistantProjectionPreviewRun(
    TeslaFleetState State,
    List<HomeAssistantProjectionPreviewEntity> Entities,
    List<string> Checks,
    string Summary);

sealed record HomeAssistantProjectionPreviewEntity(
    string Id,
    string DeviceName,
    string Component,
    string Name,
    string StateTopic,
    string ValueTemplate,
    string? DeviceClass,
    string? UnitOfMeasurement,
    bool EnabledByDefault);

sealed record HomeAssistantMqttDeviceProjection(
    string Id,
    string Name,
    string Manufacturer,
    string Model,
    string? SoftwareVersion);

sealed record HomeAssistantMqttEntityProjection(
    string Id,
    string DeviceId,
    string Component,
    string Name,
    string StateTopic,
    string ValueTemplate,
    string? DeviceClass,
    string? UnitOfMeasurement,
    bool EnabledByDefault = true,
    string? EntityCategory = null,
    string? StateClass = null,
    string? Icon = null,
    IReadOnlyDictionary<string, object?>? ExtraConfig = null);

sealed record HomeAssistantMqttStateProjection(
    string Topic,
    IReadOnlyDictionary<string, object?> Payload);

sealed record HomeAssistantRawPropertyProjection(
    string PayloadKey,
    string EntityId,
    string Name,
    string Path,
    object Value,
    string Component,
    string? DeviceClass,
    string? UnitOfMeasurement,
    string? StateClass,
    string? Icon);

sealed record HomeAssistantEntitySuggestion(
    string Component,
    string? DeviceClass,
    string? UnitOfMeasurement,
    string? StateClass,
    string? Icon);

sealed class HomeAssistantMqttProjectionMapper
{
    public HomeAssistantMqttProjection Map(LmsTeslaFleetState state, string baseTopic)
    {
        var devices = new List<HomeAssistantMqttDeviceProjection>();
        var entities = new List<HomeAssistantMqttEntityProjection>();
        var states = new List<HomeAssistantMqttStateProjection>();
        var normalizedBaseTopic = NormalizeTopic(baseTopic);
        const string helperDeviceId = "lms_tesla_fleet_helper";
        var helperStateTopic = $"{normalizedBaseTopic}/helper/state";

        devices.Add(new HomeAssistantMqttDeviceProjection(
            helperDeviceId,
            "LMS Tesla Fleet Helper",
            "Linux Made Sane",
            "Tesla Fleet Helper",
            null));
        entities.AddRange(BuildHelperEntities(helperDeviceId, helperStateTopic));
        states.Add(new HomeAssistantMqttStateProjection(helperStateTopic, BuildHelperPayload(state)));

        foreach (var vehicle in state.Vehicles)
        {
            var deviceId = $"lms_tesla_{SafeId(vehicle.Vin)}";
            var stateTopic = $"{normalizedBaseTopic}/vehicles/{SafeTopic(vehicle.Vin)}/state";
            devices.Add(new HomeAssistantMqttDeviceProjection(
                deviceId,
                vehicle.DisplayName,
                "Tesla",
                "Tesla Vehicle",
                vehicle.Meta.FirmwareVersion));
            entities.AddRange(BuildVehicleEntities(deviceId, stateTopic));
            entities.AddRange(BuildRawEntities(deviceId, stateTopic, vehicle.RawProperties));
            states.Add(new HomeAssistantMqttStateProjection(stateTopic, BuildVehiclePayload(vehicle)));
        }

        foreach (var site in state.EnergySites)
        {
            var deviceId = $"lms_tesla_energy_{SafeId(site.SiteId)}";
            var stateTopic = $"{normalizedBaseTopic}/energy/{SafeTopic(site.SiteId)}/state";
            devices.Add(new HomeAssistantMqttDeviceProjection(
                deviceId,
                ResolveEnergyDeviceName(site),
                "Tesla",
                ResolveEnergyModel(site),
                null));
            entities.AddRange(BuildEnergyEntities(deviceId, stateTopic));
            entities.AddRange(BuildRawEntities(deviceId, stateTopic, site.RawProperties));
            states.Add(new HomeAssistantMqttStateProjection(stateTopic, BuildEnergyPayload(site)));
        }

        return new HomeAssistantMqttProjection(devices, entities, states);
    }

    private static IEnumerable<HomeAssistantMqttEntityProjection> BuildHelperEntities(string deviceId, string stateTopic) =>
    [
        Sensor(deviceId, "status", "Status", stateTopic, "{{ value_json.status }}"),
        Sensor(deviceId, "vehicle_count", "Vehicle Count", stateTopic, "{{ value_json.vehicle_count }}", stateClass: "measurement"),
        Sensor(deviceId, "energy_site_count", "Energy Site Count", stateTopic, "{{ value_json.energy_site_count }}", stateClass: "measurement"),
        Sensor(deviceId, "region", "Region", stateTopic, "{{ value_json.region }}"),
        Sensor(deviceId, "last_snapshot", "Last Snapshot", stateTopic, "{{ value_json.last_snapshot }}", "timestamp")
    ];

    private static IEnumerable<HomeAssistantMqttEntityProjection> BuildVehicleEntities(string deviceId, string stateTopic) =>
    [
        Sensor(deviceId, "state", "State", stateTopic, "{{ value_json.state }}"),
        BinarySensor(deviceId, "online", "Online", stateTopic, "{{ (value_json.state == 'online') | lower }}", "connectivity"),
        Sensor(deviceId, "battery_level", "Battery", stateTopic, "{{ value_json.battery_level }}", "battery", "%", stateClass: "measurement"),
        Sensor(deviceId, "usable_battery_level", "Usable Battery", stateTopic, "{{ value_json.usable_battery_level }}", "battery", "%", stateClass: "measurement"),
        Sensor(deviceId, "charging_state", "Charging State", stateTopic, "{{ value_json.charging_state }}"),
        Sensor(deviceId, "charge_limit", "Charge Limit", stateTopic, "{{ value_json.charge_limit }}", "battery", "%", stateClass: "measurement"),
        Sensor(deviceId, "battery_range", "Battery Range", stateTopic, "{{ value_json.battery_range }}", "distance", "mi", stateClass: "measurement"),
        Sensor(deviceId, "connected_charge_cable", "Connected Charge Cable", stateTopic, "{{ value_json.connected_charge_cable }}"),
        BinarySensor(deviceId, "fast_charger_present", "Fast Charger Present", stateTopic, "{{ value_json.fast_charger_present | lower }}"),
        BinarySensor(deviceId, "charge_port_door_open", "Charge Port Door Open", stateTopic, "{{ value_json.charge_port_door_open | lower }}", "opening"),
        Sensor(deviceId, "inside_temp", "Inside Temperature", stateTopic, "{{ value_json.inside_temp }}", "temperature", "\u00b0C", stateClass: "measurement"),
        Sensor(deviceId, "outside_temp", "Outside Temperature", stateTopic, "{{ value_json.outside_temp }}", "temperature", "\u00b0C", stateClass: "measurement"),
        BinarySensor(deviceId, "climate_on", "Climate On", stateTopic, "{{ value_json.climate_on | lower }}"),
        Sensor(deviceId, "driver_temp_setting", "Driver Temperature Setting", stateTopic, "{{ value_json.driver_temp_setting }}", "temperature", "\u00b0C", stateClass: "measurement"),
        Sensor(deviceId, "passenger_temp_setting", "Passenger Temperature Setting", stateTopic, "{{ value_json.passenger_temp_setting }}", "temperature", "\u00b0C", stateClass: "measurement"),
        DeviceTracker(deviceId, "location", "Location", stateTopic),
        Sensor(deviceId, "latitude", "Latitude", stateTopic, "{{ value_json.latitude }}", enabledByDefault: false),
        Sensor(deviceId, "longitude", "Longitude", stateTopic, "{{ value_json.longitude }}", enabledByDefault: false),
        Sensor(deviceId, "heading", "Heading", stateTopic, "{{ value_json.heading }}", unitOfMeasurement: "\u00b0", stateClass: "measurement"),
        Sensor(deviceId, "speed", "Speed", stateTopic, "{{ value_json.speed }}", "speed", "mph", stateClass: "measurement"),
        Sensor(deviceId, "shift_state", "Shift State", stateTopic, "{{ value_json.shift_state }}"),
        Sensor(deviceId, "firmware", "Firmware", stateTopic, "{{ value_json.firmware }}"),
        Sensor(deviceId, "odometer", "Odometer", stateTopic, "{{ value_json.odometer }}", "distance", "mi", stateClass: "total_increasing"),
        BinarySensor(deviceId, "locked", "Locked", stateTopic, "{{ value_json.locked | lower }}"),
        BinarySensor(deviceId, "sentry_mode", "Sentry Mode", stateTopic, "{{ value_json.sentry_mode | lower }}"),
        Sensor(deviceId, "fleet_key_status", "Fleet Key Status", stateTopic, "{{ value_json.fleet_key_status }}"),
        BinarySensor(deviceId, "fleet_key_paired", "Fleet Key Paired", stateTopic, "{{ value_json.fleet_key_paired | lower }}", "connectivity"),
        BinarySensor(deviceId, "vehicle_command_protocol_required", "Signed Commands Required", stateTopic, "{{ value_json.vehicle_command_protocol_required | lower }}"),
        Sensor(deviceId, "total_keys", "Total Keys", stateTopic, "{{ value_json.total_keys }}", stateClass: "measurement")
    ];

    private static IEnumerable<HomeAssistantMqttEntityProjection> BuildEnergyEntities(string deviceId, string stateTopic) =>
    [
        Sensor(deviceId, "display_name", "Display Name", stateTopic, "{{ value_json.display_name }}"),
        Sensor(deviceId, "site_id", "Site ID", stateTopic, "{{ value_json.site_id }}", enabledByDefault: false, entityCategory: "diagnostic"),
        Sensor(deviceId, "resource_type", "Resource Type", stateTopic, "{{ value_json.resource_type }}"),
        Sensor(deviceId, "grid_status", "Grid Status", stateTopic, "{{ value_json.grid_status }}"),
        Sensor(deviceId, "battery_percentage", "Battery", stateTopic, "{{ value_json.battery_percentage }}", "battery", "%", stateClass: "measurement"),
        Sensor(deviceId, "solar_power", "Solar Power", stateTopic, "{{ value_json.solar_power }}", "power", "W", stateClass: "measurement"),
        Sensor(deviceId, "load_power", "Load Power", stateTopic, "{{ value_json.load_power }}", "power", "W", stateClass: "measurement"),
        Sensor(deviceId, "battery_power", "Battery Power", stateTopic, "{{ value_json.battery_power }}", "power", "W", stateClass: "measurement"),
        Sensor(deviceId, "grid_power", "Grid Power", stateTopic, "{{ value_json.grid_power }}", "power", "W", stateClass: "measurement"),
        Sensor(deviceId, "backup_reserve", "Backup Reserve", stateTopic, "{{ value_json.backup_reserve }}", "battery", "%", stateClass: "measurement")
    ];

    private static IEnumerable<HomeAssistantMqttEntityProjection> BuildRawEntities(
        string deviceId,
        string stateTopic,
        IReadOnlyDictionary<string, object?> rawProperties) =>
        BuildRawPropertyProjections(rawProperties).Select(raw =>
            raw.Component.Equals("binary_sensor", StringComparison.OrdinalIgnoreCase)
                ? BinarySensor(
                    deviceId,
                    raw.EntityId,
                    raw.Name,
                    stateTopic,
                    $"{{{{ value_json.{raw.PayloadKey} | lower }}}}",
                    raw.DeviceClass,
                    enabledByDefault: false,
                    entityCategory: "diagnostic",
                    icon: raw.Icon)
                : Sensor(
                    deviceId,
                    raw.EntityId,
                    raw.Name,
                    stateTopic,
                    $"{{{{ value_json.{raw.PayloadKey} }}}}",
                    raw.DeviceClass,
                    raw.UnitOfMeasurement,
                    enabledByDefault: false,
                    entityCategory: "diagnostic",
                    stateClass: raw.StateClass,
                    icon: raw.Icon));

    private static IReadOnlyDictionary<string, object?> BuildVehiclePayload(LmsTeslaVehicleState vehicle)
    {
        var payload = new Dictionary<string, object?>
        {
            ["vin"] = vehicle.Vin,
            ["display_name"] = vehicle.DisplayName,
            ["state"] = vehicle.ConnectivityState,
            ["battery_level"] = vehicle.Charge.BatteryLevelPercent,
            ["usable_battery_level"] = vehicle.Charge.UsableBatteryLevelPercent,
            ["charging_state"] = vehicle.Charge.ChargingState,
            ["charge_limit"] = vehicle.Charge.ChargeLimitPercent,
            ["battery_range"] = vehicle.Charge.BatteryRange,
            ["connected_charge_cable"] = vehicle.Charge.ConnectedChargeCable,
            ["fast_charger_present"] = vehicle.Charge.FastChargerPresent,
            ["charge_port_door_open"] = vehicle.Charge.ChargePortDoorOpen,
            ["inside_temp"] = vehicle.Climate.InsideTemperatureCelsius,
            ["outside_temp"] = vehicle.Climate.OutsideTemperatureCelsius,
            ["climate_on"] = vehicle.Climate.IsClimateOn,
            ["driver_temp_setting"] = vehicle.Climate.DriverTemperatureSettingCelsius,
            ["passenger_temp_setting"] = vehicle.Climate.PassengerTemperatureSettingCelsius,
            ["latitude"] = vehicle.Drive.Latitude,
            ["longitude"] = vehicle.Drive.Longitude,
            ["heading"] = vehicle.Drive.Heading,
            ["speed"] = vehicle.Drive.Speed,
            ["shift_state"] = vehicle.Drive.ShiftState,
            ["firmware"] = vehicle.Meta.FirmwareVersion,
            ["odometer"] = vehicle.Meta.Odometer,
            ["locked"] = vehicle.Meta.Locked,
            ["sentry_mode"] = vehicle.Meta.SentryMode,
            ["fleet_key_status"] = vehicle.FleetKey.Status,
            ["vehicle_command_protocol_required"] = vehicle.FleetKey.VehicleCommandProtocolRequired,
            ["fleet_key_paired"] = vehicle.FleetKey.KeyPaired,
            ["total_keys"] = vehicle.FleetKey.TotalNumberOfKeys
        };
        AddRawPayload(payload, vehicle.RawProperties);
        return payload;
    }

    private static IReadOnlyDictionary<string, object?> BuildEnergyPayload(LmsTeslaEnergySiteState site)
    {
        var payload = new Dictionary<string, object?>
        {
            ["site_id"] = site.SiteId,
            ["display_name"] = site.DisplayName,
            ["resource_type"] = site.ResourceType,
            ["grid_status"] = site.Live.GridStatus,
            ["battery_percentage"] = site.Live.BatteryPercentage,
            ["solar_power"] = site.Live.SolarPowerWatts,
            ["load_power"] = site.Live.LoadPowerWatts,
            ["battery_power"] = site.Live.BatteryPowerWatts,
            ["grid_power"] = site.Live.GridPowerWatts,
            ["backup_reserve"] = site.Live.BackupReservePercent
        };
        AddRawPayload(payload, site.RawProperties);
        return payload;
    }

    private static IReadOnlyDictionary<string, object?> BuildHelperPayload(LmsTeslaFleetState state) =>
        new Dictionary<string, object?>
        {
            ["status"] = "online",
            ["vehicle_count"] = state.Vehicles.Count,
            ["energy_site_count"] = state.EnergySites.Count,
            ["region"] = state.User.Region,
            ["fleet_api_audience"] = state.User.FleetApiAudience,
            ["last_snapshot"] = state.CapturedUtc
        };

    private static HomeAssistantMqttEntityProjection Sensor(
        string deviceId,
        string id,
        string name,
        string stateTopic,
        string valueTemplate,
        string? deviceClass = null,
        string? unitOfMeasurement = null,
        bool enabledByDefault = true,
        string? entityCategory = null,
        string? stateClass = null,
        string? icon = null) =>
        new(
            $"{deviceId}_{id}",
            deviceId,
            "sensor",
            name,
            stateTopic,
            valueTemplate,
            deviceClass,
            unitOfMeasurement,
            enabledByDefault,
            entityCategory,
            stateClass,
            icon);

    private static HomeAssistantMqttEntityProjection BinarySensor(
        string deviceId,
        string id,
        string name,
        string stateTopic,
        string valueTemplate,
        string? deviceClass = null,
        bool enabledByDefault = true,
        string? entityCategory = null,
        string? icon = null) =>
        new(
            $"{deviceId}_{id}",
            deviceId,
            "binary_sensor",
            name,
            stateTopic,
            valueTemplate,
            deviceClass,
            null,
            enabledByDefault,
            entityCategory,
            null,
            icon,
            new Dictionary<string, object?>
            {
                ["payload_on"] = "true",
                ["payload_off"] = "false"
            });

    private static HomeAssistantMqttEntityProjection DeviceTracker(
        string deviceId,
        string id,
        string name,
        string stateTopic) =>
        new(
            $"{deviceId}_{id}",
            deviceId,
            "device_tracker",
            name,
            stateTopic,
            "{{ value_json.state }}",
            null,
            null,
            true,
            null,
            null,
            null,
            new Dictionary<string, object?>
            {
                ["json_attributes_topic"] = stateTopic,
                ["source_type"] = "gps"
            });

    private static string ResolveEnergyDeviceName(LmsTeslaEnergySiteState site)
    {
        var displayName = string.IsNullOrWhiteSpace(site.DisplayName)
            ? $"Energy Site {site.SiteId}"
            : site.DisplayName.Trim();
        if (displayName.Contains("powerwall", StringComparison.OrdinalIgnoreCase) ||
            displayName.Contains("energy", StringComparison.OrdinalIgnoreCase))
        {
            return displayName;
        }

        return IsPowerwallLike(site)
            ? $"Tesla Powerwall - {displayName}"
            : $"Tesla Energy - {displayName}";
    }

    private static string ResolveEnergyModel(LmsTeslaEnergySiteState site) =>
        IsPowerwallLike(site) ? "Tesla Powerwall / Energy Site" : "Tesla Energy Site";

    private static bool IsPowerwallLike(LmsTeslaEnergySiteState site) =>
        site.ResourceType.Contains("battery", StringComparison.OrdinalIgnoreCase) ||
        site.RawProperties.Keys.Any(key =>
            key.Contains("battery", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("backup_reserve", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("percentage_charged", StringComparison.OrdinalIgnoreCase));

    private static void AddRawPayload(
        Dictionary<string, object?> payload,
        IReadOnlyDictionary<string, object?> rawProperties)
    {
        foreach (var raw in BuildRawPropertyProjections(rawProperties))
        {
            payload[raw.PayloadKey] = raw.Value;
        }
    }

    private static List<HomeAssistantRawPropertyProjection> BuildRawPropertyProjections(
        IReadOnlyDictionary<string, object?> rawProperties) =>
        rawProperties
            .Where(item => IsPublishableRawProperty(item.Key, item.Value))
            .Select(item =>
            {
                var value = NormalizeRawValue(item.Value!);
                var suggestion = SuggestRawEntity(item.Key, value);
                var entityId = $"raw_{SafeId(item.Key)}";
                return new HomeAssistantRawPropertyProjection(
                    entityId,
                    entityId,
                    $"Raw {HumanizePath(item.Key)}",
                    item.Key,
                    value,
                    suggestion.Component,
                    suggestion.DeviceClass,
                    suggestion.UnitOfMeasurement,
                    suggestion.StateClass,
                    suggestion.Icon);
            })
            .GroupBy(property => property.PayloadKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsPublishableRawProperty(string path, object? value)
    {
        if (value is null || IsSensitivePath(path))
        {
            return false;
        }

        if (IsNumeric(value) || value is bool)
        {
            return true;
        }

        var text = value.ToString()?.Trim() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(text) &&
               text.Length <= 180 &&
               !text.StartsWith('{') &&
               !text.StartsWith('[');
    }

    private static bool IsSensitivePath(string path)
    {
        var key = path.ToLowerInvariant();
        return key.Contains("token", StringComparison.Ordinal) ||
               key.Contains("secret", StringComparison.Ordinal) ||
               key.Contains("password", StringComparison.Ordinal) ||
               key.Contains("private_key", StringComparison.Ordinal) ||
               key.Contains("public_key", StringComparison.Ordinal);
    }

    private static object NormalizeRawValue(object value) =>
        value is string text ? text.Trim() : value;

    private static HomeAssistantEntitySuggestion SuggestRawEntity(string path, object value)
    {
        var key = path.ToLowerInvariant();
        if (value is bool)
        {
            return new HomeAssistantEntitySuggestion(
                "binary_sensor",
                key.Contains("online", StringComparison.Ordinal) ||
                key.Contains("connected", StringComparison.Ordinal) ||
                key.Contains("connectivity", StringComparison.Ordinal)
                    ? "connectivity"
                    : key.Contains("door", StringComparison.Ordinal) || key.Contains("open", StringComparison.Ordinal)
                        ? "opening"
                        : null,
                null,
                null,
                null);
        }

        if (IsNumeric(value))
        {
            if (IsIdentifierPath(key))
            {
                return new HomeAssistantEntitySuggestion("sensor", null, null, null, null);
            }

            if (key.Contains("battery_level", StringComparison.Ordinal) ||
                key.Contains("charge_limit", StringComparison.Ordinal) ||
                key.Contains("percentage", StringComparison.Ordinal) ||
                key.Contains("percent", StringComparison.Ordinal) ||
                key.EndsWith("_soc", StringComparison.Ordinal))
            {
                return new HomeAssistantEntitySuggestion("sensor", "battery", "%", "measurement", null);
            }

            if (key.Contains("temp", StringComparison.Ordinal))
            {
                return new HomeAssistantEntitySuggestion("sensor", "temperature", "\u00b0C", "measurement", null);
            }

            if (key.Contains("power", StringComparison.Ordinal))
            {
                return new HomeAssistantEntitySuggestion("sensor", "power", "W", "measurement", null);
            }

            if (key.Contains("voltage", StringComparison.Ordinal) || key.Contains("volt", StringComparison.Ordinal))
            {
                return new HomeAssistantEntitySuggestion("sensor", "voltage", "V", "measurement", null);
            }

            if (key.Contains("current", StringComparison.Ordinal) ||
                key.Contains("amperage", StringComparison.Ordinal) ||
                key.Contains("amps", StringComparison.Ordinal))
            {
                return new HomeAssistantEntitySuggestion("sensor", "current", "A", "measurement", null);
            }

            if (key.Contains("energy", StringComparison.Ordinal))
            {
                return new HomeAssistantEntitySuggestion("sensor", "energy", "kWh", "measurement", null);
            }

            if (key.Contains("range", StringComparison.Ordinal) ||
                key.Contains("distance", StringComparison.Ordinal))
            {
                return new HomeAssistantEntitySuggestion("sensor", "distance", "mi", "measurement", null);
            }

            if (key.Contains("odometer", StringComparison.Ordinal))
            {
                return new HomeAssistantEntitySuggestion("sensor", "distance", "mi", "total_increasing", null);
            }

            if (key.Contains("speed", StringComparison.Ordinal))
            {
                return new HomeAssistantEntitySuggestion("sensor", "speed", "mph", "measurement", null);
            }
        }

        if (key.Contains("time", StringComparison.Ordinal) ||
            key.Contains("date", StringComparison.Ordinal) ||
            key.Contains("timestamp", StringComparison.Ordinal))
        {
            return new HomeAssistantEntitySuggestion("sensor", "timestamp", null, null, null);
        }

        return new HomeAssistantEntitySuggestion("sensor", null, null, null, null);
    }

    private static bool IsIdentifierPath(string key) =>
        key.Equals("id", StringComparison.Ordinal) ||
        key.Equals("id_s", StringComparison.Ordinal) ||
        key.EndsWith("_id", StringComparison.Ordinal) ||
        key.EndsWith(".id", StringComparison.Ordinal) ||
        key.Contains("_id.", StringComparison.Ordinal) ||
        key.Contains(".id_", StringComparison.Ordinal) ||
        key.Contains("vehicle_id", StringComparison.Ordinal) ||
        key.Contains("site_id", StringComparison.Ordinal);

    private static bool IsNumeric(object value) =>
        value is byte or short or int or long or float or double or decimal;

    private static string HumanizePath(string path) =>
        string.Join(' ', path
            .Replace('.', ' ')
            .Replace('_', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..]));

    private static string NormalizeTopic(string value)
    {
        var topic = (value ?? string.Empty).Trim().Trim('/');
        return string.IsNullOrWhiteSpace(topic) ? "lms/tesla-fleet" : topic;
    }

    private static string SafeTopic(string value) =>
        string.Join('_', (value ?? string.Empty).Trim().Split([' ', '/', '\\', '#', '+', ':', ';'], StringSplitOptions.RemoveEmptyEntries));

    private static string SafeId(string value)
    {
        var chars = (value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .ToArray();
        var normalized = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(normalized) ? "value" : normalized;
    }
}
