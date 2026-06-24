# syntax=docker/dockerfile:1.7
# Resgrid Relay container — Docker Hardened Images (DHI), mirroring the pattern and
# fixes used by Resgrid Core's Workers.Console Dockerfile:
#   * DHI base + SDK images, pinned by tag@sha256 (see resolve-dhi-digests.sh).
#   * tzdata installed with --reinstall (DHI ships the dpkg marker but no zone files).
#   * shell-less final image: docker-compose-wait execs the app (no /bin/sh).
#   * runs as the image's non-root user.
#
# Notes specific to the relay:
#   * The console relay has NO ASP.NET dependency, so it uses the runtime-only
#     dhi.io/dotnet image (Core's Workers used aspnetcore only because they pull in
#     the ASP.NET shared framework).
#   * The LiveKit FFI native library is baked in so the cross-platform 'record' and
#     'dispatch' (TTS tone-out) modes work in a container. The 'radio' bridge mode
#     needs physical audio devices and is Windows-desktop only — not containerized.
#   * The old docker-entrypoint.sh is gone; its env validation and LocalXpose tunnel
#     orchestration now live in the app (a shell-less image has no bash).
#
# DHI requires a Docker Hardened Images subscription/login. Resolve and pin the
# digests with ./resolve-dhi-digests.sh, which rewrites the two image ARGs below.
ARG BUILD_VERSION=2.0.0
ARG DOTNET_SDK_IMAGE=dhi.io/dotnet:10.0-sdk-debian13@sha256:27572eda11ffbda0ff63bcaf301b3314f8b993e32957d60b1396bde5bd24d4a6
ARG DOTNET_RUNTIME_IMAGE=dhi.io/dotnet:10.0-debian13@sha256:baed1f48538246b0c1152bd6b8ca4f04971213286fa229abf6079f01170ccf83

# LiveKit FFI (Rust client core) — must match the Livekit.Rtc.Dotnet package version.
# Validated against Livekit.Rtc.Dotnet 0.1.3.
ARG LIVEKIT_FFI_VERSION=livekit-ffi/v0.12.65

# ─── Build ──────────────────────────────────────────────────────────────────
FROM ${DOTNET_SDK_IMAGE} AS build
ARG BUILD_VERSION
WORKDIR /src
COPY . .
RUN dotnet restore "Resgrid.Audio.Relay.Console/Resgrid.Audio.Relay.Console.csproj" -p:TargetFramework=net10.0
RUN dotnet publish "Resgrid.Audio.Relay.Console/Resgrid.Audio.Relay.Console.csproj" \
        -c Release -f net10.0 -o /app/publish /p:UseAppHost=false -p:Version=${BUILD_VERSION}

# ─── Publish extras (shell available here, not in the hardened final image) ──
FROM build AS publish
ARG BUILD_VERSION
# docker-compose-wait: waits for WAIT_HOSTS (if any) then execs WAIT_COMMAND. The
# hardened final image has no shell, so this is how we launch the app.
ADD --checksum=sha256:2241be671073520e028b2f12df1e9ef0419014cffb5670b7a80b2080804be17d \
    https://github.com/ufoscout/docker-compose-wait/releases/download/2.12.1/wait /app/publish/wait
