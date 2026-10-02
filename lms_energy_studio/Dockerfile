ARG BUILD_FROM=ghcr.io/home-assistant/amd64-base:latest
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
ARG BUILD_ARCH=amd64
ARG TARGETARCH=amd64
WORKDIR /src
COPY src ./src
RUN EFFECTIVE_ARCH="${BUILD_ARCH}" \
    && if [ "$EFFECTIVE_ARCH" = "{arch}" ]; then EFFECTIVE_ARCH="${TARGETARCH}"; fi \
    && case "$EFFECTIVE_ARCH" in amd64|x86_64) RID=linux-musl-x64 ;; aarch64|arm64) RID=linux-musl-arm64 ;; *) exit 1 ;; esac \
    && dotnet publish src/HA.LMS.EnergyStudio/HA.LMS.EnergyStudio.csproj -c Release -r "$RID" --self-contained true -o /out /p:UseAppHost=true
FROM ${BUILD_FROM}
ARG BUILD_VERSION=2026.10.02.17.34
ARG BUILD_ARCH=amd64
LABEL io.hass.version="${BUILD_VERSION}" io.hass.type="addon" io.hass.arch="${BUILD_ARCH}" org.opencontainers.image.title="LMS Energy Studio" org.opencontainers.image.source="https://github.com/lmsowner/ha_addon_lms_edge_gateway"
ENV EnergyStudio__AddonVersion=${BUILD_VERSION} ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS=http://0.0.0.0:5066 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 EnergyStudio__DataRoot=/data/lms-energy-studio
RUN apk add --no-cache ca-certificates curl libstdc++ icu-libs icu-data-full tzdata
COPY --from=build /out/ /app/
COPY rootfs/ /
RUN chmod 0755 /etc/services.d/lms-energy-studio/run
EXPOSE 5066
