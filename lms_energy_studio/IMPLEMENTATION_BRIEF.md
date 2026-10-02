# Implement LMS Energy Studio in the LMS Home Assistant repository

## Repository evidence and intended product

Inspected `lmsowner/ha_addon_lms_edge_gateway` at commit `7ca1b9f22bd98cf9c2006d7baf56a40561340546` on 2 October 2026. Recheck the current branch and all local `AGENTS.md` instructions before editing; the user's Rider checkout may be ahead of this baseline.

This repository already contains two companion apps. `lms_edge_gateway` is a Blazor host backed by `LMS.EdgeGateway.Core` and `LMS.Shared`. `lms_tesla_fleet_helper` is a separate .NET 10 app, not a generic extension mechanism inside Edge Gateway. Its `Program.cs` is a minimal ASP.NET host with its own UI; its `config.yaml`, Dockerfile and rootfs scripts package it for HA ingress. `HA.LMS.EdgeGateway.slnx` includes both apps. Build Energy Studio as the third app with the same companion relationship. Do not put energy/BMS logic into the gateway, copy its Cloudflare orchestration or invent an extension interface that does not exist.

Use:

- `lms_energy_studio/src/LMS.Energy.Core`: reusable models, binding validation, measurement conversion, quality and energy/history/tariff logic.
- `lms_energy_studio/src/HA.LMS.EnergyStudio`: .NET 10 ASP.NET host, HA transport/coordinator, ingress and persistent configuration; the supplied `wwwroot` is the complete UI.
- `lms_energy_studio/tests/LMS.Energy.Core.Tests`: meaningful conversion, topology, history, tariff and connection-lifecycle tests.
- `lms_energy_studio/config.yaml`, `Dockerfile`, `build.yaml`, rootfs services and translations: follow the sibling app's packaging after the host exists.

The proposed slug is `lms_energy_studio`; choose port 5066 after checking conflicts. Add the projects to the current solution and add an optional local Compose service/Rider run configuration without replacing existing entries. Use amd64/aarch64 builds. Store server-owned configuration under `/data/lms-energy-studio`. Grant `homeassistant_api: true`; request other permissions only when a implemented feature requires them. HA ingress is the primary access path. Edge Gateway may publish the companion app through its existing authenticated application workflow.

## Keep this dashboard

Preserve the photo-led topology, green/red directional flows, multiple arrays/inverters/storage/chargers/cars, named household loads, battery cell analysis, clickable cell-history traces, cylindrical SoC instrument and balancing pulses. Preserve reduced motion and Pause. No visual redesign or framework replacement is needed to start.

Package the supplied assets locally. The existing client entry points are `LMSEnergy.configure`, `updateTelemetry`, `updateEnergyHistory`, `getConfig`, `getState` and `resumeDemo`. Their semantics are described in `DASHBOARD_REFERENCE.md`. Keep live mode and explicitly requested demo mode distinct. Remove misleading permanent DEMO labels in live mode, show connection freshness, and never create fictitious BMS details or history because a live reading is absent.

## Entity picker: the primary setup workflow

Users configure their physical devices first, then choose HA readings for each device through **Home Assistant entities**. Reuse `ha-picker.js`/`ha-bindings.js` or port the controls into the existing LMS UI components while preserving their behaviour. Provide:

1. Search by friendly name, entity ID, device and area; group by HA device/area.
2. Filter by measurement role and compatible units. Power cannot select a kWh counter. Include unavailable candidates so a device can be configured while offline.
3. Display raw value, raw unit, normalised value, availability/freshness and history capabilities.
4. Map entity state or a validated JSON-pointer attribute path, including array entries such as `/attributes/cells/0/voltage`. A unit override is required when an attribute lacks unit metadata.
5. Explicit numeric sign inversion, signed-net or separate directional readings, and optional freshness policy. No handwritten entity names are required for the usual workflow.
6. Persist mappings and layout together server-side with a version/revision guard. All browsers see the same configuration; browser localStorage is not the production source of truth.
7. Store the registry entry ID, entity ID and device metadata where available. Resolve a registry rename through that identity and refresh the entity ID; if identity is missing/ambiguous, flag a repair instead of binding a different sensor by name.
8. Optional device/cell suggestions must require review. Never silently auto-bind because an entity name happens to contain “battery” or a cell number.

The supplied reference generates fields from the actual dashboard's stable IDs. Extend it for split import/export, split charge/discharge and bulk cell-array mappings. Inverter AC output is a displayed measurement, not a second generation input. Select battery capacity/nominal voltage from available entities or explicit metadata, not guessed model constants.

## Transport and security

Use a server-side Home Assistant connection. Under Supervisor, the documented Core REST proxy is `http://supervisor/core/api/` and WebSocket proxy is `ws://supervisor/core/websocket`. Authenticate with `SUPERVISOR_TOKEN` server-side. Local development may use an explicitly configured HA URL and environment-provided token. Never place tokens in JavaScript, config exports, browser storage or logs.

Maintain one HA connection/coordinator per app instance, not one connection per browser/component. Authenticate; subscribe to state changes; establish the initial state snapshot without losing events that arrive during bootstrap. Load device, area and entity registry metadata through supported HA commands. Correlate request IDs, handle unsuccessful results, ping/pong and reconnect with backoff. Resynchronise state after reconnect; registry changes update the catalogue/mapping identities. Fan out only mapped, normalised readings to browser clients via the existing SignalR pattern or SSE, and dispose subscriptions cleanly.

