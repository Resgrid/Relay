# Copilot instructions for Resgrid Relay

## Build, test, and run

- Full solution build (Windows required for the audio projects and test project):

```powershell
dotnet build "Resgrid Audio.sln"
```

- Full test suite:

```powershell
dotnet test "Resgrid Audio.sln"
```

- Run a single NUnit test method:

```powershell
dotnet test ".\Resgrid.Audio.Tests\Resgrid.Audio.Tests.csproj" --filter "FullyQualifiedName~Resgrid.Audio.Tests.SmtpDispatchAddressParserTests.TryParse_Should_Map_Department_Address_To_Department_Dispatch_Code"
```

- For SMTP-only work on a non-Windows agent, build the cross-platform console target directly:

```powershell
dotnet build ".\Resgrid.Audio.Relay.Console\Resgrid.Audio.Relay.Console.csproj" -f net10.0
```

- Console worker entrypoints from the repo root:

```powershell
dotnet run --project .\Resgrid.Audio.Relay.Console\Resgrid.Audio.Relay.Console.csproj -- run
dotnet run --project .\Resgrid.Audio.Relay.Console\Resgrid.Audio.Relay.Console.csproj -- setup
dotnet run --project .\Resgrid.Audio.Relay.Console\Resgrid.Audio.Relay.Console.csproj -- devices
dotnet run --project .\Resgrid.Audio.Relay.Console\Resgrid.Audio.Relay.Console.csproj -- monitor 0
```

- There is no dedicated lint, analyzer, or `dotnet format` command checked into this repository.

## High-level architecture

- `Resgrid.Audio.Relay.Console\Program.cs` is the live worker entrypoint. It selects `smtp` or `audio` mode, loads `appsettings.json` plus `RELAY_` environment variables into `RelayHostOptions`, and resolves relative paths from `AppContext.BaseDirectory`.

- SMTP mode is the current cross-platform relay path. The main flow is:

  `Program.RunAsync` -> `SmtpRelayRunner` -> `RelayMailboxFilter` -> `RelayMessageStore` -> `SmtpDispatchAddressParser` / `DispatchListBuilder` -> `CallsApi`

  `RelayMessageStore` parses the MIME message, derives a stable message id, suppresses duplicates through `ProcessedMessageStore`, optionally saves the raw `.eml` under `data\messages\`, creates the call, then uploads attachments as separate call files. `SmtpTelemetry` wraps the path with Serilog logging and optional Sentry/Countly reporting.

- Resgrid authentication and API access live in `Providers\Resgrid.Providers.ApiClient\V4`. `ResgridV4ApiClient` handles OIDC discovery, refresh-token exchange, token-cache persistence, and extraction of `CurrentUserId` from the access token `sub` claim for file uploads. `CallsApi` and `HealthApi` are the thin entrypoints used by the worker and audio pipeline.

- Audio mode is Windows-only and lives in `Resgrid.Audio.Core`. `AudioEvaluator` detects watcher triggers from DTMF/pure tones, `AudioProcessor` manages active watcher lifecycles and captured audio, and `ComService` translates watcher metadata into v4 dispatch lists, creates the call, then uploads the generated MP3 as a separate call file.

- `Resgrid.Audio.Relay` is a lightweight WPF monitor for selecting an input device and watching live audio metrics. It uses `AudioRecorder` directly; the actual relay/dispatch behavior stays in the console app plus `Resgrid.Audio.Core`.

- `Resgrid.Audio.Tests` covers the current seams: SMTP routing and telemetry, v4 dispatch-list formatting, and parts of the audio path. It targets `net10.0-windows`.

## Key conventions

- Prefer compiled code over legacy files that still exist in the tree. `Resgrid.Audio.Relay.Console.csproj` removes `Args\`, `Commands\`, `Data\`, `Models\`, and `ConsoleTable.cs` from compilation. `Providers\Resgrid.Providers.ApiClient.csproj` removes `V3\**\*.cs`. `Resgrid.Audio.Relay.csproj` removes `ViewModel\**\*.cs` and older resource scaffolding. Do not treat those files as the active implementation path unless you are intentionally reviving them.

- OIDC/v4 is the only live Resgrid integration. New API work should go through `ResgridV4ApiClient`, `CallsApi`, and the V4 models instead of the legacy username/password V3 code.

- Configuration is split by mode. Host-level settings live in `Resgrid.Audio.Relay.Console\appsettings.json` plus `RELAY_` environment variables. Audio watcher definitions live in `Resgrid.Audio.Relay.Console\settings.json`. Keep new file-based settings consistent with the existing `AppContext.BaseDirectory` path resolution.

- SMTP routing is domain-driven. Configured department domains become `DispatchCodeType.Department`; configured group domains become `DispatchCodeType.Group`. `DispatchListBuilder` emits pipe-delimited `PREFIX:CODE` tokens, with a configurable department prefix and `G` as the default group prefix.

- SMTP duplicate suppression is persisted in `data\processed-messages.json`. Raw message retention uses `data\messages\`. Preserve the rollback behavior that removes a duplicate-registration entry if call creation or attachment upload fails after registration.

- `Watcher.Type` is semantic: `1` means department dispatch and `2` means group dispatch. `Watcher.AdditionalCodes` is always treated as extra group dispatch codes. When `Config.Multiple` is `false`, simultaneous hits are merged into the first active watcher; when it is `true`, they create separate active watcher flows.

- The Windows audio path still depends on checked-in binary references from `References\` and `packages\NAudio.1.8.4\...`. Avoid cleanup refactors that replace those references without revalidating the audio pipeline.

- `.editorconfig` uses tabs for `.cs` and CRLF line endings. Tests use NUnit with FluentAssertions.
