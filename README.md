# Resgrid Relay

Resgrid Relay is a .NET 10 solution for creating Resgrid calls from either:

1. Windows audio tone monitoring
2. Direct SMTP ingestion for emails sent to Resgrid dispatch addresses

The project now uses the **Resgrid v4 API** and **OpenID Connect refresh-token authentication** instead of the legacy v3 username/password flow.

## Solution layout

| Project | Target | Purpose |
| --- | --- | --- |
| `Providers\Resgrid.Providers.ApiClient` | `net10.0` | Resgrid v4 API + OIDC refresh-token client |
| `Resgrid.Audio.Core` | `net10.0-windows` | Windows audio detection, recording, and audio-call submission |
| `Resgrid.Audio.Relay.Console` | `net10.0`, `net10.0-windows` | Main worker/CLI entry point for SMTP and audio modes |
| `Resgrid.Audio.Relay` | `net10.0-windows` | Lightweight Windows monitoring UI |
| `Resgrid.Audio.Tests` | `net10.0-windows` | Audio, dispatch-list, and SMTP routing tests |

## Modes

### SMTP mode

- Cross-platform
- Intended for Linux/Docker deployments
- Runs an SMTP listener and creates Resgrid calls from inbound mail
- Replaces the old Postmark SMTP email API path

### Audio mode

- Windows only
- Uses the existing DTMF/audio watcher flow
- Creates a call in Resgrid and uploads captured dispatch audio as a separate v4 call file

## Requirements

### Development

- .NET SDK **10.0.202** or newer compatible .NET 10 SDK
- Windows for the WPF app, tests, and audio mode

### Runtime

- **SMTP mode:** any platform with .NET 10 runtime
- **Audio mode:** Windows with an accessible audio input device

## Authentication

Relay authenticates to Resgrid through the v4 OIDC token endpoint:

- Discovery: `https://api.resgrid.com/.well-known/openid-configuration`
- Token endpoint: `https://api.resgrid.com/api/v4/connect/token`
- Grant type: `refresh_token`

You must provide:

- `ClientId`
- `ClientSecret`
- `RefreshToken`

Relay keeps the latest rotated refresh/access token state in a configurable token cache file.

## Worker configuration (`appsettings.json` / environment variables)

The console worker reads `appsettings.json` plus environment variables prefixed with `RELAY_`.

```json
{
  "Mode": "smtp",
  "AudioConfigPath": "settings.json",
  "Resgrid": {
    "BaseUrl": "https://api.resgrid.com",
    "ApiVersion": "4",
    "ClientId": "YOUR_CLIENT_ID",
    "ClientSecret": "YOUR_CLIENT_SECRET",
    "RefreshToken": "YOUR_REFRESH_TOKEN",
    "Scope": "openid profile email offline_access mobile",
    "TokenCachePath": ".\\data\\resgrid-token.json"
  },
  "Telemetry": {
    "Environment": "production",
    "Sentry": {
      "Dsn": "YOUR_SENTRY_DSN",
      "Release": "resgrid-relay@1.0.0",
      "SendDefaultPii": true
    },
    "Countly": {
      "Url": "https://countly.example.com",
      "AppKey": "YOUR_COUNTLY_APP_KEY",
      "DeviceId": "resgrid-relay-prod-01",
      "RequestTimeoutSeconds": 5
    }
  },
  "Smtp": {
    "ServerName": "resgrid-relay",
    "Port": 2525,
    "DataDirectory": ".\\data",
    "DuplicateWindowHours": 72,
    "DefaultCallPriority": 1,
    "MaxAttachmentBytes": 10485760,
    "SaveRawMessages": true,
    "DepartmentDispatchPrefix": "G",
    "DepartmentAddressDomains": [ "dispatch.resgrid.com" ],
    "GroupAddressDomains": [ "groups.resgrid.com" ]
  }
}
```

### Environment variable example

```powershell
$env:RELAY_Mode = "smtp"
$env:RELAY_Resgrid__ClientId = "YOUR_CLIENT_ID"
$env:RELAY_Resgrid__ClientSecret = "YOUR_CLIENT_SECRET"
$env:RELAY_Resgrid__RefreshToken = "YOUR_REFRESH_TOKEN"
$env:RELAY_Telemetry__Sentry__Dsn = "YOUR_SENTRY_DSN"
$env:RELAY_Telemetry__Countly__Url = "https://countly.example.com"
$env:RELAY_Telemetry__Countly__AppKey = "YOUR_COUNTLY_APP_KEY"
$env:RELAY_Smtp__Port = "2525"
```

### SMTP observability

SMTP mode now emits structured logging for:

- connection open / close / fault events
- sender and recipient acceptance or rejection
- message receipt, duplicate suppression, routing resolution, and processing lifecycle
- Resgrid call creation, attachment handling, and processing failures

Optional integrations:

