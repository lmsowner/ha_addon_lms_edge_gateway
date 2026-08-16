# LMS Edge Gateway for Home Assistant

Secure application publishing and edge gateway for Home Assistant and self-hosted services.

This add-on provides the Home Assistant install and lifecycle wrapper for LMS Edge Gateway.

Open the add-on through Home Assistant ingress after installation.

During install, Home Assistant may jump from `0%` to `100%` while still showing `Installing`. That is normally Supervisor progress for one install phase, not the final app-ready state. This add-on is currently built locally from source by Home Assistant, so the .NET publish, Caddy install, cloudflared download, Docker image creation, and service startup can continue after the coarse progress bar reaches `100%`.

## Authentication options

- `MFA/Passkey`: best for browser-first apps. LMS signs the user in with MFA or a passkey before Caddy proxies to the internal service.
- `Email approve IP`: deny-first access for clients that cannot complete browser MFA. Edge Gateway sends a throttled approval email, then temporarily allows only the approved app and exact source IP. By default the grant is also bound to the requesting client User-Agent; optionally allow every client on that IP for trusted home/LAN networks. Grants expire on idle cut-off or maximum lifetime.
- `Pass Through`: sends internet traffic straight to the destination service. Use only when the upstream service is public by design or has its own suitable authentication.

## Links

- Linux Made Sane: https://www.linuxmadesane.com
- GitHub repository: https://github.com/lmsowner/ha_addon_lms_edge_gateway
- Add-on repository URL for Home Assistant: https://github.com/lmsowner/ha_addon_lms_edge_gateway
- Issues and feedback: https://github.com/lmsowner/ha_addon_lms_edge_gateway/issues
