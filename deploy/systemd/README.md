# Running Resgrid Relay as a Linux service (systemd)

The **CLI** (`Resgrid.Audio.Relay.Console`) is cross-platform and runs on Linux for the
LiveKit / backend modes. The **desktop GUI** (`Resgrid.Audio.Relay`, WPF) is
**Windows-only** and is not part of this deployment — use the CLI for headless Linux hosts.

## Modes available on Linux

| Mode       | Linux | What it does                                              |
|------------|:-----:|----------------------------------------------------------|
| `smtp`     |  ✅   | SMTP dispatch-email relay                                 |
| `record`   |  ✅   | Save LiveKit PTT-channel transmissions to disk / S3 + log |
| `dispatch` |  ✅   | Tone out new calls (tones + Resgrid TTS) to a PTT channel |
| `audio`    |  ❌   | Windows tone-detect importer (needs a local sound card)   |
| `radio`    |  ❌   | Radio ↔ LiveKit bridge (needs sound card + serial/HID PTT)|

The Windows-only modes resolve to a "not supported on this platform" service on Linux
(they fault with a clear message rather than crashing). The LiveKit native library
(`liblivekit_ffi.so`, linux-x64 / linux-arm64) ships inside the `Livekit.Rtc.Dotnet`
NuGet package and is included automatically in the published output; its only OS
prerequisite is `libstdc++6`, present on any standard distribution.

> One service instance runs **one** mode (set by `RESGRID__RELAY__Mode`). To run several
> modes (e.g. `record` **and** `dispatch`), install one unit per mode — see *Multiple modes*.

## 1. Prerequisites

- **.NET 10 runtime** (`dotnet`) on the host — or publish self-contained to skip it.
- **`libstdc++6`** — Debian/Ubuntu: `sudo apt-get install -y libstdc++6`.

## 2. Publish

Framework-dependent (host needs the .NET 10 runtime):

```bash
dotnet publish Resgrid.Audio.Relay.Console/Resgrid.Audio.Relay.Console.csproj \
  -c Release -f net10.0 -o ./publish
```

Self-contained (no runtime needed on the host) — choose your architecture:

```bash
dotnet publish Resgrid.Audio.Relay.Console/Resgrid.Audio.Relay.Console.csproj \
  -c Release -f net10.0 -r linux-x64 --self-contained true -o ./publish     # or linux-arm64
```

`net10.0` (not `net10.0-windows`) is the cross-platform target; it excludes the Windows
audio stack (NAudio / DtmfDetection) and the WPF GUI.

## 3. Install

```bash
# Service account + directories
sudo useradd --system --no-create-home --shell /usr/sbin/nologin resgrid
sudo mkdir -p /opt/resgrid-relay /etc/resgrid-relay

# App
sudo cp -r ./publish/* /opt/resgrid-relay/
sudo chown -R resgrid:resgrid /opt/resgrid-relay

# Config (secrets) — chmod 600
sudo cp deploy/systemd/relay.env.example /etc/resgrid-relay/relay.env
sudo chmod 600 /etc/resgrid-relay/relay.env
sudo "$EDITOR" /etc/resgrid-relay/relay.env        # set Mode + Resgrid creds + mode settings

# Unit
sudo cp deploy/systemd/resgrid-relay.service /etc/systemd/system/
```

If you published **self-contained**, change `ExecStart` in the unit to run the binary
directly: `ExecStart=/opt/resgrid-relay/Resgrid.Audio.Relay.Console run`. Otherwise confirm
the `dotnet` path (`which dotnet`); the unit assumes `/usr/bin/dotnet`.

## 4. Enable + run

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now resgrid-relay
journalctl -u resgrid-relay -f
```

## Notes

- **Graceful stop** — the CLI traps `SIGTERM` (systemd's default stop signal) and `SIGINT`,
  cancelling in-flight work so recordings flush and LiveKit disconnects cleanly;
  `TimeoutStopSec=30` bounds the drain before systemd escalates to `SIGKILL`.
- **Configuration** — everything is `RESGRID__RELAY__*` environment variables. Run
  `dotnet Resgrid.Audio.Relay.Console.dll help` for the complete, authoritative list.
- **Writable state** — the unit grants `/var/lib/resgrid-relay` (via `StateDirectory`).
  Point `Recorder__LocalPath` and/or `Resgrid__TokenCachePath` there. If you write
  recordings elsewhere, add that path to `ReadWritePaths=` in the unit (it runs under
  `ProtectSystem=strict`).
- **Multiple modes** — copy the unit per mode with its own env file, e.g.
  `resgrid-relay-record.service` (`EnvironmentFile=/etc/resgrid-relay/record.env`) and
  `resgrid-relay-dispatch.service` (`EnvironmentFile=/etc/resgrid-relay/dispatch.env`),
  then `enable --now` each.
- **Containers** — a `Dockerfile` already exists at the repo root for the same CLI if you
  prefer running it as a container instead of a host service.
