# Changelog

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
