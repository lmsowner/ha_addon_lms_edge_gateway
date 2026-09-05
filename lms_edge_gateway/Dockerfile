ARG BUILD_FROM=ghcr.io/home-assistant/amd64-base:latest
ARG BUILD_VERSION=2026.09.05.12.12
ARG BUILD_ARCH=amd64

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
ARG BUILD_ARCH=amd64
ARG DOTNET_CONFIGURATION=Release
WORKDIR /src
COPY src ./src
RUN \
    case "${BUILD_ARCH}" in \
      amd64) export DOTNET_RID=linux-musl-x64 ;; \
      aarch64) export DOTNET_RID=linux-musl-arm64 ;; \
      *) echo "Unsupported BUILD_ARCH=${BUILD_ARCH}" >&2; exit 1 ;; \
    esac \
    && dotnet publish src/HA.LMS.EdgeGateway/HA.LMS.EdgeGateway.csproj \
      --configuration "${DOTNET_CONFIGURATION}" \
      --runtime "${DOTNET_RID}" \
      --self-contained true \
      --output /out \
      /p:UseAppHost=true

FROM ${BUILD_FROM}
ARG BUILD_VERSION=2026.09.05.12.12
ARG BUILD_ARCH=amd64

LABEL \
  io.hass.version="${BUILD_VERSION}" \
  io.hass.type="addon" \
  io.hass.arch="${BUILD_ARCH}" \
  org.opencontainers.image.title="LMS Edge Gateway for Home Assistant" \
  org.opencontainers.image.version="${BUILD_VERSION}" \
  org.opencontainers.image.source="https://github.com/lmsowner/ha_addon_lms_edge_gateway"

ENV \
  ASPNETCORE_ENVIRONMENT=Production \
  ASPNETCORE_URLS=http://0.0.0.0:5000 \
  DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
  EdgeGateway__AddonVersion=${BUILD_VERSION} \
  EdgeGateway__DataRoot=/data/lms-edge-gateway \
  EdgeGateway__WellKnownPublicRoot=/share/lms-edge-gateway/well-known/public \
  EdgeGateway__CaddyConfigPath=/data/caddy/Caddyfile \
  EdgeGateway__CloudflaredConfigDirectory=/data/cloudflared \
  EdgeGateway__OptionsJsonPath=/data/options.json \
  EdgeGateway__CaddyLocalServiceUrl=http://localhost:18080 \
  EdgeGateway__LmsForwardAuthPort=5000

RUN \
  apk add --no-cache \
    bind-tools \
    ca-certificates \
    caddy \
    curl \
    cyrus-sasl \
    jq \
    libstdc++ \
    opendkim \
    postfix \
    postfix-pcre \
    procps \
    rsyslog \
  && case "${BUILD_ARCH}" in \
      amd64) export CLOUDFLARED_ARCH=amd64 ;; \
      aarch64) export CLOUDFLARED_ARCH=arm64 ;; \
      *) echo "Unsupported BUILD_ARCH=${BUILD_ARCH}" >&2; exit 1 ;; \
    esac \
  && curl -fsSL "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-linux-${CLOUDFLARED_ARCH}" -o /usr/local/bin/cloudflared \
  && chmod 0755 /usr/local/bin/cloudflared

COPY --from=build /out/ /app/
COPY rootfs/ /

RUN \
  chmod 0755 /etc/cont-init.d/00-lms-edge-gateway \
  && chmod 0755 /usr/local/bin/lms-mail-relay-apply \
  && chmod 0755 /etc/services.d/lms-edge-gateway/run \
  && chmod 0755 /etc/services.d/caddy/run \
  && chmod 0755 /etc/services.d/cloudflared/run \
  && chmod 0755 /etc/services.d/lms-mail-relay/run

EXPOSE 5000 18080
