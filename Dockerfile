FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .
RUN dotnet restore "Resgrid.Audio.Relay.Console/Resgrid.Audio.Relay.Console.csproj" -p:TargetFramework=net10.0
RUN dotnet publish "Resgrid.Audio.Relay.Console/Resgrid.Audio.Relay.Console.csproj" -c Release -f net10.0 -o /app/publish /p:UseAppHost=false

# Download the LocalXpose CLI binary.
# LocalXpose distributes only a rolling "latest" build from S3 — there are no
# versioned release URLs. SHA256 values below are sourced from the AUR PKGBUILD
# (https://aur.archlinux.org/packages/localxpose-cli, last updated 2025-02-04)
# and must be updated whenever loclx publishes a new binary.
# Supported Docker architectures: linux/amd64, linux/arm64, linux/386, linux/arm
FROM debian:bookworm-slim AS loclx-download
ARG TARGETARCH=amd64
ARG LOCLX_SHA256_AMD64=03c6d1d35dfd0acb673473314c1384156ed2bfcb96e581b3e0bb398fef45fb88
ARG LOCLX_SHA256_ARM64=a423e0ce90fcab7044b4f0244fb8483fe12acf30361fff0e37d5abc2dae2da91
ARG LOCLX_SHA256_386=2534e0056ba5c1e4d55322b2b975a4945104604be15bef9df01c933ad4352804
ARG LOCLX_SHA256_ARM=83e5484169ea28f05fe221056374bbdaac129850b607bdc243326a06c22575e4
RUN apt-get update && apt-get install -y --no-install-recommends wget zstd ca-certificates \
    && case "${TARGETARCH}" in \
         amd64) sha="${LOCLX_SHA256_AMD64}" ;; \
         arm64) sha="${LOCLX_SHA256_ARM64}" ;; \
         386)   sha="${LOCLX_SHA256_386}"   ;; \
         arm)   sha="${LOCLX_SHA256_ARM}"   ;; \
         *)     echo "Unsupported arch: ${TARGETARCH}" >&2 ; exit 1 ;; \
       esac \
    && wget -q -O /tmp/loclx.pkg.tar.zst \
         "https://loclx-client.s3.amazonaws.com/loclx-linux-${TARGETARCH}.pkg.tar.zst" \
    && echo "${sha}  /tmp/loclx.pkg.tar.zst" | sha256sum -c - \
    && mkdir -p /tmp/loclx-extract \
    && tar --zstd -xf /tmp/loclx.pkg.tar.zst -C /tmp/loclx-extract \
    && find /tmp/loclx-extract -type f -name 'loclx' \
         -exec install -m 755 {} /usr/local/bin/loclx \; \
    && [ -x /usr/local/bin/loclx ] \
    && rm -rf /tmp/loclx.pkg.tar.zst /tmp/loclx-extract /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish .
COPY --from=loclx-download /usr/local/bin/loclx /usr/local/bin/loclx
COPY docker-entrypoint.sh /docker-entrypoint.sh
RUN chmod +x /docker-entrypoint.sh

# ─── Relay configuration ───────────────────────────────────────────────
# Every setting below can be overridden at runtime via -e or docker-compose.
# Required values are marked [REQUIRED] — the relay will not start without them.

# Operational mode
ENV RESGRID__RELAY__Mode=smtp

# Resgrid API (v4)
# [REQUIRED] e.g. https://api.resgrid.com
ENV RESGRID__RELAY__Resgrid__BaseUrl=""
ENV RESGRID__RELAY__Resgrid__ApiVersion=4
# [REQUIRED]
ENV RESGRID__RELAY__Resgrid__ClientId=""
# [REQUIRED]
ENV RESGRID__RELAY__Resgrid__ClientSecret=""
# Required when GrantType=RefreshToken
ENV RESGRID__RELAY__Resgrid__RefreshToken=""
ENV RESGRID__RELAY__Resgrid__Scope=openid profile email offline_access mobile
ENV RESGRID__RELAY__Resgrid__TokenCachePath=./data/resgrid-token.json
# RefreshToken | ClientCredentials | SystemApiKey
ENV RESGRID__RELAY__Resgrid__GrantType=RefreshToken
# Required when GrantType=SystemApiKey
ENV RESGRID__RELAY__Resgrid__SystemApiKey=""
# Optional, used as fallback in hosted mode
ENV RESGRID__RELAY__Resgrid__DepartmentId=""

