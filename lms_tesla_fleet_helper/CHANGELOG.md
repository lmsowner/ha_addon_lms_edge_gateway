# Changelog

## 0.2.16

- Adds Tesla's official vehicle-command HTTP proxy to the add-on image and supervises it from the Helper using the generated Fleet private key.
- Publishes writable Home Assistant MQTT vehicle controls for charge limit, charging amps, charger, climate, sentry mode, door lock, wake, lights, horn, charge port, frunk, and trunk.
- Routes vehicle writes through the signed command proxy where Tesla requires the Vehicle Command Protocol, while keeping wake-up on the documented Fleet API vehicle endpoint.
- Adds documented Energy write controls for Storm watch and Off-grid vehicle charging reserve.

## 0.2.15

- Clears retired MQTT discovery configs for the previous Energy control labels/components.
- Accepts decimal number payloads from Home Assistant for Backup reserve writes.
- Optimistically patches retained Energy state after successful commands so HA controls update immediately.
- Fetches realtime vehicle data for online cars when MQTT publishing is enabled and adds top-level Tesla field fallbacks.

## 0.2.14

- Renames writable Powerwall controls to match Tesla Custom Integration labels.
- Publishes friendly select values for Energy exports, Grid charging, and Operation mode.
- Maps friendly Home Assistant command values back to Tesla Fleet API command payloads.

## 0.2.13

- Adds writable Home Assistant MQTT controls for Tesla Energy backup reserve, operation mode, grid charging, and export rule.
- Adds a persistent MQTT command listener that receives Home Assistant writes, validates payloads, sends Tesla Energy commands, and republishes state.
- Adds the required Tesla `energy_cmds` OAuth scope and warns when OAuth needs reconnecting for writable Energy controls.

## 0.2.12

- Adds the required Tesla `energy_device_data` OAuth scope for Energy Product Information.
- Stops inventing a single Powerwall when `site_info` is unavailable; Powerwall count now comes from real Tesla site info or returned arrays.
- Reads `energy_left` from live status and exposes an Energy Data Status entity so missing energy authorization is visible in Home Assistant.

## 0.2.11

- Fixes Tesla energy battery percentage parsing when Fleet API returns decimal `percentage_charged` values.
- Adds richer Gateway/Powerwall energy entities for battery remaining Wh, charging status, grid connection, generator power, storm mode, and capability diagnostics.
- Flattens Tesla energy arrays into indexed raw fields so per-Powerwall inventory details can be projected when the API returns them.

## 0.2.10

- Reworks the Helper page into Edge Gateway-style tabs for setup, diagnostics, and the entity/property harness.
- Keeps tab selection locally persisted while navigating helper actions.
- Makes settings saves tolerate partial forms without losing existing Tesla or MQTT settings.

## 0.2.9

- Fetches Tesla energy `site_info` alongside live status so Gateway/Powerwall asset details can be projected into Home Assistant.
- Publishes the Tesla energy site as a Gateway parent device while preserving the existing MQTT device identifier.
- Publishes detected Powerwall units as child devices connected through the Gateway using Home Assistant MQTT `via_device`.

## 0.2.8

- Names battery-backed Tesla energy sites as Tesla Powerwall devices in Home Assistant MQTT Discovery so they are easier to find.
- Adds visible energy display-name metadata and disabled diagnostic site ID entities.
- Adds per-device MQTT Discovery publish counts to Helper diagnostics.

## 0.2.7

- Expands Home Assistant MQTT Discovery for Tesla vehicles with richer battery, charging, climate, drive, security, firmware, location, and Fleet key entities.
- Expands Tesla energy MQTT Discovery with resource, grid, battery, solar, load, and backup reserve entities.
- Publishes sanitized raw Tesla vehicle and energy scalar properties as disabled-by-default diagnostic entities so advanced fields are available without cluttering the default device view.

## 0.2.6

- Publishes an LMS Tesla Fleet Helper diagnostic MQTT device so Home Assistant discovery can be verified even when no Tesla resources are returned.
- Adds MQTT publish diagnostics showing discovery prefix, base topic, sample discovery topics, and retained state topics.

## 0.2.5

- Fixes Home Assistant ingress navigation by using relative redirects after helper actions.
- Opens Tesla OAuth in a new browser tab so Tesla auth is not framed inside Home Assistant ingress.
- Removes absolute back links from OAuth and error pages that could navigate to the wrong root.

## 0.2.4

- Makes LMS Edge Gateway a same-host companion add-on that is auto-detected through the Home Assistant Supervisor API and local health checks.
- Removes normal setup/options fields for Edge Gateway URL and Helper upstream URL.
- Forces the internal same-host add-on bridge so Tesla setup does not require users to enter IP addresses or ports.

## 0.2.3

- Fixes Home Assistant Supervisor source builds by using the proper `build_from` architecture map instead of passing a literal `{arch}` Docker build argument.
- Makes the Docker publish stage tolerate stale `{arch}` build arguments by falling back to Docker BuildKit `TARGETARCH`.

## 0.2.2

- Adds a companion-link diagnostics action for same-host LMS Edge Gateway add-on installs.
- Verifies both Helper-to-Edge Gateway and Edge Gateway-to-Helper health before publishing Tesla routes.

## 0.2.1

- Adds a Home Assistant add-on option for the Helper upstream URL used by Edge Gateway OAuth forwarding.
- Documents the cross-host Edge Gateway and Helper URL requirements for production Home Assistant testing.

## 0.2.0

- Adds Tesla Fleet API property discovery and a Home Assistant MQTT projection preview.
- Publishes typed Home Assistant MQTT Discovery entities through the Tesla projection mapper.
- Improves the standalone harness table layout for reviewing vehicle, energy, and MQTT mapping data before HA testing.

## 0.1.0

- Adds the initial LMS Tesla Fleet Helper companion add-on.
- Generates and stores an EC P-256 Tesla Fleet private key and publishes the public key through LMS Edge Gateway.
- Shows Tesla Developer Console values, Home Assistant redirect URI guidance, private key export, virtual key URL, and diagnostics.
