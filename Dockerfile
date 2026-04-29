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

# Relay configuration
ENV RELAY_Mode=smtp
ENV RELAY_Smtp__Port=2525

# LocalXpose tunnel configuration (all optional — tunnel is disabled by default).
# Set LOCLX_ENABLED=true to activate the tunnel.
# Provide LOCLX_TOKEN with your LocalXpose access token.
# Optionally set LOCLX_RESERVED_ENDPOINT=<host:port> to use a reserved endpoint.
# Alternatively, mount a tunnels YAML file at /etc/resgrid/loclx-tunnels.yaml.
ENV LOCLX_ENABLED=false

EXPOSE 2525

ENTRYPOINT ["/docker-entrypoint.sh"]