# Telemetry (optional — telemetry is disabled when these are left empty)
ENV RESGRID__RELAY__Telemetry__Environment=
ENV RESGRID__RELAY__Telemetry__Sentry__Dsn=
ENV RESGRID__RELAY__Telemetry__Sentry__Release=
ENV RESGRID__RELAY__Telemetry__Sentry__SendDefaultPii=true
ENV RESGRID__RELAY__Telemetry__Countly__Url=
ENV RESGRID__RELAY__Telemetry__Countly__AppKey=
ENV RESGRID__RELAY__Telemetry__Countly__DeviceId=
ENV RESGRID__RELAY__Telemetry__Countly__RequestTimeoutSeconds=5

# SMTP server
ENV RESGRID__RELAY__Smtp__ServerName=resgrid-relay
ENV RESGRID__RELAY__Smtp__Port=2525
ENV RESGRID__RELAY__Smtp__DataDirectory=./data
ENV RESGRID__RELAY__Smtp__DuplicateWindowHours=72
ENV RESGRID__RELAY__Smtp__DefaultCallPriority=1
ENV RESGRID__RELAY__Smtp__MaxAttachmentBytes=10485760
ENV RESGRID__RELAY__Smtp__MaxMessageBytes=26214400
ENV RESGRID__RELAY__Smtp__SaveRawMessages=true
ENV RESGRID__RELAY__Smtp__DepartmentDispatchPrefix=G

# Dispatch domain configuration — at least one domain list is required.
# Use the indexed form for arrays in env vars:
#   RESGRID__RELAY__Smtp__DepartmentAddressDomains__0=dispatch.resgrid.com
#   RESGRID__RELAY__Smtp__DepartmentAddressDomains__1=pager.example.com
#   RESGRID__RELAY__Smtp__GroupAddressDomains__0=groups.resgrid.com
#   RESGRID__RELAY__Smtp__GroupMessageAddressDomains__0=gm.resgrid.com
#   RESGRID__RELAY__Smtp__ListAddressDomains__0=lists.resgrid.com

# Hosted (multi-department) mode
# true when running for Resgrid Hosted
ENV RESGRID__RELAY__Smtp__HostedMode=false
ENV RESGRID__RELAY__Smtp__DepartmentDomainSeparator=.
# Optional department override
ENV RESGRID__RELAY__Smtp__DefaultDepartmentId=""
# Resolve code names to numeric IDs via lookup API
ENV RESGRID__RELAY__Smtp__ResolveDispatchCodes=true

# Redis cache for dispatch lookups (optional — disabled by default)
# When enabled, group/unit/role lookup results are cached in Redis to
# reduce API traffic. This is especially beneficial in hosted mode where
# the relay handles emails for many departments.
ENV RESGRID__RELAY__Smtp__RedisCache__Enabled=false
# ENV RESGRID__RELAY__Smtp__RedisCache__ConnectionString=redis:6379,abortConnect=false
# ENV RESGRID__RELAY__Smtp__RedisCache__TtlMinutes=60

# ─── LocalXpose tunnel ─────────────────────────────────────────────────
# All optional — tunnel is disabled by default.
ENV LOCLX_ENABLED=false
# LocalXpose access token
# ENV LOCLX_TOKEN=""
# e.g. smtp.loclx.io:25
# ENV LOCLX_RESERVED_ENDPOINT=""

EXPOSE 2525

ENTRYPOINT ["/docker-entrypoint.sh"]
