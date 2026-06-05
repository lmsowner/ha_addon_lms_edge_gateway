# Changelog

## 0.2.27

- Adds Light, Dark, and Auto theme support to the Tesla Fleet Helper UI.
- Follows the current Home Assistant theme when Auto is selected and persists manual Light/Dark choices across navigation.
- Applies the same theme handling to OAuth/status pages rendered by the helper.

## 0.2.26

- Adds native Home Assistant controls for Tesla cabin overheat protection, seat heat/cool/auto climate, steering wheel heat, valet mode, and charge-port latch.
- Adds vehicle one-shot buttons for Homelink, remote start, emissions test, charge standard/max range, and optional media controls.
- Routes seat and steering controls through proper multi-step Tesla Fleet command sequences where preconditioning or auto-mode clearing is required.

## 0.2.25

- Publishes Tesla HVAC as a native Home Assistant climate entity with on/off, target temperature, presets, and Bioweapon fan mode.
- Replaces crude vehicle buttons with native covers for charge port, windows, frunk, trunk, and sunroof.
- Adds door/window open sensors and clears the old retained climate/cover button discovery topics during MQTT publish.

## 0.2.24

- Keeps normal Home Assistant MQTT refreshes conservative: sleeping/offline vehicles are not woken for background `vehicle_data` polling.
- Adds per-vehicle Home Assistant Polling switches so `vehicle_data` reads can be disabled for a VIN without calling Tesla.
- Adds a Force Data Update button and wakes vehicles before explicit vehicle write commands so updates happen on deliberate user action.

## 0.2.23

- Adds a per-vehicle Home Assistant timestamp sensor showing when live Tesla `vehicle_data` was last refreshed.
- Includes `vehicle_data_refreshed` in the retained MQTT state payload and helper publish diagnostics.
- Preserves the timestamp while vehicles are asleep so cached values clearly show when they were last updated.

## 0.2.22

- Preserves last known vehicle MQTT state values so sleeping/offline vehicles do not overwrite Home Assistant sensors with unknown.
- Stops the Home Assistant discovery reset action from clearing retained state topics; it now only clears retained discovery configs before republishing.
- Persists the merged vehicle MQTT state payload cache across helper restarts.

## 0.2.21

- Resolves vehicle state fields by normalized raw Tesla API path so diagnostics values flow into the typed Home Assistant MQTT payload even when Tesla changes casing or nesting.
- Adds publish diagnostics showing the exact vehicle `battery_level`, `charging_state`, `charge_limit`, and `charging_amps` values sent to Home Assistant MQTT.

## 0.2.20

- Maps vehicle charge limit and charging amps from both snake_case vehicle data and CamelCase Fleet/telemetry field names.
- Publishes charge limit and charging amps as primary writable Home Assistant MQTT number entities.
- Retires the previous read-only vehicle charge sensors and old `_number` discovery topics during publish.

## 0.2.19

- Adds a Home Assistant MQTT Discovery reset and republish action for stale or renamed entities after helper updates.
- Tracks the retained discovery topics published by the helper so old entity configs can be cleared safely across restarts.
- Clears current retained Tesla Fleet state topics during discovery reset before republishing fresh state.

## 0.2.18

- Removes row caps from the Tesla API property harness and Home Assistant projection preview.
- Adds scope/resource/value/search filters for discovered Tesla API properties so vehicle fields can be debugged without hidden rows.
- Adds component/command/search filters for MQTT projection diagnostics.
- Shows sanitized property values in expandable rows instead of dropping long values from the diagnostics view.
- Shows every vehicle returned by the Tesla vehicles diagnostics call instead of truncating after eight entries.

## 0.2.17

- Separates Tesla OAuth reconnect from virtual-key installation.
- Removes the forced Tesla keypair step from the OAuth authorize URL so adding scopes does not try to reinstall the vehicle virtual key.
- Keeps virtual-key install as an explicit setup action for first install, new vehicles, or Fleet key rotation.

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