- **Sentry** captures SMTP processing and connection exceptions with message/session context attached.
- **Countly** records custom events such as `smtp_connection_started`, `smtp_connection_completed`, `smtp_message_processed`, and `smtp_message_failed`.

Notes:

- Relay logs message metadata such as sender, recipients, subject, message ids, dispatch targets, and attachment names.
- Relay intentionally does **not** mirror full email bodies into logs; the imported call note and optional raw `.eml` retention remain the system-of-record for message content.
- If `Telemetry:Countly:DeviceId` is blank, Relay derives a stable device id from the machine name and SMTP server name.

## Audio configuration (`settings.json`)

Audio mode continues to use `settings.json`, but now stores Resgrid OIDC settings instead of username/password.

```json
{
  "InputDevice": 0,
  "AudioLength": 120,
  "Multiple": false,
  "Tolerance": 100,
  "Threshold": -50,
  "EnableSilenceDetection": false,
  "Debug": false,
  "DebugKey": "",
  "Resgrid": {
    "BaseUrl": "https://api.resgrid.com",
    "ApiVersion": "4",
    "ClientId": "YOUR_CLIENT_ID",
    "ClientSecret": "YOUR_CLIENT_SECRET",
    "RefreshToken": "YOUR_REFRESH_TOKEN",
    "Scope": "openid profile email offline_access mobile",
    "TokenCachePath": ".\\data\\resgrid-token.json"
  },
  "DispatchMapping": {
    "GroupDispatchPrefix": "G",
    "DepartmentDispatchPrefix": "G"
  },
  "Watchers": [
    {
      "Id": "ee188c37-09f4-47c0-9ff1-fe34c9d6a5f1",
      "Name": "Station 1",
      "Active": true,
      "Code": "ABC123",
      "AdditionalCodes": "",
      "Type": 2,
      "Triggers": [
        {
          "Frequency1": 524.0,
          "Frequency2": 794.0,
          "Time1": 500,
          "Time2": 500,
          "Count": 2
        }
      ]
    }
  ]
}
```

### Audio watcher notes

- `Type = 1` means **department** dispatch code
- `Type = 2` means **group** dispatch code
- `AdditionalCodes` is treated as extra **group** dispatch codes

## SMTP routing behavior

Relay accepts inbound SMTP messages for configured Resgrid-style dispatch domains and converts recipient addresses into v4 `DispatchList` entries.

Examples:

| Recipient | Routing |
| --- | --- |
| `abc123@dispatch.resgrid.com` | Department dispatch code |
| `station7@groups.resgrid.com` | Group dispatch code |

Notes:

- Duplicate messages are suppressed using a persisted message-id store
- Raw `.eml` files can be saved for traceability
- Attachments are uploaded to the created call through `CallFiles/SaveCallFile`
- Structured logs, Sentry exception capture, and Countly custom events are available for SMTP operations
- If your Resgrid department-dispatch code requires a prefix other than `G`, set `Smtp:DepartmentDispatchPrefix`

## Commands

From `Resgrid.Audio.Relay.Console`:

```powershell
dotnet run --project .\Resgrid.Audio.Relay.Console\Resgrid.Audio.Relay.Console.csproj -- run
dotnet run --project .\Resgrid.Audio.Relay.Console\Resgrid.Audio.Relay.Console.csproj -- setup
dotnet run --project .\Resgrid.Audio.Relay.Console\Resgrid.Audio.Relay.Console.csproj -- devices
dotnet run --project .\Resgrid.Audio.Relay.Console\Resgrid.Audio.Relay.Console.csproj -- monitor 0
dotnet run --project .\Resgrid.Audio.Relay.Console\Resgrid.Audio.Relay.Console.csproj -- version
```

## Docker

Build the Linux SMTP worker image:

```powershell
docker build -t resgrid-relay .
```

Run it:

```powershell
docker run --rm -p 2525:2525 `
  -e RELAY_Mode=smtp `
  -e RELAY_Resgrid__ClientId=YOUR_CLIENT_ID `
  -e RELAY_Resgrid__ClientSecret=YOUR_CLIENT_SECRET `
  -e RELAY_Resgrid__RefreshToken=YOUR_REFRESH_TOKEN `
  resgrid-relay
```

## Build and test

```powershell
dotnet build "Resgrid Audio.sln"
dotnet test "Resgrid Audio.sln"
```

## Notes

- The worker now creates calls through `Calls/SaveCall`
- Audio and SMTP attachments are uploaded through `CallFiles/SaveCallFile`
- The worker derives the current Resgrid user id from the OIDC access token `sub` claim for file uploads
- The Windows audio path intentionally keeps the legacy DTMF assemblies isolated to the Windows-only project boundary

## Authors

- Shawn Jackson
- Jason Jarrett

## License

[Apache 2.0](LICENSE.txt)
