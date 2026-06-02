using System.Text.Json;
using System.Text.Json.Serialization;

sealed record TeslaApiEnvelope<T>(
    [property: JsonPropertyName("response")] T? Response,
    [property: JsonPropertyName("count")] int? Count);

sealed record TeslaApiProductsResponse(
    [property: JsonPropertyName("response")] List<TeslaApiProductDto> Response,
    [property: JsonPropertyName("count")] int? Count);

sealed record TeslaApiProductDto(
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("id_s")] string? IdString,
    [property: JsonPropertyName("vehicle_id")] long? VehicleId,
    [property: JsonPropertyName("vin")] string? Vin,
    [property: JsonPropertyName("display_name")] string? DisplayName,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("resource_type")] string? ResourceType,
    [property: JsonPropertyName("energy_site_id")] string? EnergySiteId,
    [property: JsonPropertyName("site_name")] string? SiteName,
    [property: JsonExtensionData] Dictionary<string, JsonElement>? ExtensionData = null);

sealed record TeslaApiVehicleListResponse(
    [property: JsonPropertyName("response")] List<TeslaApiVehicleSummaryDto> Response,
    [property: JsonPropertyName("count")] int? Count);

sealed record TeslaApiVehicleSummaryDto(
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("id_s")] string? IdString,
    [property: JsonPropertyName("vehicle_id")] long? VehicleId,
    [property: JsonPropertyName("vin")] string? Vin,
    [property: JsonPropertyName("display_name")] string? DisplayName,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("in_service")] bool? InService,
    [property: JsonExtensionData] Dictionary<string, JsonElement>? ExtensionData = null);

sealed record TeslaApiVehicleDataResponse(
    [property: JsonPropertyName("response")] TeslaApiVehicleDataDto? Response);

sealed record TeslaApiVehicleDataDto(
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("id_s")] string? IdString,
    [property: JsonPropertyName("vehicle_id")] long? VehicleId,
    [property: JsonPropertyName("vin")] string? Vin,
    [property: JsonPropertyName("display_name")] string? DisplayName,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("charge_state")] TeslaApiVehicleChargeStateDto? ChargeState,
    [property: JsonPropertyName("climate_state")] TeslaApiVehicleClimateStateDto? ClimateState,
    [property: JsonPropertyName("drive_state")] TeslaApiVehicleDriveStateDto? DriveState,
    [property: JsonPropertyName("vehicle_state")] TeslaApiVehicleVehicleStateDto? VehicleState,
    [property: JsonExtensionData] Dictionary<string, JsonElement>? ExtensionData = null);

sealed record TeslaApiVehicleChargeStateDto(
    [property: JsonPropertyName("battery_level")] int? BatteryLevel,
    [property: JsonPropertyName("usable_battery_level")] int? UsableBatteryLevel,
    [property: JsonPropertyName("charging_state")] string? ChargingState,
    [property: JsonPropertyName("charge_limit_soc")] int? ChargeLimitSoc,
    [property: JsonPropertyName("battery_range")] double? BatteryRange,
    [property: JsonPropertyName("est_battery_range")] double? EstimatedBatteryRange,
    [property: JsonPropertyName("ideal_battery_range")] double? IdealBatteryRange,
    [property: JsonPropertyName("conn_charge_cable")] string? ConnectedChargeCable,
    [property: JsonPropertyName("fast_charger_present")] bool? FastChargerPresent,
    [property: JsonPropertyName("charge_port_door_open")] bool? ChargePortDoorOpen,
    [property: JsonExtensionData] Dictionary<string, JsonElement>? ExtensionData = null);

sealed record TeslaApiVehicleClimateStateDto(
    [property: JsonPropertyName("inside_temp")] double? InsideTemperatureCelsius,
    [property: JsonPropertyName("outside_temp")] double? OutsideTemperatureCelsius,
    [property: JsonPropertyName("is_climate_on")] bool? IsClimateOn,
    [property: JsonPropertyName("driver_temp_setting")] double? DriverTemperatureSettingCelsius,
    [property: JsonPropertyName("passenger_temp_setting")] double? PassengerTemperatureSettingCelsius,
    [property: JsonExtensionData] Dictionary<string, JsonElement>? ExtensionData = null);

sealed record TeslaApiVehicleDriveStateDto(
    [property: JsonPropertyName("latitude")] double? Latitude,
    [property: JsonPropertyName("longitude")] double? Longitude,
    [property: JsonPropertyName("heading")] int? Heading,
    [property: JsonPropertyName("speed")] double? Speed,
    [property: JsonPropertyName("shift_state")] string? ShiftState,
    [property: JsonExtensionData] Dictionary<string, JsonElement>? ExtensionData = null);

sealed record TeslaApiVehicleVehicleStateDto(
    [property: JsonPropertyName("car_version")] string? CarVersion,
    [property: JsonPropertyName("locked")] bool? Locked,
    [property: JsonPropertyName("sentry_mode")] bool? SentryMode,
    [property: JsonPropertyName("odometer")] double? Odometer,
    [property: JsonExtensionData] Dictionary<string, JsonElement>? ExtensionData = null);

sealed record TeslaApiFleetStatusRequest(
    [property: JsonPropertyName("vins")] IReadOnlyList<string> Vins);

sealed record TeslaApiFleetStatusResponse(
    [property: JsonPropertyName("response")] JsonElement Response);

sealed record TeslaApiEnergyLiveStatusResponse(
    [property: JsonPropertyName("response")] TeslaApiEnergyLiveStatusDto? Response);

sealed record TeslaApiEnergyLiveStatusDto(
    [property: JsonPropertyName("grid_status")] string? GridStatus,
    [property: JsonPropertyName("percentage_charged")] int? PercentageCharged,
    [property: JsonPropertyName("battery_percentage")] int? BatteryPercentage,
    [property: JsonPropertyName("solar_power")] double? SolarPowerWatts,
    [property: JsonPropertyName("load_power")] double? LoadPowerWatts,
    [property: JsonPropertyName("battery_power")] double? BatteryPowerWatts,
    [property: JsonPropertyName("grid_power")] double? GridPowerWatts,
    [property: JsonPropertyName("backup_reserve_percent")] int? BackupReservePercent,
    [property: JsonExtensionData] Dictionary<string, JsonElement>? ExtensionData = null);

sealed record TeslaApiCommandRequest(
    string CommandName,
    string VehicleIdentifier,
    IReadOnlyDictionary<string, object?> Parameters);

sealed record TeslaApiCommandResponse(
    [property: JsonPropertyName("response")] TeslaApiCommandResultDto? Response);

sealed record TeslaApiCommandResultDto(
    [property: JsonPropertyName("result")] bool? Result,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonExtensionData] Dictionary<string, JsonElement>? ExtensionData = null);
