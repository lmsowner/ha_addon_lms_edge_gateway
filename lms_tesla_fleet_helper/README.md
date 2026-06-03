# LMS Tesla Fleet Helper

LMS Tesla Fleet Helper is a companion Home Assistant add-on for Tesla Fleet setup.

It owns Tesla-specific behaviour:

- EC P-256 key generation.
- Tesla Fleet public/private key management.
- Public key publishing through LMS Edge Gateway.
- Home Assistant `tesla_fleet.key` export.
- Tesla Developer Console guidance.
- Tesla virtual key install URL.
- Diagnostics for the public key publishing flow.

LMS Edge Gateway remains the public HTTPS, Cloudflare, Caddy, and `.well-known` publishing layer. Install this helper on the same Home Assistant server as LMS Edge Gateway.

## Companion add-on setup

The recommended setup is both add-ons on the same Home Assistant Supervisor host. Both add-ons use host networking, so the default URLs are intentionally loopback URLs:

- `edge_gateway_url`: `http://127.0.0.1:5000`
- `public_upstream_url`: `http://127.0.0.1:5055`

The Tesla origin domain still points at Edge Gateway publicly. Edge Gateway then forwards `/oauth/start`, `/redirect`, and `/oauth/callback` to `public_upstream_url`.

Use the **Check Edge Gateway link** diagnostics action to confirm both directions are healthy before publishing Tesla routes.

Cross-host installs are possible, but they should use stable LAN hostnames or static DHCP addresses instead of `127.0.0.1`.
