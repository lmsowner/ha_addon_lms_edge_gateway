# Linux Made Sane - Edge Gateway

Linux Made Sane - Edge Gateway makes secure self-hosted application publishing sane for Home Assistant users.

The add-on is the Home Assistant packaging layer for the Linux Made Sane - Edge Gateway control plane. It runs:

- ASP.NET Core / Blazor UI
- Linux Made Sane - Edge Gateway orchestration services
- Caddy
- cloudflared

## Project links

- Linux Made Sane: https://www.linuxmadesane.com
- GitHub repository: https://github.com/lmsowner/ha_addon_lms_edge_gateway
- Home Assistant add-on repository URL: https://github.com/lmsowner/ha_addon_lms_edge_gateway
- Issues and feedback: https://github.com/lmsowner/ha_addon_lms_edge_gateway/issues

## Current setup flow

The add-on provides the installable Home Assistant surface, ingress UI, runtime status, persistent storage, and supervised Caddy/cloudflared processes.

Setup Relay uses the saved Cloudflare token to create or reuse the named tunnel, create the proxied wildcard DNS record for `*.ha-app-relay.<domain>`, update tunnel ingress to Caddy, save the cloudflared connector token, and write the generated Caddy configuration.

Per-application publish, Cloudflare Access policy, LMS authentication policy, rollback, and relay removal flows are the next pieces.

## Home Assistant install progress

Home Assistant Supervisor reports add-on installation progress in broad phases. It can show `0%`, jump to `100%`, and still display `Installing` while Docker is still building, preparing, or starting the add-on container.

That is expected for this package while it is source-built by Home Assistant. The add-on config does not yet point to prebuilt registry images, so the HA host builds the Dockerfile locally. That includes restoring and publishing the .NET application, installing Caddy, downloading cloudflared, creating the image, and starting the supervised services. On smaller HA hardware this can look like the install has paused even though the build is still running.

When prebuilt multi-architecture images are published and the add-on `image` field points at them, Home Assistant can pull the ready image instead of building it locally. The progress UI may still be coarse, but the long local build phase should disappear.

## Storage

Persistent files live under `/data`:

- `/data/lms-edge-gateway/edge-gateway.json`
- `/data/caddy/Caddyfile`
- `/data/cloudflared`
- `/data/logs`

## Local ports

The Blazor UI listens on port `5000`.

Caddy has a default local health endpoint on port `18080`:

```text
http://addon-host:18080/health
```

## Cloudflare

Cloudflare setup requires:

- a Cloudflare account
- a Cloudflare-managed domain
- a scoped Cloudflare API token

The Setup page guides users through token validation, zone selection, tunnel creation, wildcard DNS routing, cloudflared connector setup, and local Caddy configuration.

## Security model

The add-on uses Home Assistant ingress for the management UI. Published applications will be protected by generated Cloudflare Access and Linux Made Sane - Edge Gateway policy once the application publish flow is wired.

Setup Relay creates only the scoped wildcard relay namespace. Individual application routes still require the publish flow.
