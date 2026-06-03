# Changelog

## 0.2.3

- Fixes Home Assistant Supervisor source builds by using the proper `build_from` architecture map instead of passing a literal `{arch}` Docker build argument.
- Makes the Docker publish stage tolerate stale `{arch}` build arguments by falling back to Docker BuildKit `TARGETARCH`.

## 0.2.2

- Adds a companion-link diagnostics action for same-host LMS Edge Gateway add-on installs.
- Verifies both Helper-to-Edge Gateway and Edge Gateway-to-Helper health before publishing Tesla routes.

## 0.2.1

- Adds a Home Assistant add-on option for the Helper upstream URL used by Edge Gateway OAuth forwarding.
- Documents the cross-host Edge Gateway and Helper URL requirements for production Home Assistant testing.

## 0.2.0

- Adds Tesla Fleet API property discovery and a Home Assistant MQTT projection preview.
- Publishes typed Home Assistant MQTT Discovery entities through the Tesla projection mapper.
- Improves the standalone harness table layout for reviewing vehicle, energy, and MQTT mapping data before HA testing.

## 0.1.0

- Adds the initial LMS Tesla Fleet Helper companion add-on.
- Generates and stores an EC P-256 Tesla Fleet private key and publishes the public key through LMS Edge Gateway.
- Shows Tesla Developer Console values, Home Assistant redirect URI guidance, private key export, virtual key URL, and diagnostics.
