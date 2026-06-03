# LMS Tesla Fleet Helper Mapping

This add-on should keep Tesla API transport, LMS state, and Home Assistant MQTT projection separate.

## Flow

```text
Tesla API endpoint
  -> Tesla API DTO
  -> LMS normalized Tesla model
  -> Home Assistant MQTT projection
  -> MQTT Discovery config + retained state topic
```

Commands use a separate path:

```text
Home Assistant command topic
  -> LMS command request
  -> safety policy
  -> Tesla vehicle-command proxy request
  -> Tesla command response
  -> LMS command result state
  -> MQTT command status topic
```

## Layers

### Tesla API DTOs

DTOs live in `TeslaFleetApiDtos.cs`.

They mirror Tesla request/response payloads closely and use `JsonExtensionData` for fields that are not typed yet. DTOs are for parsing only and should not be published directly to Home Assistant.

Initial DTO coverage:

- products
- vehicle list
- vehicle data
- fleet status
- energy live status
- command request/response envelope

### LMS normalized model

Normalized records live in `TeslaFleetNormalizedModels.cs`.

This is the stable model the rest of the add-on should use:

- `LmsTeslaFleetState`
- `LmsTeslaVehicleState`
- `LmsTeslaChargeState`
- `LmsTeslaClimateState`
- `LmsTeslaDriveState`
- `LmsTeslaFleetKeyState`
- `LmsTeslaEnergySiteState`
- `LmsTeslaApiProperty`
- `LmsTeslaCommandDefinition`

The normalized model owns redaction, naming, type decisions, and future compatibility. It should tolerate Tesla changing field names better than MQTT or UI code can.

### Mapping

`TeslaFleetStateMapper` maps the current snapshot/harness shape into the normalized model.

The next step is to move the live `TeslaFleetDataClient` deserialization from loose `JsonElement`/dictionary parsing into the DTO records, then map DTOs into the same normalized model.

### Home Assistant MQTT projection

`HomeAssistantMqttProjectionMapper` maps normalized LMS state into:

- `HomeAssistantMqttDeviceProjection`
- `HomeAssistantMqttEntityProjection`
- `HomeAssistantMqttStateProjection`

This is intentionally separate from MQTTnet. Publishing is just transport; projection decides what HA should see.

## Endpoint Coverage Plan

Global endpoints:

- user profile
- user region
- products

Vehicle-scoped endpoints:

- vehicle list
- fleet status
- optional vehicle data for online vehicles
- command responses via vehicle-command proxy

Energy-scoped endpoints:

- energy site discovery from products
- site info/configuration
- live status
- later: energy history where supported

Command endpoints:

- initially disabled in HA
- enabled only after vehicle-command proxy integration
- safety policies required for lock/unlock/honk/window/trunk style actions

## Rule

Tesla DTOs should never feed MQTT directly. The only supported path is:

```text
DTO -> LMS normalized model -> MQTT projection
```
