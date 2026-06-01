# LMS Edge Gateway for Home Assistant

Secure application publishing and edge gateway for Home Assistant and self-hosted services.

This add-on provides the Home Assistant install and lifecycle wrapper for LMS Edge Gateway.

Open the add-on through Home Assistant ingress after installation.

During install, Home Assistant may jump from `0%` to `100%` while still showing `Installing`. That is normally Supervisor progress for one install phase, not the final app-ready state. This add-on is currently built locally from source by Home Assistant, so the .NET publish, Caddy install, cloudflared download, Docker image creation, and service startup can continue after the coarse progress bar reaches `100%`.

## Links

- Linux Made Sane: https://www.linuxmadesane.com
- GitHub repository: https://github.com/lmsowner/ha_addon_lms_edge_gateway
- Add-on repository URL for Home Assistant: https://github.com/lmsowner/ha_addon_lms_edge_gateway
- Issues and feedback: https://github.com/lmsowner/ha_addon_lms_edge_gateway/issues
