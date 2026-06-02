# Changelog

## 0.1.37

- Makes the Domain Services origin host mandatory and stops pre-filling Tesla as the default host.
- Aligns Domain Services editor controls with fixed label/control/helper rows and matching input/select heights.

## 0.1.36

- Moves public `.well-known` files served by Caddy to `/share/lms-edge-gateway/well-known/public` in the Home Assistant add-on.
- Keeps private add-on state and Tesla Fleet private keys under `/data/lms-edge-gateway`.

## 0.1.35

- Adds an origin host field to Domain Services so .well-known services can publish on subdomains such as `tesla.example.com` while still selecting from provisioned relay domains.
- Uses the computed origin hostname for Cloudflare DNS, tunnel ingress, Caddy host matching, Tesla Developer values, and public URL preview.

## 0.1.34

- Changes Domain Services to use a fixed dropdown of provisioned external relay domains instead of accepting arbitrary typed/app-derived domains.
- Disables .well-known publishing until Setup Relay has completed for at least one external domain.

## 0.1.33

- Adds Domain Services / .well-known Manager with reusable service storage, validation, public file publishing, verification, and Caddy route generation.
- Adds guided quick-start templates for Tesla Fleet, security.txt, WebFinger, Apple App Site Association, Android Asset Links, OpenID/OAuth discovery, and custom text/JSON.
- Adds Tesla Fleet EC key generation with the private key stored outside the public .well-known folder and only the public key published.
- Validates .well-known paths, content types, private-key-looking public bodies, and public verification responses.
- Validates generated Caddy configs before reload and restores the previous Caddyfile when reload fails.

## 0.1.32

- Moves the Setup Relay screenshot into the relay documentation section.
- Places documentation screenshots on separate full-width rows and removes temporary image labels from captions.

## 0.1.31

- Adds explicit pass-through warnings on new routes, edited routes, and enabled published routes.
- Removes the Blocked auth mode from route create/edit controls; disable a route instead when it should not be reachable.
- Updates Documentation so route policy guidance describes MFA/Passkey, Pass Through, source restrictions, and route disablement.

## 0.1.30

- Replaces the Cloudflare token permissions placeholder in Documentation with the existing permissions screenshot.
- Adds Published Apps screenshots for the add-route entry point and the saved service route state.

## 0.1.29

- Documents why Home Assistant can show add-on installation moving from `0%` to `100%` while the Supervisor still reports `Installing`.
- Clarifies that the current package is source-built by Home Assistant until prebuilt registry images are published and referenced by the add-on config.

## 0.1.28

- Adds an in-app Documentation tab covering Cloudflare setup, relay domains, app publishing, discovery, Caddy routing, authentication policies, passkey setup, messaging providers, scenarios, and operations.
- Adds screenshot placeholder areas in the documentation page for future captured setup images.
- Adds third-party software and service acknowledgements to the About page.

## 0.1.27

- Returns a plain Caddy 404 for unmatched published-app hostnames instead of exposing an Edge Gateway-specific message.
- Uses a generic 404 reason for unmatched forward-auth route checks.

## 0.1.26

- Tightens the About page layout by reducing artwork sizes, media column width, section spacing, and list row padding.

## 0.1.25

- Moves the Messaging provider status note out of the first control row so Status and Provider controls align cleanly.

## 0.1.24

- Fixes Messaging form control heights so Provider no longer stretches taller than the status control.
- Aligns Messaging controls to the same control row for cleaner bottom alignment.

## 0.1.23

- Removes the extra outer Published Apps wrapper.
- Adds expandable and collapsible domain groups on the Published Apps page.

## 0.1.22

- Moves the connected Cloudflare status and reset button to the bottom of the Setup page.

## 0.1.21

- Hides the Setup page "What Edge Gateway manages" callout after Cloudflare is configured.
- Moves the Cloudflare reset action into a separate bottom row of the token card.

## 0.1.20

- Aligns the Messaging Email enabled control with the Provider drop-down and other fields.

## 0.1.19

- Fixes Messaging so Email enabled is only shown for the selected provider when that provider is saved and verified.
- Makes a successful test email the action that verifies and enables the selected messaging provider.
- Prevents switching providers from carrying over the previous provider's verified/enabled state.

## 0.1.18

- Updates the About page to present LMS HA Edge Gateway as the Home Assistant add-on edition.
- Uses the Home Assistant-specific LMS Edge Gateway imagery in the About page.
- Adds clearer references to Linux Made Sane as the base LMS project.

## 0.1.17

- Keeps the full Home Assistant add-on name while shortening the sidebar label to Edge Gateway.
- Adds clearer first-run Cloudflare onboarding that explains the required account, DNS-managed domain, and scoped API token.
- Improves the Setup tab introduction so users know what Edge Gateway will manage before configuring a relay.

## 0.1.16

- Runs relay validation on add-on startup after tunnel ingress reconciliation.
- Restarts or directly starts cloudflared during startup when Cloudflare reports the saved tunnel is not healthy.
- Matches the Setup tab validation behavior without requiring the user to open Setup after an update.

## 0.1.15

- Reconciles managed Cloudflare Tunnel ingress routes for every saved app when the add-on starts.
- Uses the same tunnel ingress origin settings on startup that edit/save applies to existing app routes.
- Prevents version updates from leaving old Cloudflare tunnel entries stale until each app is manually re-saved.

## 0.1.14

- Reads Home Assistant's frontend `hass.selectedTheme` and `hass.themes.darkMode` state before falling back to CSS inference.
- Fixes the local theme toggle cycle so forced dark, forced light, and follow Home Assistant are all reachable.

## 0.1.13

- Defaults the UI theme to follow the current Home Assistant frame, including Home Assistant light, dark, and auto modes.
- Keeps the Sun/Moon toggle as a local override and adds an auto indicator when following Home Assistant.
- Loads the theme script before stylesheets so the selected theme is applied before first paint.

## 0.1.12

- Fixes WS-Discovery parsing so XML namespace/schema URLs are ignored and only device `XAddrs` endpoints become service candidates.

## 0.1.11

- Runs the Home Assistant add-on on the host network so LAN discovery sees the same network view as the HA host.
- Grants raw network access for ICMP reachability checks during LAN discovery.
- Binds the internal Caddy relay listener to loopback when using the default localhost origin.
- Adds LAN scan-plan progress messages showing CIDRs, ARP/neighbour counts, and target counts before probing.

## 0.1.10

- Uses one shared add-route dialog across Setup and Apps instead of maintaining duplicate implementations.
- Adds staged HTTP/S service discovery with ARP, SSDP, mDNS, WS-Discovery, Docker, and targeted LAN scan phases.
- Improves large subnet discovery by prioritising known neighbours and the Home Assistant host's local /24 before the wider CIDR.
- Fixes Supervisor network parsing so nested IPv4 addresses inherit parent prefix/netmask values.
- Aligns new route dialog labels, controls, and helper text for a cleaner horizontal layout.

## 0.1.9

- Adds immediate tab navigation feedback with a busy overlay while Blazor loads the next page.
- Adds shared button spinners for long-running actions such as saving, scanning, testing, repairing, setup, and deletes.
- Improves page loading states with a spinner and progress indicator on setup, apps, security, diagnostics, and Cloudflare data loads.

## 0.1.8

- Re-applies the saved theme after Blazor enhanced navigation so the toggle state persists between pages.
- Stores the selected theme in both local storage and a cookie fallback.
- Uses the parent Home Assistant frame theme as the default when no explicit add-on theme has been saved.

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
