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

LMS Edge Gateway remains the public HTTPS, Cloudflare, Caddy, and `.well-known` publishing layer.

## Cross-host setup

If LMS Edge Gateway and LMS Tesla Fleet Helper run on different machines, do not leave both URLs at `127.0.0.1`.

- `edge_gateway_url` is the URL this helper uses to call Edge Gateway. Use the Edge Gateway box LAN URL, for example `http://192.168.15.10:5000`.
- `public_upstream_url` is the URL Edge Gateway uses to forward Tesla OAuth traffic back to this helper. Use the Home Assistant box LAN URL, for example `http://192.168.15.20:5055`.

The Tesla origin domain still points at Edge Gateway publicly. Edge Gateway then forwards `/oauth/start`, `/redirect`, and `/oauth/callback` to `public_upstream_url`.
