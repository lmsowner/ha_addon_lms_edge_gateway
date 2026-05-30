# Changelog

## 0.1.2

- Adopts Home Assistant theme variables inside ingress.
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
