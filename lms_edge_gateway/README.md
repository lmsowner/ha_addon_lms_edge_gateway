# LMS Edge Gateway for Home Assistant

Secure application publishing and edge gateway for Home Assistant and self-hosted services.

This add-on provides the Home Assistant install and lifecycle wrapper for LMS Edge Gateway.

Open the add-on through Home Assistant ingress after installation.

During install, Home Assistant should pull the prebuilt GHCR image for your architecture. If you still see a long `0%` wait, confirm GitHub Actions finished publishing `ghcr.io/lmsowner/lms_edge_gateway-{arch}` for this version and that the package is public.

## Authentication options

- `MFA/Passkey`: best for browser-first apps. LMS signs the user in with MFA or a passkey before Caddy proxies to the internal service.
- `Email approve IP`: deny-first access for clients that cannot complete browser MFA. Edge Gateway sends a throttled approval email, then temporarily allows only the approved app and exact source IP. By default the grant is also bound to the requesting client User-Agent; optionally allow every client on that IP for trusted home/LAN networks. Grants expire on idle cut-off or maximum lifetime.
- `Verified LAN trust` (optional on MFA/Email approve IP routes): when Edge Gateway sees a real LAN source IP inside your trusted CIDRs and reverse/forward DNS matches your internal domain (for example `*.kiernanfamily.co.uk`), authentication can be skipped for that request. Cloudflare internet clients are never trusted this way.
- `Pass Through`: sends internet traffic straight to the destination service. Use only when the upstream service is public by design or has its own suitable authentication.

## Links

- Linux Made Sane: https://www.linuxmadesane.com
- GitHub repository: https://github.com/lmsowner/ha_addon_lms_edge_gateway
- Add-on repository URL for Home Assistant: https://github.com/lmsowner/ha_addon_lms_edge_gateway
- Issues and feedback: https://github.com/lmsowner/ha_addon_lms_edge_gateway/issues
