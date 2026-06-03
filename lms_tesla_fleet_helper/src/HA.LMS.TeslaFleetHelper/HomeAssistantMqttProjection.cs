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
    bool EnabledByDefault = true);

sealed record HomeAssistantMqttStateProjection(
    string Topic,
    IReadOnlyDictionary<string, object?> Payload);

sealed class HomeAssistantMqttProjectionMapper
{
    public HomeAssistantMqttProjection Map(LmsTeslaFleetState state, string baseTopic)
    {
        var devices = new List<HomeAssistantMqttDeviceProjection>();
        var entities = new List<HomeAssistantMqttEntityProjection>();
        var states = new List<HomeAssistantMqttStateProjection>();

        foreach (var vehicle in state.Vehicles)
        {
            var deviceId = $"lms_tesla_{SafeId(vehicle.Vin)}";
            var stateTopic = $"{NormalizeTopic(baseTopic)}/vehicles/{SafeTopic(vehicle.Vin)}/state";
            devices.Add(new HomeAssistantMqttDeviceProjection(
                deviceId,
                vehicle.DisplayName,
                "Tesla",
                "Tesla Vehicle",
                vehicle.Meta.FirmwareVersion));
            entities.AddRange(BuildVehicleEntities(deviceId, stateTopic));
            states.Add(new HomeAssistantMqttStateProjection(stateTopic, BuildVehiclePayload(vehicle)));
        }

        foreach (var site in state.EnergySites)
        {
            var deviceId = $"lms_tesla_energy_{SafeId(site.SiteId)}";
            var stateTopic = $"{NormalizeTopic(baseTopic)}/energy/{SafeTopic(site.SiteId)}/state";
            devices.Add(new HomeAssistantMqttDeviceProjection(
                deviceId,
                site.DisplayName,
                "Tesla",
                "Tesla Energy Site",
                null));
            entities.AddRange(BuildEnergyEntities(deviceId, stateTopic));
            states.Add(new HomeAssistantMqttStateProjection(stateTopic, BuildEnergyPayload(site)));
        }

        return new HomeAssistantMqttProjection(devices, entities, states);
    }

    private static IEnumerable<HomeAssistantMqttEntityProjection> BuildVehicleEntities(string deviceId, string stateTopic) =>
    [
        Sensor(deviceId, "state", "State", stateTopic, "{{ value_json.state }}"),
        Sensor(deviceId, "battery_level", "Battery", stateTopic, "{{ value_json.battery_level }}", "battery", "%"),
        Sensor(deviceId, "charging_state", "Charging State", stateTopic, "{{ value_json.charging_state }}"),
        Sensor(deviceId, "charge_limit", "Charge Limit", stateTopic, "{{ value_json.charge_limit }}", "battery", "%"),
        Sensor(deviceId, "inside_temp", "Inside Temperature", stateTopic, "{{ value_json.inside_temp }}", "temperature", "\u00b0C"),
        Sensor(deviceId, "outside_temp", "Outside Temperature", stateTopic, "{{ value_json.outside_temp }}", "temperature", "\u00b0C"),
        Sensor(deviceId, "latitude", "Latitude", stateTopic, "{{ value_json.latitude }}", enabledByDefault: false),
        Sensor(deviceId, "longitude", "Longitude", stateTopic, "{{ value_json.longitude }}", enabledByDefault: false),
        Sensor(deviceId, "firmware", "Firmware", stateTopic, "{{ value_json.firmware }}"),
        Sensor(deviceId, "fleet_key_status", "Fleet Key Status", stateTopic, "{{ value_json.fleet_key_status }}"),
        Sensor(deviceId, "total_keys", "Total Keys", stateTopic, "{{ value_json.total_keys }}")
    ];

    private static IEnumerable<HomeAssistantMqttEntityProjection> BuildEnergyEntities(string deviceId, string stateTopic) =>
    [
        Sensor(deviceId, "grid_status", "Grid Status", stateTopic, "{{ value_json.grid_status }}"),
        Sensor(deviceId, "battery_percentage", "Battery", stateTopic, "{{ value_json.battery_percentage }}", "battery", "%"),
        Sensor(deviceId, "solar_power", "Solar Power", stateTopic, "{{ value_json.solar_power }}", "power", "W"),
        Sensor(deviceId, "load_power", "Load Power", stateTopic, "{{ value_json.load_power }}", "power", "W"),
        Sensor(deviceId, "battery_power", "Battery Power", stateTopic, "{{ value_json.battery_power }}", "power", "W"),
        Sensor(deviceId, "grid_power", "Grid Power", stateTopic, "{{ value_json.grid_power }}", "power", "W"),
        Sensor(deviceId, "backup_reserve", "Backup Reserve", stateTopic, "{{ value_json.backup_reserve }}", "battery", "%")
    ];

    private static IReadOnlyDictionary<string, object?> BuildVehiclePayload(LmsTeslaVehicleState vehicle) =>
        new Dictionary<string, object?>
        {
            ["vin"] = vehicle.Vin,
            ["display_name"] = vehicle.DisplayName,
            ["state"] = vehicle.ConnectivityState,
            ["battery_level"] = vehicle.Charge.BatteryLevelPercent,
            ["charging_state"] = vehicle.Charge.ChargingState,
            ["charge_limit"] = vehicle.Charge.ChargeLimitPercent,
            ["inside_temp"] = vehicle.Climate.InsideTemperatureCelsius,
            ["outside_temp"] = vehicle.Climate.OutsideTemperatureCelsius,
            ["latitude"] = vehicle.Drive.Latitude,
            ["longitude"] = vehicle.Drive.Longitude,
            ["firmware"] = vehicle.Meta.FirmwareVersion,
            ["fleet_key_status"] = vehicle.FleetKey.Status,
            ["total_keys"] = vehicle.FleetKey.TotalNumberOfKeys
        };

    private static IReadOnlyDictionary<string, object?> BuildEnergyPayload(LmsTeslaEnergySiteState site) =>
        new Dictionary<string, object?>
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

    private static HomeAssistantMqttEntityProjection Sensor(
        string deviceId,
        string id,
        string name,
        string stateTopic,
        string valueTemplate,
        string? deviceClass = null,
        string? unitOfMeasurement = null,
        bool enabledByDefault = true) =>
        new(
            $"{deviceId}_{id}",
            deviceId,
            "sensor",
            name,
            stateTopic,
            valueTemplate,
            deviceClass,
            unitOfMeasurement,
            enabledByDefault);

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
        return new string(chars).Trim('_');
    }
}