All UI/API/assets/live transports must work below the HA ingress path and any Edge Gateway path prefix. The prototype deliberately uses relative asset/API URLs. Implement and test the backend's base-path handling using the repository's verified pattern; do not trust forwarded ingress headers from arbitrary callers. Reuse the established ingress/external authentication boundary. Do not expose an unauthenticated writable configuration endpoint or add an open HA proxy. Validate mapping targets server-side against the fetched catalogue and reject arbitrary URLs/service calls. This initial extension is monitoring, not device control.

## Measurements and partial configuration

Keep power (kW), energy (kWh), voltage (V), current (A), capacity (Ah), SoC (%), resistance (Ω) and currency rates distinct. Convert units at one boundary. `unknown`, `unavailable`, non-finite/empty values, missing attributes and disconnected states yield quality information plus `null`, never zero. A constant reading need not be stale merely because it has not changed: use `last_reported`/connection observation and an optional sensor-specific freshness policy, not `last_changed` alone.

Conventions:

- Grid net = import − export; positive import, negative export.
- Battery net = discharge − charge; positive supplying, negative charging.
- EV net = charging − export; V2G also requires configured vehicle and charger capability.
- Pack BMS current = positive charging. Cell balancing current = positive receiving, negative giving/bleeding. These are different measurements.
- Home demand excludes EVs and battery charging. If a chosen meter includes them, make subtraction an explicit, quality-checked mapping rule, not a hidden assumption.

The supplied demo `updateTelemetry` currently requires all aggregates and balance within 50 W. **Do not feed real data through this gate by inventing zeros or altering measured readings.** Add a production quality-aware snapshot contract and progressive rendering. Display configured components independently when optional readings are absent. Show stale/unmapped status and suppress unsupported flows. Use explicitly labelled derived totals only when every contributing measurement is valid. Display residual/unmetered/losses separately and support configurable meter tolerance. Preserve independently measured inverter net output; do not overwrite it with ideal lossless arithmetic. Aggregating readings from different timestamps needs freshness/skew checks.

BMS pack/cell detail remains capability-driven. Cell voltage does not imply cell SoC. Only reported active balancing routes animate from a giver to a receiver; otherwise show reported per-cell activity without inventing endpoints. Passive balancing goes to a resistor/heat. Cells/pack history must use recorded readings, with gaps and timestamps, not today's live value repeated over a past day.

## Recorded energy and tariff sessions

Use HA Recorder/statistics through supported APIs. Prefer valid per-load accumulated energy counters for kWh; handle counter resets and statistics sum semantics. Integrate power only as an explicitly labelled estimate with actual elapsed time, no interpolation across outages and no double-counting cumulative totals. Aggregate household loads separately from each EV and battery charging. Preserve time-zone/DST intervals and exact tariff boundaries. The demo daily renderer currently uses a 24-hour wall-clock timeline; extend its time model to unambiguous UTC instants and render repeated/missing local hours correctly before claiming production tariff accuracy.

Current tariff configuration supports daily peak/off-peak windows, dated whole-home sessions and dynamic interval prices, including negatives. Its six-hour 23:30–05:30 preset is illustrative. Build supplier adapters around published attributes/events/response shapes from each installed HA integration, selected by users. Do not assume a single Octopus entity ID or attribute schema. Some HA supplier integrations are third-party and versioned separately. Confirm the actual installed integration's schema and distinguish scheduled versus confirmed/applied sessions and EV-only versus whole-home eligibility. Sessions must not automatically cover the entire house merely because an EV started charging. Support separate EV billing/caps when the contract requires it.

Costs use metered grid imports, not all consumption, and maintain priced-energy coverage. Show missing prices, estimated costs, standing charges/export credits separately. Never silently fill missing dynamic rates with a fixed rate. Backend should supply confirmed price/session intervals and measured daily/BMS histories to the client.

## Delivery and checks

Read the latest repo, implement on a feature branch and keep source/assets local; the hosted ChatGPT demo is a design reference, not a runtime dependency. Run existing repository checks and new .NET tests, then verify the UI through HA ingress on a test HA instance. Test nested prefix asset/API/live paths, reconnect/bootstrap races, unavailable/deleted/renamed entities, multi-browser mapping conflicts, multiple inverters and batteries, partial mappings, BMS arrays, passive/active balancing, V2G signs and Recorder gaps. Test negative dynamic prices, bonus sessions, fixed midnight windows, counter resets and both daylight-saving changes. Confirm no HA/Cloudflare/Tesla tokens reach the client.

Update repo README/solution/Compose/build workflow using current templates. Do not claim an installable add-on until its .NET build and target image pass. Return the concrete implementation, what was tested, and any remaining live-integration limitation; do not stop after writing a plan.

## Sources used to ground this brief

- Repository README: https://github.com/lmsowner/ha_addon_lms_edge_gateway/blob/7ca1b9f22bd98cf9c2006d7baf56a40561340546/README.md
- Tesla app manifest: https://github.com/lmsowner/ha_addon_lms_edge_gateway/blob/7ca1b9f22bd98cf9c2006d7baf56a40561340546/lms_tesla_fleet_helper/config.yaml
- Solution: https://github.com/lmsowner/ha_addon_lms_edge_gateway/blob/7ca1b9f22bd98cf9c2006d7baf56a40561340546/HA.LMS.EdgeGateway.slnx
- HA app communication: https://developers.home-assistant.io/docs/apps/communication/
- HA WebSocket API: https://developers.home-assistant.io/docs/api/websocket/
- HA REST/history API: https://developers.home-assistant.io/docs/api/rest/
- HA entity registry: https://developers.home-assistant.io/docs/entity_registry_index/