RUN chmod +x /app/publish/wait
# The hardened DHI image marks tzdata installed (dpkg) but ships none of the zone
# files, so a plain install is a no-op. --reinstall forces re-extraction; the test
# asserts the zone files are really present so the build fails loudly otherwise.
RUN DEBIAN_FRONTEND=noninteractive apt-get update \
    && apt-get install -y --reinstall --no-install-recommends tzdata \
    && test -f /usr/share/zoneinfo/America/New_York \
    && test -f /usr/share/zoneinfo/Asia/Kolkata \
    && rm -rf /var/lib/apt/lists/*
# DHI images run non-root and /app is root-owned; pre-create writable runtime dirs
# (token cache, recordings) with open perms so the non-root user can write them.
RUN mkdir -p /app/publish/data /app/publish/recordings \
    && chmod -R 0777 /app/publish/data /app/publish/recordings

# ─── LocalXpose CLI download (optional SMTP tunnel) ─────────────────────────
# LocalXpose distributes only a rolling "latest" build from S3 — there are no
# versioned release URLs. SHA256 values are from the AUR PKGBUILD and must be
# refreshed whenever loclx publishes a new binary.
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

# ─── LiveKit FFI native library download ────────────────────────────────────
FROM debian:bookworm-slim AS ffi-download
ARG LIVEKIT_FFI_VERSION
ARG TARGETARCH=amd64
RUN apt-get update && apt-get install -y --no-install-recommends curl unzip ca-certificates \
    && case "${TARGETARCH}" in \
         amd64) asset="ffi-linux-x86_64" ;; \
         arm64) asset="ffi-linux-arm64"  ;; \
         *)     echo "Unsupported arch: ${TARGETARCH}" >&2 ; exit 1 ;; \
       esac \
    && enc=$(printf '%s' "${LIVEKIT_FFI_VERSION}" | sed 's#/#%2F#') \
    && curl -sSL -o /tmp/ffi.zip \
         "https://github.com/livekit/rust-sdks/releases/download/${enc}/${asset}.zip" \
    && mkdir -p /tmp/ffi && unzip -q /tmp/ffi.zip -d /tmp/ffi \
    && find /tmp/ffi -name 'liblivekit_ffi.so' -exec install -m 755 {} /usr/local/lib/liblivekit_ffi.so \; \
    && test -f /usr/local/lib/liblivekit_ffi.so \
    && rm -rf /tmp/ffi.zip /tmp/ffi /var/lib/apt/lists/*

# ─── Final (hardened, distroless, non-root) ─────────────────────────────────
FROM ${DOTNET_RUNTIME_IMAGE} AS final
WORKDIR /app

# DHI/distroless bases ship without tzdata; copy the IANA database so TimeZoneInfo
# can resolve zones (otherwise TimeZoneNotFoundException).
COPY --from=publish /usr/share/zoneinfo /usr/share/zoneinfo
ENV TZ=Etc/UTC

COPY --from=publish /app/publish .
COPY --from=loclx-download /usr/local/bin/loclx /usr/local/bin/loclx
COPY --from=ffi-download /usr/local/lib/liblivekit_ffi.so /app/liblivekit_ffi.so
# Native lib lives next to the app; make it discoverable by the loader.
ENV LD_LIBRARY_PATH=/app

# ─── Relay configuration ───────────────────────────────────────────────
# Every setting below can be overridden at runtime via -e or docker-compose.

# Operational mode: smtp | record | dispatch  (radio is Windows-desktop only).
ENV RESGRID__RELAY__Mode=smtp

# Resgrid API (v4)
ENV RESGRID__RELAY__Resgrid__BaseUrl=""
ENV RESGRID__RELAY__Resgrid__ApiVersion=4
ENV RESGRID__RELAY__Resgrid__ClientId=""
ENV RESGRID__RELAY__Resgrid__ClientSecret=""
ENV RESGRID__RELAY__Resgrid__RefreshToken=""
ENV RESGRID__RELAY__Resgrid__Scope="openid profile email offline_access mobile"
ENV RESGRID__RELAY__Resgrid__TokenCachePath=./data/resgrid-token.json
ENV RESGRID__RELAY__Resgrid__GrantType=RefreshToken
ENV RESGRID__RELAY__Resgrid__SystemApiKey=""
ENV RESGRID__RELAY__Resgrid__DepartmentId=""

# Telemetry (optional)
ENV RESGRID__RELAY__Telemetry__Environment=
ENV RESGRID__RELAY__Telemetry__Sentry__Dsn=
ENV RESGRID__RELAY__Telemetry__Countly__Url=
ENV RESGRID__RELAY__Telemetry__Countly__AppKey=

# SMTP server (smtp mode)
ENV RESGRID__RELAY__Smtp__ServerName=resgrid-relay
ENV RESGRID__RELAY__Smtp__Port=2525
ENV RESGRID__RELAY__Smtp__DataDirectory=./data
ENV RESGRID__RELAY__Smtp__HostedMode=false
ENV RESGRID__RELAY__Smtp__ResolveDispatchCodes=true
ENV RESGRID__RELAY__Smtp__RedisCache__Enabled=false

# Voice modes (record / dispatch). Channel selector + optional department override.
# ENV RESGRID__RELAY__Voice__Channel=default
# ENV RESGRID__RELAY__Voice__DepartmentId=
# Compliance recorder (Mode=record):
# ENV RESGRID__RELAY__Recorder__Channel=all
# ENV RESGRID__RELAY__Recorder__Store=local            # local | s3 | both
# ENV RESGRID__RELAY__Recorder__LocalPath=/app/recordings
# ENV RESGRID__RELAY__Recorder__Log=jsonl              # jsonl | sqlite | none
# ENV RESGRID__RELAY__Recorder__S3__Bucket=
# ENV RESGRID__RELAY__Recorder__S3__Region=
# ENV RESGRID__RELAY__Recorder__S3__AccessKey=
# ENV RESGRID__RELAY__Recorder__S3__SecretKey=
# Dispatch tone-out (Mode=dispatch):
# ENV RESGRID__RELAY__Tts__ServiceBaseUrl=https://tts.resgrid.com
# ENV RESGRID__RELAY__DispatchVoice__Channel=default

# ─── LocalXpose tunnel (optional, smtp mode) ────────────────────────────
ENV LOCLX_ENABLED=false
# ENV LOCLX_TOKEN=""
# ENV LOCLX_RESERVED_ENDPOINT=""

EXPOSE 2525

# Shell-less launch (DHI has no /bin/sh): docker-compose-wait execs the app.
ENV WAIT_COMMAND="dotnet Resgrid.Audio.Relay.Console.dll"
ENTRYPOINT ["./wait"]
