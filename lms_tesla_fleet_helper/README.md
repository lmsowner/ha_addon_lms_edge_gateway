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
