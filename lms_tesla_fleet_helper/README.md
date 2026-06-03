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

Both add-ons should run on the same Home Assistant Supervisor host. The helper auto-detects the LMS Edge Gateway add-on through the Supervisor API and uses the local add-on bridge internally.

The Tesla origin domain still points at Edge Gateway publicly. Edge Gateway then forwards `/oauth/start`, `/redirect`, and `/oauth/callback` to the helper over the internal same-host add-on bridge.

Use the **Check Edge Gateway link** diagnostics action to confirm both directions are healthy before publishing Tesla routes.
