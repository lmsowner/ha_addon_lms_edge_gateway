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
    string? CommandTopic,
    string ValueTemplate,
    string? DeviceClass,
    string? UnitOfMeasurement,
    bool EnabledByDefault);

sealed record HomeAssistantMqttDeviceProjection(
    string Id,
    string Name,
    string Manufacturer,
    string Model,
    string? SoftwareVersion,
    string? ViaDeviceId = null);

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
    IReadOnlyDictionary<string, object?>? ExtraConfig = null,
    string? CommandTopic = null,
    string? CommandTemplate = null);

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

sealed record SeatClimateDefinition(
    string Id,
    string Name,
    string HeaterPath,
    string? CoolerPath,
    string? AutoPath,
    bool IsFront);

sealed class HomeAssistantMqttProjectionMapper
{
    private static readonly string[] CabinOverheatProtectionOptions = ["Off", "No A/C", "On"];
    private static readonly string[] FrontSeatHeatOptions = ["Off", "Low", "Medium", "High", "Auto"];
    private static readonly string[] FrontSeatCoolHeatOptions = ["Off", "Heat Low", "Heat Medium", "Heat High", "Auto", "Cool Low", "Cool Medium", "Cool High"];
    private static readonly string[] RearSeatHeatOptions = ["Off", "Low", "Medium", "High"];
    private static readonly string[] SteeringWheelHeatOptions = ["Off", "Low", "High", "Auto"];
    private static readonly SeatClimateDefinition[] SeatClimateDefinitions =
    [
        new("left", "Left seat climate", "seat_heater_left", "seat_fan_front_left", "auto_seat_climate_left", true),
        new("right", "Right seat climate", "seat_heater_right", "seat_fan_front_right", "auto_seat_climate_right", true),
        new("rear_left", "Rear left seat heater", "seat_heater_rear_left", null, null, false),
        new("rear_center", "Rear center seat heater", "seat_heater_rear_center", null, null, false),
        new("rear_right", "Rear right seat heater", "seat_heater_rear_right", null, null, false),
        new("third_row_left", "Third row left seat heater", "seat_heater_third_row_left", null, null, false),
        new("third_row_right", "Third row right seat heater", "seat_heater_third_row_right", null, null, false)
    ];

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
            entities.AddRange(BuildVehicleEntities(deviceId, stateTopic, vehicle));
            entities.AddRange(BuildRawEntities(deviceId, stateTopic, vehicle.RawProperties));
            states.Add(new HomeAssistantMqttStateProjection(stateTopic, BuildVehiclePayload(vehicle)));
        }

        foreach (var site in state.EnergySites)
        {
            var deviceId = $"lms_tesla_energy_{SafeId(site.SiteId)}";
            var stateTopic = $"{normalizedBaseTopic}/energy/{SafeTopic(site.SiteId)}/state";
            devices.Add(new HomeAssistantMqttDeviceProjection(
                deviceId,
                ResolveGatewayDeviceName(site),
                "Tesla",
                "Tesla Backup Gateway / Energy Site",
                null));
            entities.AddRange(BuildEnergyEntities(deviceId, stateTopic));
            entities.AddRange(BuildRawEntities(deviceId, stateTopic, site.RawProperties));
            var powerwallCount = ResolvePowerwallCount(site);
            for (var index = 1; index <= powerwallCount; index++)
            {
                var powerwallDeviceId = $"{deviceId}_powerwall_{index}";
                devices.Add(new HomeAssistantMqttDeviceProjection(
                    powerwallDeviceId,
                    $"Tesla Powerwall {index} - {site.DisplayName}",
                    "Tesla",
                    "Tesla Powerwall Inventory",
                    null,
                    deviceId));
                entities.AddRange(BuildPowerwallUnitEntities(powerwallDeviceId, stateTopic, index, site));
            }
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

    private static IEnumerable<HomeAssistantMqttEntityProjection> BuildVehicleEntities(
        string deviceId,
        string stateTopic,
        LmsTeslaVehicleState vehicle)
    {
        var maxChargingAmps = vehicle.Charge.MaxChargingAmps is > 0
            ? Math.Clamp(vehicle.Charge.MaxChargingAmps.Value, 1, 80)
            : 80;
        var minClimateTemp = ReadDouble(vehicle.RawProperties, "climate_state.min_avail_temp", "min_avail_temp") ?? 15;
        var maxClimateTemp = ReadDouble(vehicle.RawProperties, "climate_state.max_avail_temp", "max_avail_temp") ?? 28;

        return
        [
            Sensor(deviceId, "state", "State", stateTopic, "{{ value_json.state }}"),
            BinarySensor(deviceId, "online", "Online", stateTopic, "{{ (value_json.state == 'online') | lower }}", "connectivity"),
            Sensor(deviceId, "vehicle_data_refreshed", "Vehicle Data Refreshed", stateTopic, "{{ value_json.vehicle_data_refreshed }}", "timestamp"),
            Switch(deviceId, "polling", "Polling", stateTopic, "{{ 'ON' if value_json.polling_enabled else 'OFF' }}", BuildCommandTopic(stateTopic, "polling"), entityCategory: "diagnostic", icon: "mdi:car-connected"),
            Sensor(deviceId, "battery_level", "Battery", stateTopic, "{{ value_json.battery_level }}", "battery", "%", stateClass: "measurement"),
            Sensor(deviceId, "usable_battery_level", "Usable Battery", stateTopic, "{{ value_json.usable_battery_level }}", "battery", "%", stateClass: "measurement"),
            Sensor(deviceId, "charging_state", "Charging State", stateTopic, "{{ value_json.charging_state }}"),
            Number(deviceId, "charge_limit", "Charge limit", stateTopic, "{{ value_json.charge_limit }}", BuildCommandTopic(stateTopic, "charge_limit"), "battery", "%", 50, 100, 1, "slider", icon: "mdi:battery-charging-80"),
            Number(deviceId, "charging_amps", "Charging amps", stateTopic, "{{ value_json.charging_amps }}", BuildCommandTopic(stateTopic, "charging_amps"), "current", "A", 1, maxChargingAmps, 1, "slider", icon: "mdi:current-ac"),
            Switch(deviceId, "charger", "Charger", stateTopic, "{{ 'ON' if value_json.charging_state == 'Charging' else 'OFF' }}", BuildCommandTopic(stateTopic, "charger"), icon: "mdi:ev-station"),
            Sensor(deviceId, "battery_range", "Battery Range", stateTopic, "{{ value_json.battery_range }}", "distance", "mi", stateClass: "measurement"),
            Sensor(deviceId, "connected_charge_cable", "Connected Charge Cable", stateTopic, "{{ value_json.connected_charge_cable }}"),
            BinarySensor(deviceId, "fast_charger_present", "Fast Charger Present", stateTopic, "{{ value_json.fast_charger_present | lower }}"),
            BinarySensor(deviceId, "charge_port_door_open", "Charge Port Door Open", stateTopic, "{{ value_json.charge_port_door_open | lower }}", "opening"),
            Cover(deviceId, "charge_port_door", "Charge port door", stateTopic, "{{ value_json.charge_port_door_state }}", BuildCommandTopic(stateTopic, "charge_port_door"), "door", icon: "mdi:ev-plug-tesla"),
            Sensor(deviceId, "inside_temp", "Inside Temperature", stateTopic, "{{ value_json.inside_temp }}", "temperature", "\u00b0C", stateClass: "measurement"),
            Sensor(deviceId, "outside_temp", "Outside Temperature", stateTopic, "{{ value_json.outside_temp }}", "temperature", "\u00b0C", stateClass: "measurement"),
            BinarySensor(deviceId, "climate_on", "Climate On", stateTopic, "{{ value_json.climate_on | lower }}"),
            Climate(deviceId, "climate", "Climate", stateTopic, minClimateTemp, maxClimateTemp),
            Select(deviceId, "cabin_overheat_protection", "Cabin overheat protection", stateTopic, "{{ value_json.cabin_overheat_protection }}", BuildCommandTopic(stateTopic, "cabin_overheat_protection"), CabinOverheatProtectionOptions, icon: "mdi:car-thermostat"),
            Switch(deviceId, "heated_steering_wheel", "Heated steering wheel", stateTopic, "{{ 'ON' if value_json.heated_steering_wheel else 'OFF' }}", BuildCommandTopic(stateTopic, "heated_steering_wheel"), enabledByDefault: IsSimpleSteeringWheelHeatEnabled(vehicle.RawProperties), icon: "mdi:steering"),
            Select(deviceId, "steering_wheel_heat", "Steering wheel heat", stateTopic, "{{ value_json.steering_wheel_heat }}", BuildCommandTopic(stateTopic, "steering_wheel_heat"), SteeringWheelHeatOptions, enabledByDefault: IsVariableSteeringWheelHeatEnabled(vehicle.RawProperties), icon: "mdi:steering"),
            Select(deviceId, "seat_climate_left", "Left seat climate", stateTopic, "{{ value_json.seat_climate_left }}", BuildCommandTopic(stateTopic, "seat_climate_left"), ResolveFrontSeatOptions(vehicle.RawProperties), icon: "mdi:car-seat-heater"),
            Select(deviceId, "seat_climate_right", "Right seat climate", stateTopic, "{{ value_json.seat_climate_right }}", BuildCommandTopic(stateTopic, "seat_climate_right"), ResolveFrontSeatOptions(vehicle.RawProperties), icon: "mdi:car-seat-heater"),
            Select(deviceId, "seat_climate_rear_left", "Rear left seat heater", stateTopic, "{{ value_json.seat_climate_rear_left }}", BuildCommandTopic(stateTopic, "seat_climate_rear_left"), RearSeatHeatOptions, enabledByDefault: IsRearSeatEnabled(vehicle.RawProperties, "seat_heater_rear_left"), icon: "mdi:car-seat-heater"),
            Select(deviceId, "seat_climate_rear_center", "Rear center seat heater", stateTopic, "{{ value_json.seat_climate_rear_center }}", BuildCommandTopic(stateTopic, "seat_climate_rear_center"), RearSeatHeatOptions, enabledByDefault: IsRearSeatEnabled(vehicle.RawProperties, "seat_heater_rear_center"), icon: "mdi:car-seat-heater"),
            Select(deviceId, "seat_climate_rear_right", "Rear right seat heater", stateTopic, "{{ value_json.seat_climate_rear_right }}", BuildCommandTopic(stateTopic, "seat_climate_rear_right"), RearSeatHeatOptions, enabledByDefault: IsRearSeatEnabled(vehicle.RawProperties, "seat_heater_rear_right"), icon: "mdi:car-seat-heater"),
            Select(deviceId, "seat_climate_third_row_left", "Third row left seat heater", stateTopic, "{{ value_json.seat_climate_third_row_left }}", BuildCommandTopic(stateTopic, "seat_climate_third_row_left"), RearSeatHeatOptions, enabledByDefault: IsThirdRowSeatEnabled(vehicle.RawProperties, "seat_heater_third_row_left"), icon: "mdi:car-seat-heater"),
            Select(deviceId, "seat_climate_third_row_right", "Third row right seat heater", stateTopic, "{{ value_json.seat_climate_third_row_right }}", BuildCommandTopic(stateTopic, "seat_climate_third_row_right"), RearSeatHeatOptions, enabledByDefault: IsThirdRowSeatEnabled(vehicle.RawProperties, "seat_heater_third_row_right"), icon: "mdi:car-seat-heater"),
            Sensor(deviceId, "driver_temp_setting", "Driver Temperature Setting", stateTopic, "{{ value_json.driver_temp_setting }}", "temperature", "\u00b0C", stateClass: "measurement"),
            Sensor(deviceId, "passenger_temp_setting", "Passenger Temperature Setting", stateTopic, "{{ value_json.passenger_temp_setting }}", "temperature", "\u00b0C", stateClass: "measurement"),
            BinarySensor(deviceId, "doors_open", "Doors Open", stateTopic, "{{ value_json.doors_open | lower }}", "door"),
            BinarySensor(deviceId, "windows_open", "Windows Open", stateTopic, "{{ value_json.windows_open | lower }}", "window"),
            Cover(deviceId, "windows", "Windows", stateTopic, "{{ value_json.windows_state }}", BuildCommandTopic(stateTopic, "windows"), "window", icon: "mdi:car-door"),
            Cover(deviceId, "frunk", "Frunk", stateTopic, "{{ value_json.frunk_state }}", BuildCommandTopic(stateTopic, "frunk"), "door", icon: "mdi:car"),
            Cover(deviceId, "trunk", "Trunk", stateTopic, "{{ value_json.trunk_state }}", BuildCommandTopic(stateTopic, "trunk"), "door", icon: "mdi:car-back"),
            Cover(deviceId, "sunroof", "Sunroof", stateTopic, "{{ value_json.sunroof_state }}", BuildCommandTopic(stateTopic, "sunroof"), "window", enabledByDefault: false, icon: "mdi:car-select"),
            DeviceTracker(deviceId, "location", "Location", stateTopic),
            Sensor(deviceId, "latitude", "Latitude", stateTopic, "{{ value_json.latitude }}", enabledByDefault: false),
            Sensor(deviceId, "longitude", "Longitude", stateTopic, "{{ value_json.longitude }}", enabledByDefault: false),
            Sensor(deviceId, "heading", "Heading", stateTopic, "{{ value_json.heading }}", unitOfMeasurement: "\u00b0", stateClass: "measurement"),
            Sensor(deviceId, "speed", "Speed", stateTopic, "{{ value_json.speed }}", "speed", "mph", stateClass: "measurement"),
            Sensor(deviceId, "shift_state", "Shift State", stateTopic, "{{ value_json.shift_state }}"),
            Sensor(deviceId, "firmware", "Firmware", stateTopic, "{{ value_json.firmware }}"),
            Sensor(deviceId, "odometer", "Odometer", stateTopic, "{{ value_json.odometer }}", "distance", "mi", stateClass: "total_increasing"),
            BinarySensor(deviceId, "locked", "Locked", stateTopic, "{{ value_json.locked | lower }}"),
            Lock(deviceId, "doors", "Doors", stateTopic, "{{ 'LOCKED' if value_json.locked else 'UNLOCKED' }}", BuildCommandTopic(stateTopic, "door_lock")),
            Lock(deviceId, "charge_port_latch", "Charge port latch", stateTopic, "{{ value_json.charge_port_latch_state }}", BuildCommandTopic(stateTopic, "charge_port_latch"), icon: "mdi:ev-plug-tesla", supportsOpen: true),
            BinarySensor(deviceId, "sentry_mode", "Sentry Mode", stateTopic, "{{ value_json.sentry_mode | lower }}"),
            Switch(deviceId, "sentry_mode_switch", "Sentry mode", stateTopic, "{{ 'ON' if value_json.sentry_mode else 'OFF' }}", BuildCommandTopic(stateTopic, "sentry_mode"), icon: "mdi:shield-car"),
            Switch(deviceId, "valet_mode", "Valet mode", stateTopic, "{{ 'ON' if value_json.valet_mode else 'OFF' }}", BuildCommandTopic(stateTopic, "valet_mode"), icon: "mdi:room-service"),
            Button(deviceId, "wake_up", "Wake up", BuildCommandTopic(stateTopic, "wake_up"), icon: "mdi:power"),
            Button(deviceId, "force_data_update", "Force data update", BuildCommandTopic(stateTopic, "force_data_update"), entityCategory: "diagnostic", icon: "mdi:database-sync"),
            Button(deviceId, "flash_lights", "Flash lights", BuildCommandTopic(stateTopic, "flash_lights"), icon: "mdi:car-light-high"),
            Button(deviceId, "honk_horn", "Horn", BuildCommandTopic(stateTopic, "honk_horn"), icon: "mdi:bullhorn"),
            Button(deviceId, "homelink", "Homelink", BuildCommandTopic(stateTopic, "homelink"), enabledByDefault: IsHomelinkEnabled(vehicle.RawProperties), icon: "mdi:garage"),
            Button(deviceId, "remote_start", "Remote start", BuildCommandTopic(stateTopic, "remote_start"), icon: "mdi:key-wireless"),
            Button(deviceId, "remote_boombox", "Emissions test", BuildCommandTopic(stateTopic, "remote_boombox"), enabledByDefault: IsPedestrianSpeakerEnabled(vehicle.RawProperties), icon: "mdi:weather-windy"),
            Button(deviceId, "charge_standard", "Charge standard", BuildCommandTopic(stateTopic, "charge_standard"), enabledByDefault: false, icon: "mdi:battery-charging"),
            Button(deviceId, "charge_max_range", "Charge max range", BuildCommandTopic(stateTopic, "charge_max_range"), enabledByDefault: false, icon: "mdi:battery-charging-100"),
            Button(deviceId, "media_toggle_playback", "Media play pause", BuildCommandTopic(stateTopic, "media_toggle_playback"), enabledByDefault: false, icon: "mdi:play-pause"),
            Button(deviceId, "media_next_track", "Media next track", BuildCommandTopic(stateTopic, "media_next_track"), enabledByDefault: false, icon: "mdi:skip-next"),
            Button(deviceId, "media_prev_track", "Media previous track", BuildCommandTopic(stateTopic, "media_prev_track"), enabledByDefault: false, icon: "mdi:skip-previous"),
            Button(deviceId, "media_next_fav", "Media next favorite", BuildCommandTopic(stateTopic, "media_next_fav"), enabledByDefault: false, icon: "mdi:skip-forward"),
            Button(deviceId, "media_prev_fav", "Media previous favorite", BuildCommandTopic(stateTopic, "media_prev_fav"), enabledByDefault: false, icon: "mdi:skip-backward"),
            Button(deviceId, "media_volume_up", "Media volume up", BuildCommandTopic(stateTopic, "media_volume_up"), enabledByDefault: false, icon: "mdi:volume-plus"),
            Button(deviceId, "media_volume_down", "Media volume down", BuildCommandTopic(stateTopic, "media_volume_down"), enabledByDefault: false, icon: "mdi:volume-minus"),
            Sensor(deviceId, "fleet_key_status", "Fleet Key Status", stateTopic, "{{ value_json.fleet_key_status }}"),
            BinarySensor(deviceId, "fleet_key_paired", "Fleet Key Paired", stateTopic, "{{ value_json.fleet_key_paired | lower }}", "connectivity"),
            BinarySensor(deviceId, "vehicle_command_protocol_required", "Signed Commands Required", stateTopic, "{{ value_json.vehicle_command_protocol_required | lower }}"),
            Sensor(deviceId, "total_keys", "Total Keys", stateTopic, "{{ value_json.total_keys }}", stateClass: "measurement")
        ];
    }

    private static IEnumerable<HomeAssistantMqttEntityProjection> BuildEnergyEntities(string deviceId, string stateTopic) =>
    [
        Sensor(deviceId, "display_name", "Display Name", stateTopic, "{{ value_json.display_name }}"),
        Sensor(deviceId, "site_id", "Site ID", stateTopic, "{{ value_json.site_id }}", enabledByDefault: false, entityCategory: "diagnostic"),
        Sensor(deviceId, "energy_data_status", "Energy Data Status", stateTopic, "{{ value_json.energy_data_status }}"),
        Sensor(deviceId, "resource_type", "Resource Type", stateTopic, "{{ value_json.resource_type }}"),
        Sensor(deviceId, "gateway_type", "Gateway Type", stateTopic, "{{ value_json.gateway_type }}", enabledByDefault: false, entityCategory: "diagnostic"),
        Sensor(deviceId, "battery_type", "Battery Type", stateTopic, "{{ value_json.battery_type }}", enabledByDefault: false, entityCategory: "diagnostic"),
        Sensor(deviceId, "powerwall_count", "Powerwall Count", stateTopic, "{{ value_json.powerwall_count }}", stateClass: "measurement"),
        BinarySensor(deviceId, "has_solar", "Has Solar", stateTopic, "{{ value_json.has_solar | lower }}", enabledByDefault: false, entityCategory: "diagnostic"),
        BinarySensor(deviceId, "has_battery", "Has Battery", stateTopic, "{{ value_json.has_battery | lower }}", enabledByDefault: false, entityCategory: "diagnostic"),
        BinarySensor(deviceId, "has_grid", "Has Grid", stateTopic, "{{ value_json.has_grid | lower }}", enabledByDefault: false, entityCategory: "diagnostic"),
        BinarySensor(deviceId, "has_backup", "Has Backup", stateTopic, "{{ value_json.has_backup | lower }}", enabledByDefault: false, entityCategory: "diagnostic"),
        BinarySensor(deviceId, "has_load_meter", "Has Load Meter", stateTopic, "{{ value_json.has_load_meter | lower }}", enabledByDefault: false, entityCategory: "diagnostic"),
        Sensor(deviceId, "grid_status", "Grid Status", stateTopic, "{{ value_json.grid_status }}"),
        BinarySensor(deviceId, "grid_connected", "Grid Connected", stateTopic, "{{ (value_json.grid_status | lower == 'active') | lower }}", "power"),
        Sensor(deviceId, "island_status", "Island Status", stateTopic, "{{ value_json.island_status }}", enabledByDefault: false, entityCategory: "diagnostic"),
        Sensor(deviceId, "battery_percentage", "Battery", stateTopic, "{{ value_json.battery_percentage }}", "battery", "%", stateClass: "measurement"),
        Sensor(deviceId, "battery_remaining", "Battery Remaining", stateTopic, "{{ value_json.battery_remaining }}", "energy_storage", "Wh", stateClass: "measurement"),
        Sensor(deviceId, "solar_power", "Solar Power", stateTopic, "{{ value_json.solar_power }}", "power", "W", stateClass: "measurement"),
        Sensor(deviceId, "load_power", "Load Power", stateTopic, "{{ value_json.load_power }}", "power", "W", stateClass: "measurement"),
        Sensor(deviceId, "battery_power", "Battery Power", stateTopic, "{{ value_json.battery_power }}", "power", "W", stateClass: "measurement"),
        BinarySensor(deviceId, "battery_charging", "Battery Charging", stateTopic, "{{ (value_json.battery_power | float(0) < -100) | lower }}", "battery_charging"),
        Sensor(deviceId, "grid_power", "Grid Power", stateTopic, "{{ value_json.grid_power }}", "power", "W", stateClass: "measurement"),
        Sensor(deviceId, "generator_power", "Generator Power", stateTopic, "{{ value_json.generator_power }}", "power", "W", stateClass: "measurement", enabledByDefault: false),
        Sensor(deviceId, "backup_reserve", "Backup Reserve", stateTopic, "{{ value_json.backup_reserve }}", "battery", "%", stateClass: "measurement"),
        Number(deviceId, "backup_reserve_number", "Backup reserve", stateTopic, "{{ value_json.backup_reserve }}", BuildCommandTopic(stateTopic, "backup_reserve"), "battery", "%", 0, 100, 1, "auto", icon: "mdi:battery"),
        Number(deviceId, "off_grid_vehicle_charging_reserve", "Off-grid vehicle charging reserve", stateTopic, "{{ value_json.off_grid_vehicle_charging_reserve }}", BuildCommandTopic(stateTopic, "off_grid_vehicle_charging_reserve"), "battery", "%", 0, 100, 1, "auto", icon: "mdi:car-battery"),
        Select(deviceId, "operation_mode", "Operation mode", stateTopic, "{{ value_json.operation_mode }}", BuildCommandTopic(stateTopic, "operation_mode"), ["Self-Powered", "Time-Based Control", "Backup"], icon: "mdi:home-battery"),
        Select(deviceId, "grid_charging", "Grid charging", stateTopic, "{{ value_json.grid_charging }}", BuildCommandTopic(stateTopic, "grid_charging"), ["Yes", "No"], icon: "mdi:transmission-tower-export"),
        Select(deviceId, "energy_exports", "Energy exports", stateTopic, "{{ value_json.energy_exports }}", BuildCommandTopic(stateTopic, "energy_exports"), ["Nothing", "Solar", "Everything"], icon: "mdi:home-export-outline"),
        Switch(deviceId, "storm_mode", "Storm watch", stateTopic, "{{ 'ON' if value_json.storm_mode_active else 'OFF' }}", BuildCommandTopic(stateTopic, "storm_mode"), icon: "mdi:weather-lightning-rainy"),
        BinarySensor(deviceId, "grid_services_active", "Grid Services Active", stateTopic, "{{ value_json.grid_services_active | lower }}", enabledByDefault: false, entityCategory: "diagnostic"),
        BinarySensor(deviceId, "storm_mode_active", "Storm Mode Active", stateTopic, "{{ value_json.storm_mode_active | lower }}", enabledByDefault: false, entityCategory: "diagnostic")
    ];

    private static IEnumerable<HomeAssistantMqttEntityProjection> BuildPowerwallUnitEntities(
        string deviceId,
        string stateTopic,
        int index,
        LmsTeslaEnergySiteState site)
    {
        var entities = new List<HomeAssistantMqttEntityProjection>
        {
            Sensor(deviceId, "status", "Status", stateTopic, $"{{{{ value_json.powerwall_{index}_status }}}}"),
            Sensor(deviceId, "gateway", "Gateway", stateTopic, "{{ value_json.display_name }}", enabledByDefault: false, entityCategory: "diagnostic"),
            Sensor(deviceId, "unit_index", "Unit Index", stateTopic, $"{{{{ value_json.powerwall_{index}_unit_index }}}}", enabledByDefault: false, entityCategory: "diagnostic", stateClass: "measurement")
        };

        entities.AddRange(BuildPowerwallUnitRawEntities(deviceId, stateTopic, index, site.RawProperties));
        return entities;
    }

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
            ["vehicle_data_refreshed"] = vehicle.Meta.VehicleDataRefreshedUtc,
            ["polling_enabled"] = ReadBool(vehicle.RawProperties, "lms_helper.polling_enabled", "polling_enabled") ?? true,
            ["battery_level"] = vehicle.Charge.BatteryLevelPercent,
            ["usable_battery_level"] = vehicle.Charge.UsableBatteryLevelPercent,
            ["charging_state"] = vehicle.Charge.ChargingState,
            ["charge_limit"] = vehicle.Charge.ChargeLimitPercent,
            ["charging_amps"] = vehicle.Charge.ChargingAmps,
            ["max_charging_amps"] = vehicle.Charge.MaxChargingAmps,
            ["battery_range"] = vehicle.Charge.BatteryRange,
            ["connected_charge_cable"] = vehicle.Charge.ConnectedChargeCable,
            ["fast_charger_present"] = vehicle.Charge.FastChargerPresent,
            ["charge_port_door_open"] = vehicle.Charge.ChargePortDoorOpen,
            ["charge_port_door_state"] = ToOpenClosedState(vehicle.Charge.ChargePortDoorOpen),
            ["inside_temp"] = vehicle.Climate.InsideTemperatureCelsius,
            ["outside_temp"] = vehicle.Climate.OutsideTemperatureCelsius,
            ["climate_on"] = vehicle.Climate.IsClimateOn,
            ["hvac_mode"] = vehicle.Climate.IsClimateOn == true ? "heat_cool" : "off",
            ["climate_preset_mode"] = ResolveClimatePresetMode(vehicle.RawProperties),
            ["climate_fan_mode"] = ResolveClimateFanMode(vehicle.RawProperties),
            ["cabin_overheat_protection"] = ResolveCabinOverheatProtection(vehicle.RawProperties),
            ["heated_steering_wheel"] = ReadBool(vehicle.RawProperties, "climate_state.steering_wheel_heater", "steering_wheel_heater"),
            ["steering_wheel_heat"] = ResolveSteeringWheelHeat(vehicle.RawProperties),
            ["seat_climate_left"] = ResolveSeatClimate(vehicle.RawProperties, SeatClimateDefinitions[0]),
            ["seat_climate_right"] = ResolveSeatClimate(vehicle.RawProperties, SeatClimateDefinitions[1]),
            ["seat_climate_rear_left"] = ResolveSeatClimate(vehicle.RawProperties, SeatClimateDefinitions[2]),
            ["seat_climate_rear_center"] = ResolveSeatClimate(vehicle.RawProperties, SeatClimateDefinitions[3]),
            ["seat_climate_rear_right"] = ResolveSeatClimate(vehicle.RawProperties, SeatClimateDefinitions[4]),
            ["seat_climate_third_row_left"] = ResolveSeatClimate(vehicle.RawProperties, SeatClimateDefinitions[5]),
            ["seat_climate_third_row_right"] = ResolveSeatClimate(vehicle.RawProperties, SeatClimateDefinitions[6]),
            ["driver_temp_setting"] = vehicle.Climate.DriverTemperatureSettingCelsius,
            ["passenger_temp_setting"] = vehicle.Climate.PassengerTemperatureSettingCelsius,
            ["doors_open"] = ResolveAnyOpen(vehicle.RawProperties, "vehicle_state.df", "vehicle_state.dr", "vehicle_state.pf", "vehicle_state.pr", "df", "dr", "pf", "pr"),
            ["windows_open"] = ResolveAnyOpen(vehicle.RawProperties, "vehicle_state.fd_window", "vehicle_state.fp_window", "vehicle_state.rd_window", "vehicle_state.rp_window", "fd_window", "fp_window", "rd_window", "rp_window"),
            ["windows_state"] = ToOpenClosedState(ResolveAnyOpen(vehicle.RawProperties, "vehicle_state.fd_window", "vehicle_state.fp_window", "vehicle_state.rd_window", "vehicle_state.rp_window", "fd_window", "fp_window", "rd_window", "rp_window")),
            ["frunk_state"] = ToOpenClosedState(ReadBool(vehicle.RawProperties, "vehicle_state.ft", "ft")),
            ["trunk_state"] = ToOpenClosedState(ReadBool(vehicle.RawProperties, "vehicle_state.rt", "rt")),
            ["sunroof_state"] = ResolveSunroofState(vehicle.RawProperties),
            ["latitude"] = vehicle.Drive.Latitude,
            ["longitude"] = vehicle.Drive.Longitude,
            ["heading"] = vehicle.Drive.Heading,
            ["speed"] = vehicle.Drive.Speed,
            ["shift_state"] = vehicle.Drive.ShiftState,
            ["firmware"] = vehicle.Meta.FirmwareVersion,
            ["odometer"] = vehicle.Meta.Odometer,
            ["locked"] = vehicle.Meta.Locked,
            ["charge_port_latch_state"] = ResolveChargePortLatchState(vehicle.RawProperties),
            ["sentry_mode"] = vehicle.Meta.SentryMode,
            ["valet_mode"] = ReadBool(vehicle.RawProperties, "vehicle_state.valet_mode", "valet_mode"),
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
            ["energy_data_status"] = ResolveEnergyDataStatus(site),
            ["resource_type"] = site.ResourceType,
            ["gateway_type"] = site.Capabilities.GatewayType,
            ["battery_type"] = site.Capabilities.BatteryType,
            ["has_solar"] = site.Capabilities.HasSolar,
            ["has_battery"] = site.Capabilities.HasBattery,
            ["has_grid"] = site.Capabilities.HasGrid,
            ["has_backup"] = site.Capabilities.HasBackup,
            ["has_load_meter"] = site.Capabilities.HasLoadMeter,
            ["grid_status"] = site.Live.GridStatus,
            ["island_status"] = site.Live.IslandStatus,
            ["battery_percentage"] = site.Live.BatteryPercentage,
            ["battery_remaining"] = site.Live.EnergyRemainingWh,
            ["solar_power"] = site.Live.SolarPowerWatts,
            ["load_power"] = site.Live.LoadPowerWatts,
            ["battery_power"] = site.Live.BatteryPowerWatts,
            ["grid_power"] = site.Live.GridPowerWatts,
            ["generator_power"] = site.Live.GeneratorPowerWatts,
            ["backup_reserve"] = site.Live.BackupReservePercent,
            ["off_grid_vehicle_charging_reserve"] = ResolveOffGridVehicleChargingReserve(site),
            ["operation_mode"] = ResolveOperationMode(site),
            ["grid_charging"] = ResolveGridCharging(site),
            ["energy_exports"] = ResolveEnergyExports(site),
            ["grid_services_active"] = site.Live.GridServicesActive,
            ["storm_mode_active"] = site.Live.StormModeActive
        };
        var powerwallCount = ResolvePowerwallCount(site);
        payload["powerwall_count"] = powerwallCount;
        for (var index = 1; index <= powerwallCount; index++)
        {
            payload[$"powerwall_{index}_status"] = "present";
            payload[$"powerwall_{index}_unit_index"] = index;
        }
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

    private static HomeAssistantMqttEntityProjection Number(
        string deviceId,
        string id,
        string name,
        string stateTopic,
        string valueTemplate,
        string commandTopic,
        string? deviceClass,
        string? unitOfMeasurement,
        double min,
        double max,
        double step,
        string mode,
        bool enabledByDefault = true,
        string? entityCategory = null,
        string? icon = null) =>
        new(
            $"{deviceId}_{id}",
            deviceId,
            "number",
            name,
            stateTopic,
            valueTemplate,
            deviceClass,
            unitOfMeasurement,
            enabledByDefault,
            entityCategory,
            null,
            icon,
            new Dictionary<string, object?>
            {
                ["min"] = min,
                ["max"] = max,
                ["step"] = step,
                ["mode"] = mode
            },
            commandTopic);

    private static HomeAssistantMqttEntityProjection Select(
        string deviceId,
        string id,
        string name,
        string stateTopic,
        string valueTemplate,
        string commandTopic,
        string[] options,
        bool enabledByDefault = true,
        string? entityCategory = null,
        string? icon = null) =>
        new(
            $"{deviceId}_{id}",
            deviceId,
            "select",
            name,
            stateTopic,
            valueTemplate,
            null,
            null,
            enabledByDefault,
            entityCategory,
            null,
            icon,
            new Dictionary<string, object?>
            {
                ["options"] = options
            },
            commandTopic);

    private static HomeAssistantMqttEntityProjection Switch(
        string deviceId,
        string id,
        string name,
        string stateTopic,
        string valueTemplate,
        string commandTopic,
        bool enabledByDefault = true,
        string? entityCategory = null,
        string? icon = null) =>
        new(
            $"{deviceId}_{id}",
            deviceId,
            "switch",
            name,
            stateTopic,
            valueTemplate,
            null,
            null,
            enabledByDefault,
            entityCategory,
            null,
            icon,
            new Dictionary<string, object?>
            {
                ["payload_on"] = "ON",
                ["payload_off"] = "OFF",
                ["state_on"] = "ON",
                ["state_off"] = "OFF"
            },
            commandTopic);

    private static HomeAssistantMqttEntityProjection Button(
        string deviceId,
        string id,
        string name,
        string commandTopic,
        string payload = "PRESS",
        bool enabledByDefault = true,
        string? entityCategory = null,
        string? icon = null) =>
        new(
            $"{deviceId}_{id}",
            deviceId,
            "button",
            name,
            string.Empty,
            string.Empty,
            null,
            null,
            enabledByDefault,
            entityCategory,
            null,
            icon,
            new Dictionary<string, object?>
            {
                ["payload_press"] = payload
            },
            commandTopic);

    private static HomeAssistantMqttEntityProjection Climate(
        string deviceId,
        string id,
        string name,
        string stateTopic,
        double minTemp,
        double maxTemp) =>
        new(
            $"{deviceId}_{id}",
            deviceId,
            "climate",
            name,
            string.Empty,
            string.Empty,
            null,
            null,
            true,
            null,
            null,
            "mdi:fan",
            new Dictionary<string, object?>
            {
                ["temperature_unit"] = "C",
                ["min_temp"] = Math.Round(minTemp, 1),
                ["max_temp"] = Math.Round(maxTemp, 1),
                ["temp_step"] = 0.5,
                ["modes"] = new[] { "off", "heat_cool" },
                ["payload_on"] = "ON",
                ["payload_off"] = "OFF",
                ["power_command_topic"] = BuildCommandTopic(stateTopic, "climate"),
                ["mode_state_topic"] = stateTopic,
                ["mode_state_template"] = "{{ value_json.hvac_mode }}",
                ["mode_command_topic"] = BuildCommandTopic(stateTopic, "climate_mode"),
                ["current_temperature_topic"] = stateTopic,
                ["current_temperature_template"] = "{{ value_json.inside_temp }}",
                ["temperature_state_topic"] = stateTopic,
                ["temperature_state_template"] = "{{ value_json.driver_temp_setting }}",
                ["temperature_command_topic"] = BuildCommandTopic(stateTopic, "climate_temperature"),
                ["preset_modes"] = new[] { "normal", "defrost", "keep", "dog", "camp" },
                ["preset_mode_state_topic"] = stateTopic,
                ["preset_mode_value_template"] = "{{ value_json.climate_preset_mode }}",
                ["preset_mode_command_topic"] = BuildCommandTopic(stateTopic, "climate_preset"),
                ["fan_modes"] = new[] { "off", "bioweapon" },
                ["fan_mode_state_topic"] = stateTopic,
                ["fan_mode_state_template"] = "{{ value_json.climate_fan_mode }}",
                ["fan_mode_command_topic"] = BuildCommandTopic(stateTopic, "climate_fan")
            });

    private static HomeAssistantMqttEntityProjection Cover(
        string deviceId,
        string id,
        string name,
        string stateTopic,
        string valueTemplate,
        string commandTopic,
        string? deviceClass,
        bool enabledByDefault = true,
        string? icon = null) =>
        new(
            $"{deviceId}_{id}",
            deviceId,
            "cover",
            name,
            stateTopic,
            valueTemplate,
            deviceClass,
            null,
            enabledByDefault,
            null,
            null,
            icon,
            new Dictionary<string, object?>
            {
                ["payload_open"] = "OPEN",
                ["payload_close"] = "CLOSE",
                ["state_open"] = "open",
                ["state_closed"] = "closed"
            },
            commandTopic);

    private static HomeAssistantMqttEntityProjection Lock(
        string deviceId,
        string id,
        string name,
        string stateTopic,
        string valueTemplate,
        string commandTopic,
        bool enabledByDefault = true,
        string? entityCategory = null,
        string? icon = null,
        bool supportsOpen = false) =>
        new(
            $"{deviceId}_{id}",
            deviceId,
            "lock",
            name,
            stateTopic,
            valueTemplate,
            null,
            null,
            enabledByDefault,
            entityCategory,
            null,
            icon,
            new Dictionary<string, object?>
            {
                ["payload_lock"] = "LOCK",
                ["payload_unlock"] = "UNLOCK",
                ["payload_open"] = supportsOpen ? "OPEN" : null,
                ["state_locked"] = "LOCKED",
                ["state_unlocked"] = "UNLOCKED"
            },
            commandTopic);

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

    private static string ResolveGatewayDeviceName(LmsTeslaEnergySiteState site)
    {
        var displayName = string.IsNullOrWhiteSpace(site.DisplayName)
            ? $"Energy Site {site.SiteId}"
            : site.DisplayName.Trim();
        if (displayName.Contains("gateway", StringComparison.OrdinalIgnoreCase))
        {
            return displayName;
        }

        return $"Tesla Gateway - {displayName}";
    }

    private static string ResolveEnergyDataStatus(LmsTeslaEnergySiteState site)
    {
        var hasSiteInfo = site.RawProperties.Keys.Any(key => key.StartsWith("site_info.", StringComparison.OrdinalIgnoreCase));
        var hasLiveStatus =
            site.RawProperties.ContainsKey("energy_left") ||
            site.RawProperties.ContainsKey("percentage_charged") ||
            site.RawProperties.ContainsKey("battery_power") ||
            site.RawProperties.ContainsKey("grid_status");
        return (hasSiteInfo, hasLiveStatus) switch
        {
            (true, true) => "full",
            (true, false) => "site_info_only",
            (false, true) => "live_status_only",
            _ => "products_only"
        };
    }

    private static string? ResolveOperationMode(LmsTeslaEnergySiteState site) =>
        MapOperationModeToHomeAssistant(ReadString(
            site.RawProperties,
            "site_info.default_real_mode",
            "site_info.operation",
            "default_real_mode",
            "operation"));

    private static int? ResolveOffGridVehicleChargingReserve(LmsTeslaEnergySiteState site) =>
        ReadInt(
            site.RawProperties,
            "site_info.off_grid_vehicle_charging_reserve",
            "site_info.off_grid_vehicle_charging_reserve_percent",
            "off_grid_vehicle_charging_reserve",
            "off_grid_vehicle_charging_reserve_percent");

    private static string? ResolveGridCharging(LmsTeslaEnergySiteState site)
    {
        var disallowChargeFromGrid = ReadBool(
            site.RawProperties,
            "site_info.components.disallow_charge_from_grid_with_solar_installed",
            "components.disallow_charge_from_grid_with_solar_installed",
            "disallow_charge_from_grid_with_solar_installed");
        if (disallowChargeFromGrid.HasValue)
        {
            return !disallowChargeFromGrid.Value ? "Yes" : "No";
        }

        return ReadString(
            site.RawProperties,
            "site_info.components.customer_preferred_export_rule",
            "components.customer_preferred_export_rule",
            "customer_preferred_export_rule") is not null
            ? "Yes"
            : null;
    }

    private static string? ResolveEnergyExports(LmsTeslaEnergySiteState site) =>
        MapEnergyExportsToHomeAssistant(ReadString(
            site.RawProperties,
            "site_info.components.customer_preferred_export_rule",
            "components.customer_preferred_export_rule",
            "customer_preferred_export_rule"));

    private static string? MapOperationModeToHomeAssistant(string? value) =>
        value?.Trim() switch
        {
            "self_consumption" => "Self-Powered",
            "autonomous" => "Time-Based Control",
            "backup" => "Backup",
            "" or null => null,
            _ => value
        };

    private static string? MapEnergyExportsToHomeAssistant(string? value) =>
        value?.Trim() switch
        {
            "never" => "Nothing",
            "pv_only" => "Solar",
            "battery_ok" => "Everything",
            "" or null => null,
            _ => value
        };

    private static int ResolvePowerwallCount(LmsTeslaEnergySiteState site)
    {
        if (site.Capabilities.PowerwallCount is > 0)
        {
            return Math.Clamp(site.Capabilities.PowerwallCount.Value, 1, 16);
        }

        var explicitCounts = new[]
            {
                "battery_count",
                "powerwall_count",
                "powerwall_count_on_site",
                "battery_block_count",
                "site_info.battery_count",
                "site_info.powerwall_count",
                "site_info.powerwall_count_on_site",
                "site_info.battery_block_count"
            }
            .Select(key => ReadPositiveInt(site.RawProperties, key))
            .Where(value => value > 0);
        var arrayCounts = site.RawProperties
            .Where(item => IsPowerwallArrayPath(item.Key))
            .Select(item => ReadJsonArrayCount(item.Value))
            .Where(value => value > 0);
        var count = explicitCounts.Concat(arrayCounts).DefaultIfEmpty(0).Max();
        if (count > 0)
        {
            return Math.Clamp(count, 1, 16);
        }

        return 0;
    }

    private static bool IsPowerwallArrayPath(string path)
    {
        var key = path.ToLowerInvariant();
        return key.Contains("battery_blocks", StringComparison.Ordinal) ||
               key.Contains("battery_units", StringComparison.Ordinal) ||
               key.Contains("powerwalls", StringComparison.Ordinal) ||
               key.EndsWith("batteries", StringComparison.Ordinal);
    }

    private static bool IsPowerwallUnitPath(string path, int index)
    {
        var key = path.ToLowerInvariant();
        var indexSegment = $".{index}.";
        return key.Contains($"battery_blocks{indexSegment}", StringComparison.Ordinal) ||
               key.Contains($"battery_units{indexSegment}", StringComparison.Ordinal) ||
               key.Contains($"powerwalls{indexSegment}", StringComparison.Ordinal) ||
               key.Contains($"batteries{indexSegment}", StringComparison.Ordinal);
    }

    private static int ReadPositiveInt(IReadOnlyDictionary<string, object?> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || value is null)
        {
            return 0;
        }

        return value switch
        {
            int intValue when intValue > 0 => intValue,
            long longValue when longValue is > 0 and <= int.MaxValue => (int)longValue,
            double doubleValue when doubleValue > 0 && doubleValue <= int.MaxValue => (int)Math.Round(doubleValue),
            decimal decimalValue when decimalValue > 0 && decimalValue <= int.MaxValue => (int)Math.Round(decimalValue),
            _ => int.TryParse(value.ToString(), out var parsed) && parsed > 0 ? parsed : 0
        };
    }

    private static string ResolveClimatePresetMode(IReadOnlyDictionary<string, object?> values)
    {
        var defrostMode = ReadInt(values, "climate_state.defrost_mode", "defrost_mode");
        if (defrostMode == 2)
        {
            return "defrost";
        }

        var keeperMode = ReadString(values, "climate_state.climate_keeper_mode", "climate_keeper_mode");
        return keeperMode?.Trim().ToLowerInvariant() switch
        {
            "dog" => "dog",
            "camp" => "camp",
            "on" or "keep" => "keep",
            _ => "normal"
        };
    }

    private static string ResolveClimateFanMode(IReadOnlyDictionary<string, object?> values) =>
        ReadBool(values, "climate_state.bioweapon_mode", "bioweapon_mode") == true
            ? "bioweapon"
            : "off";

    private static string? ResolveCabinOverheatProtection(IReadOnlyDictionary<string, object?> values)
    {
        var value = ReadString(values, "climate_state.cabin_overheat_protection", "cabin_overheat_protection");
        return value?.Trim().ToLowerInvariant() switch
        {
            "on" => "On",
            "no a/c" or "no_ac" or "fan_only" => "No A/C",
            "off" => "Off",
            "" or null => null,
            _ => value
        };
    }

    private static string ResolveSteeringWheelHeat(IReadOnlyDictionary<string, object?> values)
    {
        if (ReadBool(values, "climate_state.auto_steering_wheel_heat", "auto_steering_wheel_heat") == true)
        {
            return "Auto";
        }

        var level = ReadInt(values, "climate_state.steering_wheel_heat_level", "steering_wheel_heat_level");
        if (level.HasValue)
        {
            return level.Value switch
            {
                <= 0 => "Off",
                1 => "Low",
                _ => "High"
            };
        }

        return ReadBool(values, "climate_state.steering_wheel_heater", "steering_wheel_heater") == true
            ? "High"
            : "Off";
    }

    private static string ResolveSeatClimate(IReadOnlyDictionary<string, object?> values, SeatClimateDefinition seat)
    {
        if (!string.IsNullOrWhiteSpace(seat.AutoPath) &&
            ReadBool(values, $"climate_state.{seat.AutoPath}", seat.AutoPath) == true)
        {
            return "Auto";
        }

        if (seat.IsFront && HasSeatCooling(values))
        {
            var coolerLevel = ReadInt(values, $"climate_state.{seat.CoolerPath}", seat.CoolerPath ?? string.Empty);
            if (coolerLevel is > 1)
            {
                return coolerLevel.Value switch
                {
                    2 => "Cool Low",
                    3 => "Cool Medium",
                    _ => "Cool High"
                };
            }
        }

        var heaterLevel = ReadInt(values, $"climate_state.{seat.HeaterPath}", seat.HeaterPath);
        if (heaterLevel is > 0)
        {
            return seat.IsFront && HasSeatCooling(values)
                ? heaterLevel.Value switch
                {
                    1 => "Heat Low",
                    2 => "Heat Medium",
                    _ => "Heat High"
                }
                : heaterLevel.Value switch
                {
                    1 => "Low",
                    2 => "Medium",
                    _ => "High"
                };
        }

        return "Off";
    }

    private static string[] ResolveFrontSeatOptions(IReadOnlyDictionary<string, object?> values) =>
        HasSeatCooling(values) ? FrontSeatCoolHeatOptions : FrontSeatHeatOptions;

    private static bool HasSeatCooling(IReadOnlyDictionary<string, object?> values) =>
        ReadBool(values, "vehicle_config.has_seat_cooling", "has_seat_cooling") == true ||
        values.Keys.Any(key => key.Contains("seat_fan_front_", StringComparison.OrdinalIgnoreCase));

    private static bool IsRearSeatEnabled(IReadOnlyDictionary<string, object?> values, string heaterPath) =>
        HasRawValue(values, heaterPath) ||
        ReadInt(values, "vehicle_config.rear_seat_heaters", "rear_seat_heaters") is > 0;

    private static bool IsThirdRowSeatEnabled(IReadOnlyDictionary<string, object?> values, string heaterPath)
    {
        if (HasRawValue(values, heaterPath))
        {
            return true;
        }

        var thirdRowSeats = ReadString(values, "vehicle_config.third_row_seats", "third_row_seats");
        return !string.IsNullOrWhiteSpace(thirdRowSeats) &&
               !thirdRowSeats.Equals("None", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVariableSteeringWheelHeatEnabled(IReadOnlyDictionary<string, object?> values) =>
        HasRawValue(values, "steering_wheel_heat_level") ||
        HasRawValue(values, "auto_steering_wheel_heat");

    private static bool IsSimpleSteeringWheelHeatEnabled(IReadOnlyDictionary<string, object?> values) =>
        HasRawValue(values, "steering_wheel_heater") &&
        !IsVariableSteeringWheelHeatEnabled(values);

    private static bool IsHomelinkEnabled(IReadOnlyDictionary<string, object?> values) =>
        ReadBool(values, "vehicle_state.homelink_nearby", "homelink_nearby") == true ||
        ReadInt(values, "vehicle_state.homelink_device_count", "homelink_device_count") is > 0;

    private static bool IsPedestrianSpeakerEnabled(IReadOnlyDictionary<string, object?> values) =>
        ReadBool(values, "vehicle_config.pedestrian_speaker", "pedestrian_speaker") == true;

    private static string ResolveChargePortLatchState(IReadOnlyDictionary<string, object?> values)
    {
        var latch = ReadString(values, "charge_state.charge_port_latch", "charge_port_latch");
        return latch?.Trim().Equals("Engaged", StringComparison.OrdinalIgnoreCase) == true
            ? "LOCKED"
            : "UNLOCKED";
    }

    private static bool HasRawValue(IReadOnlyDictionary<string, object?> values, string key) =>
        values.Any(item => NormalizeLookupKey(item.Key).EndsWith(NormalizeLookupKey(key), StringComparison.OrdinalIgnoreCase));

    private static bool? ResolveAnyOpen(IReadOnlyDictionary<string, object?> values, params string[] keys)
    {
        var found = false;
        foreach (var key in keys)
        {
            var value = ReadBool(values, key);
            if (!value.HasValue)
            {
                continue;
            }

            found = true;
            if (value.Value)
            {
                return true;
            }
        }

        return found ? false : null;
    }

    private static string? ToOpenClosedState(bool? isOpen) =>
        isOpen.HasValue
            ? isOpen.Value ? "open" : "closed"
            : null;

    private static string? ResolveSunroofState(IReadOnlyDictionary<string, object?> values)
    {
        var state = ReadString(values, "vehicle_state.sun_roof_state", "sun_roof_state");
        if (string.IsNullOrWhiteSpace(state))
        {
            return null;
        }

        return state.Equals("closed", StringComparison.OrdinalIgnoreCase) ? "closed" : "open";
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!values.TryGetValue(key, out var value) || value is null)
            {
                continue;
            }

            var text = value.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
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

            switch (value)
            {
                case double doubleValue:
                    return doubleValue;
                case float floatValue:
                    return floatValue;
                case decimal decimalValue:
                    return (double)decimalValue;
                case int intValue:
                    return intValue;
                case long longValue:
                    return longValue;
            }

            if (double.TryParse(value.ToString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static int? ReadInt(IReadOnlyDictionary<string, object?> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!values.TryGetValue(key, out var value) || value is null)
            {
                continue;
            }

            switch (value)
            {
                case int intValue:
                    return intValue;
                case long longValue when longValue is <= int.MaxValue and >= int.MinValue:
                    return (int)longValue;
                case double doubleValue when doubleValue is <= int.MaxValue and >= int.MinValue:
                    return (int)Math.Round(doubleValue);
                case decimal decimalValue when decimalValue is <= int.MaxValue and >= int.MinValue:
                    return (int)Math.Round(decimalValue);
            }

            if (double.TryParse(value.ToString(), out var parsed) &&
                parsed is <= int.MaxValue and >= int.MinValue)
            {
                return (int)Math.Round(parsed);
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

            switch (value)
            {
                case bool boolValue:
                    return boolValue;
                case int intValue:
                    return intValue != 0;
                case long longValue:
                    return longValue != 0;
                case double doubleValue:
                    return Math.Abs(doubleValue) > double.Epsilon;
            }

            var text = value.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (bool.TryParse(text, out var parsed))
            {
                return parsed;
            }

            if (text.Equals("on", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("open", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("opened", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("1", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (text.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("no", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("closed", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("close", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("0", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return null;
    }

    private static string BuildCommandTopic(string stateTopic, string action) =>
        stateTopic.EndsWith("/state", StringComparison.Ordinal)
            ? $"{stateTopic[..^"/state".Length]}/command/{action}"
            : $"{stateTopic.TrimEnd('/')}/command/{action}";

    private static string NormalizeLookupKey(string value)
    {
        var chars = (value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray();
        return new string(chars);
    }

    private static int ReadJsonArrayCount(object? value)
    {
        var text = value?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(text) || !text.StartsWith('['))
        {
            return 0;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(text);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
                ? document.RootElement.GetArrayLength()
                : 0;
        }
        catch (System.Text.Json.JsonException)
        {
            return 0;
        }
    }

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

    private static IEnumerable<HomeAssistantMqttEntityProjection> BuildPowerwallUnitRawEntities(
        string deviceId,
        string stateTopic,
        int index,
        IReadOnlyDictionary<string, object?> rawProperties)
    {
        foreach (var raw in BuildRawPropertyProjections(rawProperties).Where(raw => IsPowerwallUnitPath(raw.Path, index)))
        {
            yield return raw.Component.Equals("binary_sensor", StringComparison.OrdinalIgnoreCase)
                ? BinarySensor(
                    deviceId,
                    $"unit_{SafeId(raw.Path)}",
                    HumanizePowerwallUnitPath(raw.Path, index),
                    stateTopic,
                    $"{{{{ value_json.{raw.PayloadKey} | lower }}}}",
                    raw.DeviceClass,
                    enabledByDefault: false,
                    entityCategory: "diagnostic",
                    icon: raw.Icon)
                : Sensor(
                    deviceId,
                    $"unit_{SafeId(raw.Path)}",
                    HumanizePowerwallUnitPath(raw.Path, index),
                    stateTopic,
                    $"{{{{ value_json.{raw.PayloadKey} }}}}",
                    raw.DeviceClass,
                    raw.UnitOfMeasurement,
                    enabledByDefault: false,
                    entityCategory: "diagnostic",
                    stateClass: raw.StateClass,
                    icon: raw.Icon);
        }
    }

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

    private static string HumanizePowerwallUnitPath(string path, int index)
    {
        var normalized = path.ToLowerInvariant();
        foreach (var marker in new[]
                 {
                     $"battery_blocks.{index}.",
                     $"battery_units.{index}.",
                     $"powerwalls.{index}.",
                     $"batteries.{index}."
                 })
        {
            var markerIndex = normalized.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex >= 0)
            {
                return HumanizePath(path[(markerIndex + marker.Length)..]);
            }
        }

        return HumanizePath(path);
    }

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
