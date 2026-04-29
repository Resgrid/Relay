FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .
RUN dotnet restore "Resgrid.Audio.Relay.Console/Resgrid.Audio.Relay.Console.csproj" -p:TargetFramework=net10.0
RUN dotnet publish "Resgrid.Audio.Relay.Console/Resgrid.Audio.Relay.Console.csproj" -c Release -f net10.0 -o /app/publish /p:UseAppHost=false

# Download the LocalXpose CLI binary for Linux AMD64.
# See docker-entrypoint.sh for tunnel configuration options.
FROM debian:bookworm-slim AS loclx-download
RUN apt-get update && apt-get install -y --no-install-recommends wget unzip ca-certificates \
    && wget -q -O /tmp/loclx.zip \
         "https://github.com/localxpose/loclx/releases/latest/download/loclx-linux-amd64.zip" \
    && unzip /tmp/loclx.zip -d /tmp/loclx-extract \
    && find /tmp/loclx-extract -type f -name 'loclx' \
         -exec install -m 755 {} /usr/local/bin/loclx \; \
    && rm -rf /tmp/loclx.zip /tmp/loclx-extract /var/lib/apt/lists/*

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
