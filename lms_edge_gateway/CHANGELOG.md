# Changelog

## 0.1.16

- Runs relay validation on add-on startup after tunnel ingress reconciliation.
- Restarts or directly starts cloudflared during startup when Cloudflare reports the saved tunnel is not healthy.
- Matches the Setup tab validation behavior without requiring the user to open Setup after an update.

## 0.1.15

- Reconciles managed Cloudflare Tunnel ingress routes for every saved app when the add-on starts.
- Uses the same tunnel ingress origin settings on startup that edit/save applies to existing app routes.
- Prevents version updates from leaving old Cloudflare tunnel entries stale until each app is manually re-saved.

## 0.1.14

- Reads Home Assistant's frontend `hass.selectedTheme` and `hass.themes.darkMode` state before falling back to CSS inference.
- Fixes the local theme toggle cycle so forced dark, forced light, and follow Home Assistant are all reachable.

## 0.1.13

- Defaults the UI theme to follow the current Home Assistant frame, including Home Assistant light, dark, and auto modes.
- Keeps the Sun/Moon toggle as a local override and adds an auto indicator when following Home Assistant.
- Loads the theme script before stylesheets so the selected theme is applied before first paint.

## 0.1.12

- Fixes WS-Discovery parsing so XML namespace/schema URLs are ignored and only device `XAddrs` endpoints become service candidates.

## 0.1.11

- Runs the Home Assistant add-on on the host network so LAN discovery sees the same network view as the HA host.
- Grants raw network access for ICMP reachability checks during LAN discovery.
- Binds the internal Caddy relay listener to loopback when using the default localhost origin.
- Adds LAN scan-plan progress messages showing CIDRs, ARP/neighbour counts, and target counts before probing.

## 0.1.10

- Uses one shared add-route dialog across Setup and Apps instead of maintaining duplicate implementations.
- Adds staged HTTP/S service discovery with ARP, SSDP, mDNS, WS-Discovery, Docker, and targeted LAN scan phases.
- Improves large subnet discovery by prioritising known neighbours and the Home Assistant host's local /24 before the wider CIDR.
- Fixes Supervisor network parsing so nested IPv4 addresses inherit parent prefix/netmask values.
- Aligns new route dialog labels, controls, and helper text for a cleaner horizontal layout.

## 0.1.9

- Adds immediate tab navigation feedback with a busy overlay while Blazor loads the next page.
- Adds shared button spinners for long-running actions such as saving, scanning, testing, repairing, setup, and deletes.
- Improves page loading states with a spinner and progress indicator on setup, apps, security, diagnostics, and Cloudflare data loads.

## 0.1.8

- Re-applies the saved theme after Blazor enhanced navigation so the toggle state persists between pages.
- Stores the selected theme in both local storage and a cookie fallback.
- Uses the parent Home Assistant frame theme as the default when no explicit add-on theme has been saved.

## 0.1.7

- Adds a persisted Sun/Moon theme toggle to the app shell and login layout.
- Adds a high-contrast dark palette for the app shell, cards, tables, forms, modals, status pills, and login flow.
- Applies the saved theme before stylesheets load to avoid a light/dark flash.

## 0.1.6

- Builds full LAN scan ranges when Supervisor reports Home Assistant host IPv4 details as address plus prefix or netmask.
- Falls back bare private Supervisor host addresses to `/24` instead of scanning only the add-on container network.
- Keeps known LAN neighbours in the scan target set even when CIDR discovery falls back.

## 0.1.5

- Uses Supervisor Core info to detect whether Home Assistant should be reached as HTTP or HTTPS on its internal 8123 endpoint.
- Adds separate internal HTTP and HTTPS repair actions for Home Assistant routes.
- Adds a clearer Caddy 502 hint when Home Assistant likely needs HTTPS on port 8123.

## 0.1.4

- Prevents Home Assistant routes that target another public HTTPS host from sending the new public Host header upstream.
- Adds an edit-screen repair action to switch Home Assistant routes back to the internal `homeassistant:8123` target.

## 0.1.3

- Removes Home Assistant theme inheritance and returns the add-on UI to its fixed light styling.

## 0.1.2

- Uses Home Assistant Supervisor network information to scan the host LAN subnet, not only the add-on container network.
- Collapses duplicate HTTP/S lookup candidates for the same discovered service.

## 0.1.1

- Updates the Home Assistant add-on display name to LMS Edge Gateway for Home Assistant.
- Prepares the add-on package for full end-to-end Home Assistant testing.

## 0.1.0

- Initial Linux Made Sane - Edge Gateway Home Assistant Add-on product scaffold.
- Adds Blazor control plane with Home Assistant ingress support.
- Adds Caddy and cloudflared process supervision.
- Adds persistent `/data` storage for gateway state and Caddy configuration.
- Adds LMS-style dashboard, applications, Cloudflare, security, and diagnostics views.
