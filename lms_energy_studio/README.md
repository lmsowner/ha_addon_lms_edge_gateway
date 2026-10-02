# LMS Energy Studio

Energy Studio is a separate .NET 10 companion app. Edge Gateway continues to publish applications; Tesla Fleet Helper remains independent. All supplied photographs, topology animation, layout controls, battery instruments and explicit demo scenarios are bundled locally.

Home Assistant release version: **2026.10.02.18.44**. Energy Studio is published from `main` using the same architecture-specific GHCR pattern as Edge Gateway. The publish workflow builds and smoke-tests each image before pushing. Image publishing runs in the background after the version commit; local Docker and real Supervisor validation remain unverified.

## Install from your Home Assistant repository

1. Open Settings → Apps → App store. Your existing repository URL is `https://github.com/lmsowner/ha_addon_lms_edge_gateway`.
2. Use **Check for updates** / reload the store repository so Home Assistant fetches the new `main` metadata.
3. Select **LMS Energy Studio**, version **2026.10.02.18.44**, and install. HA pulls `ghcr.io/lmsowner/lms_energy_studio-{arch}:2026.10.02.18.44` instead of building locally.
4. If the version appears before its background image publish has finished, retry installation once publishing completes. Images are built for amd64 and aarch64.
5. Start it and open **Web UI** through ingress. No HA token is entered in the browser or app options: the host uses Supervisor's server-side token and Core REST/WebSocket proxies.
6. Use **Customise** for physical devices, then **Home Assistant entities** for readings. Configure and review your real contract under **Daily energy history → Tariff** before enabling costing; defaults start unconfirmed.

For source-only local testing, copy the complete `lms_energy_studio` directory into `/addons/lms_energy_studio` and remove `image` from that local manifest to request a Supervisor source build.

Data is stored atomically in `/data/lms-energy-studio/configuration.json`. Back up app data using HA's app backup. Tokens are never part of this file. No HA service/control calls are exposed.

## Local development

```sh
export EnergyStudio__HomeAssistantUrl=http://your-ha:8123
# Set EnergyStudio__HomeAssistantToken in your shell or secret store; do not commit it.
export EnergyStudio__DataRoot="$PWD/local-energy-data"
ASPNETCORE_ENVIRONMENT=Development dotnet run --project lms_energy_studio/src/HA.LMS.EnergyStudio --urls http://127.0.0.1:5066
```

Open `http://localhost:5066/`. Development bypass applies only to loopback peers. Without HA credentials the host runs with disconnected/null readings, not mock telemetry. In Rider, choose **Start Energy Studio** or the project's **Energy Studio** launch profile.

The optional Compose service uses `ENERGY_STUDIO_HA_URL`, `ENERGY_STUDIO_HA_TOKEN` and `ENERGY_STUDIO_ACCESS_TOKEN`. Docker bridge clients are not loopback from inside the container: use an authenticated proxy with a server-injected bearer `EnergyStudio__AccessToken` (at least 32 characters), or run the project directly for browser development. Never put that token in client JavaScript. Production has no local bypass.

## Mapping and quality

- Search friendly names, entity IDs, devices and areas; filter by device/area and measurement units. Offline entities remain selectable. Previews show raw and converted values; the availability panel shows source timestamps and qualities.
- Use signed grid (import minus export), signed battery (discharge minus charge), or both separate directions. Do not map signed and split readings simultaneously. EV power is charging minus export; negative power requires both vehicle and charger V2G capability.
- Attribute mappings use `/attributes/...` JSON pointers, including array indexes, and explicit units. Configure actual BMS cell IDs first. A pointer template such as `/attributes/cells/{index}/voltage` can populate pending mappings for all cells; review the previews before saving.
- Active balancing routes require explicit configured endpoints and mapped reported activity/current. No routes are inferred from cell voltage. Passive balancing is drawn to heat; unavailable cell SoC stays unknown.
- Pack capacity can be mapped, or explicitly confirmed as your own configured metadata. Defaults are never taken as measured live capacity. Pack current is positive charging; balancing current is positive receiving.
- Enable the explicit home-meter subtraction rule only if that meter includes EV demand and battery charging. Every subtracted reading must be valid. Inverter AC output stays independently measured; arrays and DC storage are not counted again in AC residuals.
- Unknown/unavailable/missing/stale/invalid-unit/disconnected values are null. Optional max-age policies use `last_reported`, falling back to `last_updated`; the shared snapshot refresh updates unchanged reported values. Derived aggregates reject timestamp skew over 120 seconds. Residual/unmetered/losses are shown separately, with a configurable meter tolerance.
- Registry IDs follow entity renames. Deleted or ambiguous registry identities produce `repair-required`; the picker must be used to repair them. Revision conflicts return 409 and require reload rather than overwriting another browser's changes.

Layout and mappings are server-owned. Demo scenes explicitly suspend live rendering and do not save scenario telemetry. **Live Home Assistant** resumes measured rendering. Sample picker mappings never write to the server. Export contains only layout data, not credentials.

## Recorded history and tariffs

Recorder REST history is fetched server-side for mapped readings only, bounded to seven days per request. Energy state counters fall back to hourly statistics sums when raw history is absent. Only fully bounded intervals are totalled. Energy counters take priority over corresponding power readings in the browser; counters use deltas and skip resets. Power integration is a labelled estimate using actual elapsed time, with no bridge across unknown samples or gaps over the sensor policy (300 seconds by default). Independent/overlapping meter categories are shown separately, never summed into a misleading consumption total. BMS history contains actual timestamped observations, with gaps rather than today's value repeated backwards.

