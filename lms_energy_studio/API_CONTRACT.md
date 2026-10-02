# Proposed Energy Studio browser/backend contract

These endpoints are a contract for Rider/Codex to implement, not existing HA endpoints. The supplied picker calls them relative to the dashboard base URL. Serve JSON using camelCase. Backend owns HA credentials and persistent configuration.

## GET `api/energy/entities`

Return a cached, merged state/registry catalogue. Refresh values through the server coordinator; do not make each browser fetch every state from HA independently.

```json
[
  {
    "entity_id": "sensor.example_bms",
    "registry_id": "stable-ha-registry-entry-id",
    "device_id": "ha-device-id",
    "device_name": "Battery BMS",
    "area_name": "Battery room",
    "state": "online",
    "last_updated": "2026-10-02T10:00:00Z",
    "last_reported": "2026-10-02T10:00:02Z",
    "attributes": {
      "friendly_name": "Battery BMS",
      "cells": [{"voltage": 3.32, "current": -800}]
    }
  }
]
```

Metadata may be null. Include state units, device_class and state_class when available. Supplement with history/statistics capability metadata. Large attributes should be bounded/sanitised, not arbitrary entity secrets or unrelated HA settings.

## GET / PUT `api/energy/config`

```json
{
  "revision": 3,
  "layout": {"name": "My home", "cars": 0, "storage": [], "loads": []},
  "cells": {"battery-stable-id": ["cell-1", "cell-2"]},
  "mappings": {
    "home": {"entityId": "sensor.example_home_power", "invert": false},
    "cells.battery-stable-id.cell-1.voltage": {
      "entityId": "sensor.example_bms",
      "pointer": "/attributes/cells/0/voltage",
      "sourceUnit": "V",
      "invert": false,
      "maxAgeSeconds": 120
    }
  }
}
```

`layout` is the full `LMSEnergy.getConfig()` shape, not the abbreviated example above. Key mappings by dashboard device IDs, not by array position or a displayed name. Reference EV keys currently use indexes; migrate to persistent vehicle IDs in the production model.

PUT includes `expectedRevision`. Validate mappings/units/pointers against the server catalogue, validate layout counts/IDs, reject missing/stale revision with HTTP 409 and atomically persist a revisioned document. Return the new full configuration including its incremented revision. Do not persist sample-catalogue mappings. Persist registry identity alongside the friendly entity ID so a renamed entity can be repaired correctly. Reject token/URL fields and unexpected configuration properties. Ensure writes use the existing authenticated ingress/external boundary and appropriate same-origin/CSRF protection.

## Live snapshots and history

Implement an authenticated, ingress-safe SignalR/SSE route for quality-aware snapshots. This needs a new production renderer adapter; it is deliberately not supplied as a fake call to the strict balanced demo API.

Every measurement carries `{value: number|null, unit, quality, entityId, observedAt, measuredAt}`. Quality includes good, unmapped, missing, unavailable, stale, invalid-unit and derived. Snapshot also carries connection state, topology IDs and residual/unmetered power. Update mapped values from one server coordinator, coalesce bursts and retain freshness/quality. Optional values do not block independent devices.

Recorded daily energy, BMS cell/pack histories and dated tariff intervals are fetched from dedicated backend endpoints with time bounds and source metadata. Adapt these to the existing frontend entry points where the data model permits; extend the wall-clock daily model for DST and real aggregate residuals rather than hiding these limitations.

No monitoring endpoint invokes HA services. Future commands require a separate explicit capability/confirmation model.


## Implemented host contract (feature branch)

The prototype routes above are now implemented in `src/HA.LMS.EnergyStudio`. Production snapshots use `GET api/energy/snapshot` and SSE `GET api/energy/live`. Both return connection/revision/observation metadata, independently quality-tagged readings and an AC-bus residual. HTTP PUT requires `X-Energy-Studio: 1`, JSON, same-origin access and `expectedRevision`; conflicts return 409. Unexpected DTO properties and incompatible mappings are rejected. GET config resolves stored registry identities to current entity IDs.

EV mapping keys now use `vehicle-1` or other persistent vehicle IDs rather than array positions. Additional fields include grid import/export power, battery charge/discharge power and energy, chargers, cell SoC, and explicitly reported balancing routes. Layout `accounting.homeIncludesEvAndCharging` and `toleranceKw` define reviewed subtraction/tolerance policy.

`GET api/energy/history?key=...&start=<offset timestamp>&end=<offset timestamp>` returns raw quality-tagged points, source metadata, bounded energy intervals, covered seconds, total/priced kWh and optional estimated costs in minor currency units. It accepts mapped targets only, with at most seven days per request. Counter histories can fall back to HA hourly reset-adjusted statistics sums; attribute histories stay Recorder-only. Categories are independent. Only metered import or explicitly enabled separate EV billing receives costs.

`GET api/energy/tariffs` returns reviewed tariff configuration and normalised supplier intervals, or null when unconfirmed. UTC interval prices require explicit scope/status. The optional per-vehicle `evBilling` object supports reviewed daily `sessionCapKwh`; capped queries start at local midnight. See README for adapter schemas and installation limitations.
