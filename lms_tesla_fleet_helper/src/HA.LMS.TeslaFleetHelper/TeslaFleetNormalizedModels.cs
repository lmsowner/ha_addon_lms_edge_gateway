sealed record LmsTeslaFleetState(
    DateTimeOffset CapturedUtc,
    LmsTeslaUserState User,
    IReadOnlyList<LmsTeslaVehicleState> Vehicles,
    IReadOnlyList<LmsTeslaEnergySiteState> EnergySites,
    IReadOnlyList<LmsTeslaApiProperty> Properties);

sealed record LmsTeslaUserState(
    string Region,
    string FleetApiAudience);

sealed record LmsTeslaVehicleState(
    string Vin,
    string Id,
    string DisplayName,
    string ConnectivityState,
    LmsTeslaChargeState Charge,
    LmsTeslaClimateState Climate,
    LmsTeslaDriveState Drive,
    LmsTeslaVehicleMetaState Meta,
    LmsTeslaFleetKeyState FleetKey,
    IReadOnlyDictionary<string, object?> RawProperties);

sealed record LmsTeslaChargeState(
    int? BatteryLevelPercent,
    int? UsableBatteryLevelPercent,
    string ChargingState,
    int? ChargeLimitPercent,
    double? BatteryRange,
    string ConnectedChargeCable,
    bool? FastChargerPresent,
    bool? ChargePortDoorOpen);

sealed record LmsTeslaClimateState(
    double? InsideTemperatureCelsius,
    double? OutsideTemperatureCelsius,
    bool? IsClimateOn,
    double? DriverTemperatureSettingCelsius,
    double? PassengerTemperatureSettingCelsius);

sealed record LmsTeslaDriveState(
    double? Latitude,
    double? Longitude,
    int? Heading,
    double? Speed,
    string ShiftState);

sealed record LmsTeslaVehicleMetaState(
    string FirmwareVersion,
    double? Odometer,
    bool? Locked,
    bool? SentryMode);

sealed record LmsTeslaFleetKeyState(
    bool? VehicleCommandProtocolRequired,
    int? TotalNumberOfKeys,
    bool? KeyPaired,
    string Status);

sealed record LmsTeslaEnergySiteState(
    string SiteId,
    string DisplayName,
    string ResourceType,
    LmsTeslaEnergySiteCapabilities Capabilities,
    LmsTeslaEnergyLiveState Live,
    IReadOnlyDictionary<string, object?> RawProperties);

sealed record LmsTeslaEnergySiteCapabilities(
    bool? HasSolar,
    bool? HasBattery,
    bool? HasGrid,
    bool? HasBackup,
    bool? HasLoadMeter,
    string GatewayType,
    string BatteryType,
    int? PowerwallCount,
    double? NameplateEnergyWh);

sealed record LmsTeslaEnergyLiveState(
    string GridStatus,
    int? BatteryPercentage,
    double? SolarPowerWatts,
    double? LoadPowerWatts,
    double? BatteryPowerWatts,
    double? GridPowerWatts,
    double? GeneratorPowerWatts,
    int? BackupReservePercent,
    double? EnergyRemainingWh,
    string IslandStatus,
    bool? GridServicesActive,
    bool? StormModeActive);

sealed record LmsTeslaApiProperty(
    string Scope,
    string ResourceId,
    string ResourceName,
    string Path,
    string ValueType,
    object? Value,
    string SuggestedEntityType,
    string? SuggestedDeviceClass,
    string? SuggestedUnit);

sealed record LmsTeslaCommandDefinition(
    string Id,
    string DisplayName,
    string CommandName,
    string SafetyLevel,
    IReadOnlyList<LmsTeslaCommandParameterDefinition> Parameters);

sealed record LmsTeslaCommandParameterDefinition(
    string Name,
    string ValueType,
    bool Required,
    object? DefaultValue);
