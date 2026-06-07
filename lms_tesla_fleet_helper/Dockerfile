ARG BUILD_FROM=ghcr.io/home-assistant/amd64-base:latest
ARG BUILD_VERSION=0.2.40
ARG BUILD_ARCH=amd64

FROM golang:1.23-alpine AS vehicle-command
ARG VEHICLE_COMMAND_VERSION=v0.4.1
WORKDIR /src/vehicle-command
RUN \
    apk add --no-cache git \
    && git clone --depth 1 --branch "${VEHICLE_COMMAND_VERSION}" https://github.com/teslamotors/vehicle-command.git . \
    && go build -o /go/bin/tesla-http-proxy ./cmd/tesla-http-proxy

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
ARG BUILD_ARCH=amd64
ARG TARGETARCH=amd64
ARG DOTNET_CONFIGURATION=Release
WORKDIR /src
COPY src ./src
RUN \
    EFFECTIVE_BUILD_ARCH="${BUILD_ARCH}" \
    && if [ "${EFFECTIVE_BUILD_ARCH}" = "{arch}" ] || [ -z "${EFFECTIVE_BUILD_ARCH}" ]; then EFFECTIVE_BUILD_ARCH="${TARGETARCH}"; fi \
    && case "${EFFECTIVE_BUILD_ARCH}" in \
      amd64|x86_64) export DOTNET_RID=linux-musl-x64 ;; \
      aarch64|arm64) export DOTNET_RID=linux-musl-arm64 ;; \
      *) echo "Unsupported BUILD_ARCH=${EFFECTIVE_BUILD_ARCH}" >&2; exit 1 ;; \
    esac \
    && dotnet publish src/HA.LMS.TeslaFleetHelper/HA.LMS.TeslaFleetHelper.csproj \
      --configuration "${DOTNET_CONFIGURATION}" \
      --runtime "${DOTNET_RID}" \
      --self-contained true \
      --output /out \
      /p:UseAppHost=true

FROM ${BUILD_FROM}
ARG BUILD_VERSION=0.2.40
ARG BUILD_ARCH=amd64

LABEL \
  io.hass.version="${BUILD_VERSION}" \
  io.hass.type="addon" \
  io.hass.arch="${BUILD_ARCH}" \
  org.opencontainers.image.title="LMS Tesla Fleet Helper" \
  org.opencontainers.image.version="${BUILD_VERSION}" \
  org.opencontainers.image.source="https://github.com/lmsowner/ha_addon_lms_edge_gateway"

ENV \
  ASPNETCORE_ENVIRONMENT=Production \
  ASPNETCORE_URLS=http://0.0.0.0:5055 \
  DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
  TeslaFleetHelper__AddonVersion=${BUILD_VERSION} \
  TeslaFleetHelper__DataRoot=/data/lms-tesla-fleet-helper \
  TeslaFleetHelper__OptionsJsonPath=/data/options.json

RUN \
  apk add --no-cache \
    ca-certificates \
    curl \
    libstdc++

COPY --from=build /out/ /app/
COPY --from=vehicle-command /go/bin/tesla-http-proxy /usr/local/bin/tesla-http-proxy
COPY rootfs/ /

RUN \
  chmod 0755 /etc/cont-init.d/00-lms-tesla-fleet-helper \
  && chmod 0755 /etc/services.d/lms-tesla-fleet-helper/run

EXPOSE 5055