Daily bounds follow the configured tariff timezone, including 23/25-hour DST days. The production history view retains UTC timestamps and displays local offsets, replacing the demo's 24-hour assumptions. Costs are applied only to metered grid imports. Energy is apportioned by elapsed time where a counter interval straddles a price boundary, so interval cost allocation is explicitly an estimate. Missing dynamic prices stay unpriced; priced kWh and coverage are reported. Standing charges/export credits are excluded and labelled separately from import cost.

Fixed peak/off-peak windows can cross midnight. Dated extra sessions and negative dynamic rates are supported. UTC intervals have `{start,end,price,status,scope}`; scope is `home` or `ev` and status `confirmed` or `scheduled`. Scheduled and EV-only cheap sessions never lower whole-home import cost.

Supplier integrations differ by version. The tariff editor accepts reviewed attribute adapters selected by entity and pointer; no Octopus sensor name/schema is assumed. Each adapter specifies:

```json
{
  "entityId": "sensor.your_supplier_rates",
  "pointer": "/attributes/rates",
  "sourceUnit": "GBP/kWh",
  "startField": "start",
  "endField": "end",
  "priceField": "value",
  "statusField": "status",
  "scope": "home"
}
```

Alternatively use an explicitly reviewed `status: "confirmed"` / `"scheduled"` policy when the integration's entries have no status field. Start/end must contain an offset or `Z`; ambiguous local timestamps are rejected. Applied entries become confirmed. Unknown schemas, unsupported status values and unavailable supplier readings yield no intervals. Whole-home eligibility must be explicitly verified from the installed integration/contract. Separate EV billing is explicitly enabled per stable vehicle ID using its recorded energy counter. Optional `sessionCapKwh` limits eligible cheap-session energy per local day; excess uses the configured base rate, or remains unpriced when that rate is missing. Capped requests must start at local midnight. These costs remain separate estimates, never added to metered home import or presented as a supplier invoice. Caps and whole-home eligibility are configured from the actual contract, never inferred.

## Ingress and optional Edge publishing

All assets/API/SSE URLs are relative to the dashboard's trailing-slash URL. Only Supervisor's ingress peer (`172.30.32.2`) can set `X-Ingress-Path`. Nested published paths are configured using `public_path_prefix`; they are removed before ASP.NET routing. Health checks expose only process health.

For optional Edge publishing, use the existing Gateway authenticated route workflow, set **Requires authentication**, preserve Host, and route to the app's internal address on port 5066. Configure `edge_gateway_proxy_ip` as the exact Caddy peer IP and `public_path_prefix` to its published path (e.g. `/energy`). Energy Studio accepts the existing `X-LMS-User` auth result only from that configured peer. Leave the peer setting empty for ingress-only installations; keep this upstream private and do not point an unauthenticated route at it. Do not trust arbitrary forwarded headers. No Cloudflare/Tesla logic or gateway publishing implementation was moved into this app.

For a standalone authenticated reverse proxy, inject a server-side bearer `EnergyStudio__AccessToken`. Browser writes also require JSON, a same-origin Origin where supplied, and `X-Energy-Studio: 1`. There is no browser HA proxy and no service-call API.

## Checks and validation limits

```sh
dotnet build HA.LMS.EdgeGateway.slnx -c Release
dotnet test lms_energy_studio/tests/LMS.Energy.Core.Tests -c Release
npm ci --prefix lms_energy_studio/tests --ignore-scripts
npm test --prefix lms_energy_studio/tests
python3 lms_energy_studio/tests/host-smoke.py
```

Verified locally: full solution build; Energy Studio core/connection tests; dashboard DOM and mapping checks; nested HTML/assets/API/SSE; configuration conflicts, CSRF and forged ingress/proxy rejection. The HA protocol fixture exercises authentication, initial-state event races, registry metadata, rename resolution and reconnect. It is test infrastructure, not a production mock backend.

The existing Edge Gateway suite has three failures in unchanged tests: `Route_auth_skips_mfa_when_cloudflare_connecting_ip_is_ipv4_mapped`, `Route_auth_skips_mfa_for_configured_known_source_ip`, and `Temporary_ip_denial_diagnostics_explain_known_ip_skip_off` (144 other tests passed).

Not validated: real Supervisor ingress, Recorder retention/exclusion behavior, installed supplier schemas, actual BMS/V2G hardware, simultaneous real browsers, rendered browser visual QA (computer-use timed out), and target Docker images (local Docker daemon unavailable). There is no live HA credential/connection in this session. When raw counter history is absent, supported energy-state mappings fall back to hourly reset-adjusted statistics sums in kWh. Statistics API/version behavior still requires live validation; attribute-only histories have no statistics fallback. Unavailable history remains empty. The GHCR publication workflow includes amd64/aarch64 image builds and health/access smoke tests before each architecture is pushed. Publication status is not polled after the version push, following this repository’s testing-release convention.

Protocol references: [HA WebSocket API](https://developers.home-assistant.io/docs/api/websocket/), [REST/Recorder history](https://developers.home-assistant.io/docs/api/rest/), [Supervisor app communication](https://developers.home-assistant.io/docs/apps/communication/).
