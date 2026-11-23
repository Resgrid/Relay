using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Resgrid.Relay.Engine.Configuration
{
	/// <summary>
	/// Loads and persists <see cref="RelayHostOptions"/> for the desktop UI. Layers
	/// (lowest → highest precedence): the bundled <c>appsettings.json</c>, the per-user
	/// <c>relay.user.json</c> file, then <c>RESGRID__RELAY__</c> environment variables so
	/// container/ops overrides always win over anything written from the UI.
	/// </summary>
	public static class RelayConfiguration
	{
		/// <summary>Environment variable prefix shared with the console host.</summary>
		public const string EnvPrefix = "RESGRID__RELAY__";

		/// <summary>
		/// Path of the per-user settings file:
		/// <c>%APPDATA%\Resgrid\Relay\relay.user.json</c> on Windows. On platforms where
		/// the ApplicationData folder is unavailable this falls back to a folder next to
		/// the running assembly so module load never throws.
		/// </summary>
		public static string UserConfigPath { get; } = ResolveUserConfigPath();

		private static string ResolveUserConfigPath()
		{
			string appData;
			try
			{
				appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
			}
			catch
			{
				appData = null;
			}

			if (string.IsNullOrWhiteSpace(appData))
				appData = AppContext.BaseDirectory ?? ".";

			return Path.Combine(appData, "Resgrid", "Relay", "relay.user.json");
		}

		/// <summary>
		/// Builds the layered configuration and binds it into a fresh
		/// <see cref="RelayHostOptions"/>. Relative <see cref="RelayHostOptions.AudioConfigPath"/>
		/// and <c>Resgrid.TokenCachePath</c> values are resolved against the base directory,
		/// mirroring the console's <c>LoadHostOptions</c>.
		/// </summary>
		public static RelayHostOptions Load() => Load(includeEnvironment: true);

		/// <summary>
		/// Loads only the on-disk layers (appsettings + relay.user.json), WITHOUT the
		/// <c>RESGRID__RELAY__</c> environment overrides. Use this as the base when saving so
		/// ops/container env values are never persisted into the per-user config file.
		/// </summary>
		public static RelayHostOptions LoadFromDisk() => Load(includeEnvironment: false);

		private static RelayHostOptions Load(bool includeEnvironment)
		{
			var builder = new ConfigurationBuilder()
				.SetBasePath(AppContext.BaseDirectory)
				.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
				.AddJsonFile(UserConfigPath, optional: true, reloadOnChange: false);

			if (includeEnvironment)
				builder.AddEnvironmentVariables(EnvPrefix);

			IConfigurationRoot configuration;
			try
			{
				configuration = builder.Build();
			}
			catch (Exception ex) when (ex is InvalidDataException || ex is FormatException || ex is JsonException)
			{
				// A hand-edit that breaks relay.user.json (or appsettings.json) surfaces as a configuration error with the file
				// named, not an unhandled exception.
				throw new RelayConfigurationException($"A settings file could not be read: {ex.Message}");
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
			{
				// A file that cannot be opened (another program holds it locked, or its permissions shut this account out; the
				// JSON provider opens it outside its own exception wrapping) is not wrong settings: it may open on the next try,
				// so it must not end as a configuration error, which stops a service manager from restarting the relay.
				throw new RelaySettingsUnavailableException($"A settings file could not be opened: {ex.Message}", ex);
			}

			var options = Bind(configuration);

			if (!string.IsNullOrWhiteSpace(options.AudioConfigPath) && !Path.IsPathRooted(options.AudioConfigPath))
				options.AudioConfigPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, options.AudioConfigPath));

			if (options.Resgrid != null
				&& !string.IsNullOrWhiteSpace(options.Resgrid.TokenCachePath)
				&& !Path.IsPathRooted(options.Resgrid.TokenCachePath))
			{
				options.Resgrid.TokenCachePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, options.Resgrid.TokenCachePath));
			}

			// Resgrid rotates the refresh token on every use, so without a cache file the rotated token lives only in memory and
			// the next start (or the next resilience restart, which builds a new client) presents a spent token and fails. The
			// desktop app ships no appsettings.json, so give it a per-user location; only at runtime, so Save never writes it out.
			if (includeEnvironment && options.Resgrid != null && string.IsNullOrWhiteSpace(options.Resgrid.TokenCachePath))
				options.Resgrid.TokenCachePath = DefaultTokenCachePath();

			return options;
		}

		/// <summary>
		/// Binds <paramref name="configuration"/> into a fresh <see cref="RelayHostOptions"/>. The configuration binder appends to
		/// an array that already holds items, so binding over the SMTP domain defaults turned a configured
		/// <c>[pager.county.gov]</c> into <c>[dispatch.resgrid.com, pager.county.gov]</c> (and the desktop save/load cycle grew the
		/// lists on every save). The domain arrays are bound from null, so a configured list replaces the default; an absent
		/// or empty list (the shipped appsettings.json has <c>[]</c>) keeps the default, as before, so existing deployments
		/// that relied on the resgrid.com domains keep serving them. Throws <see cref="RelayConfigurationException"/> for
		/// values that can't be converted.
		///
		/// Configuration layers merge arrays by index, so a two-domain list in appsettings.json under a one-domain list in
		/// relay.user.json still bound both (removing a domain in the desktop app never took effect). For an
		/// <see cref="IConfigurationRoot"/> the domain lists come whole from the highest layer that sets a non-empty one.
		/// </summary>
		public static RelayHostOptions Bind(IConfiguration configuration)
		{
			if (configuration == null)
				throw new ArgumentNullException(nameof(configuration));

			var options = new RelayHostOptions();
			var defaults = new SmtpRelayOptions();
			options.Smtp.DepartmentAddressDomains = null;
			options.Smtp.GroupAddressDomains = null;
			options.Smtp.GroupMessageAddressDomains = null;
			options.Smtp.ListAddressDomains = null;

			try
			{
				configuration.Bind(options);
			}
			catch (InvalidOperationException ex)
			{
				throw new RelayConfigurationException($"Invalid relay setting: {ex.InnerException?.Message ?? ex.Message}");
			}

			options.Smtp ??= new SmtpRelayOptions();
			if (configuration is IConfigurationRoot root)
			{
				options.Smtp.DepartmentAddressDomains = TopLayerList(root, SmtpKey(nameof(SmtpRelayOptions.DepartmentAddressDomains))) ?? options.Smtp.DepartmentAddressDomains;
				options.Smtp.GroupAddressDomains = TopLayerList(root, SmtpKey(nameof(SmtpRelayOptions.GroupAddressDomains))) ?? options.Smtp.GroupAddressDomains;
				options.Smtp.GroupMessageAddressDomains = TopLayerList(root, SmtpKey(nameof(SmtpRelayOptions.GroupMessageAddressDomains))) ?? options.Smtp.GroupMessageAddressDomains;
				options.Smtp.ListAddressDomains = TopLayerList(root, SmtpKey(nameof(SmtpRelayOptions.ListAddressDomains))) ?? options.Smtp.ListAddressDomains;
			}
			options.Smtp.DepartmentAddressDomains = OrDefault(options.Smtp.DepartmentAddressDomains, defaults.DepartmentAddressDomains);
			options.Smtp.GroupAddressDomains = OrDefault(options.Smtp.GroupAddressDomains, defaults.GroupAddressDomains);
			options.Smtp.GroupMessageAddressDomains = OrDefault(options.Smtp.GroupMessageAddressDomains, defaults.GroupMessageAddressDomains);
			options.Smtp.ListAddressDomains = OrDefault(options.Smtp.ListAddressDomains, defaults.ListAddressDomains);
			return options;
		}

		private static string SmtpKey(string property) => ConfigurationPath.Combine(nameof(RelayHostOptions.Smtp), property);

		/// <summary>
		/// The list at <paramref name="path"/> as the highest-precedence layer that sets a non-empty one defines it, with no
		/// entries merged in from lower layers; null when no layer sets one (an empty list falls through, as before).
		/// </summary>
		private static string[] TopLayerList(IConfigurationRoot root, string path)
		{
			foreach (var provider in root.Providers.Reverse())
			{
				var values = provider.GetChildKeys(Enumerable.Empty<string>(), path)
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.Select(key => provider.TryGet(ConfigurationPath.Combine(path, key), out var value) ? value : null)
					.Where(value => !string.IsNullOrWhiteSpace(value))
					.ToArray();
				if (values.Length > 0)
					return values;
			}

			return null;
		}

		/// <summary>
		/// <paramref name="model"/> (an on-disk model, e.g. edited in the UI) with the <c>RESGRID__RELAY__</c> environment layer
		/// applied on top, exactly as the next <see cref="Load"/> would see it. Used to validate and test what will actually run
		/// without writing environment values into the user file.
		/// </summary>
		public static RelayHostOptions WithEnvironment(RelayHostOptions model)
		{
			if (model == null)
				throw new ArgumentNullException(nameof(model));

			var json = JsonSerializer.SerializeToUtf8Bytes(model);
			var configuration = new ConfigurationBuilder()
				.AddJsonStream(new MemoryStream(json))
				.AddEnvironmentVariables(EnvPrefix)
				.Build();
			var options = Bind(configuration);

			// A relative path from the environment resolves against the base directory, as Load() does: otherwise a test run
			// from here resolved it against the working directory and used a different token cache than the running relay.
			ResolvePaths(options);
			return options;
		}

		/// <summary>
		/// The value the <c>RESGRID__RELAY__</c> environment layer sets for configuration <paramref name="key"/> (e.g.
		/// <c>Resgrid:ClientId</c>), or null when it sets none — so the UI can accept a field it leaves blank because the
		/// environment supplies it.
		/// </summary>
		public static string EnvironmentValue(string key)
		{
			if (string.IsNullOrWhiteSpace(key))
				return null;

			return new ConfigurationBuilder().AddEnvironmentVariables(EnvPrefix).Build()[key];
		}

		/// <summary>
		/// Changes only the given keys of the per-user file (creating it if needed), leaving every other setting — and the
		/// appsettings.json layer beneath it — untouched. <paramref name="patch"/> edits the file's JSON root.
		/// </summary>
		public static void PatchUserFile(Action<System.Text.Json.Nodes.JsonObject> patch)
		{
			if (patch == null)
				throw new ArgumentNullException(nameof(patch));

			System.Text.Json.Nodes.JsonObject root = null;
			if (File.Exists(UserConfigPath))
			{
				try
				{
					root = ParseUserFile(File.ReadAllText(UserConfigPath));
				}
				catch (JsonException ex)
				{
					throw new RelayConfigurationException($"{UserConfigPath} is not valid JSON ({ex.Message}); fix or delete it first.");
				}
			}

			root ??= new System.Text.Json.Nodes.JsonObject();
			patch(root);

			var directory = Path.GetDirectoryName(UserConfigPath);
			if (!string.IsNullOrWhiteSpace(directory))
				Directory.CreateDirectory(directory);
			var tempPath = UserConfigPath + ".tmp";
			File.WriteAllText(tempPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
			File.Move(tempPath, UserConfigPath, overwrite: true);
		}

		/// <summary>The JSON syntax Microsoft.Extensions.Configuration.Json accepts.</summary>
		private static readonly JsonDocumentOptions UserFileParseOptions = new JsonDocumentOptions
		{
			AllowTrailingCommas = true,
			CommentHandling = JsonCommentHandling.Skip
		};

		/// <summary>
		/// Parses the per-user file with the syntax the configuration loader accepts (trailing commas, comments), so a file the
		/// relay runs with can also be patched. Comments are not kept when the file is rewritten. Null for a non-object root.
		/// </summary>
		internal static System.Text.Json.Nodes.JsonObject ParseUserFile(string text) =>
			System.Text.Json.Nodes.JsonNode.Parse(text, documentOptions: UserFileParseOptions) as System.Text.Json.Nodes.JsonObject;

		/// <summary>The per-user file content for <paramref name="options"/>: only the values that differ from appsettings.json + defaults.</summary>
		internal static string BuildUserFileJson(RelayHostOptions options)
		{
			var baseline = new ConfigurationBuilder()
				.SetBasePath(AppContext.BaseDirectory)
				.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);

			RelayHostOptions defaults;
			try
			{
				defaults = Bind(baseline.Build());
			}
			catch (Exception)
			{
				defaults = new RelayHostOptions();
			}

			return BuildUserFileJson(options, defaults);
		}

		/// <summary>The per-user file content for <paramref name="options"/> over <paramref name="defaults"/> (the layers beneath it).</summary>
		internal static string BuildUserFileJson(RelayHostOptions options, RelayHostOptions defaults)
		{
			// Path-like values are resolved to absolute paths on load; resolve both sides the same way (on copies) so an
			// unchanged relative path doesn't count as a difference.
			var current = JsonSerializer.Deserialize<RelayHostOptions>(JsonSerializer.Serialize(options)) ?? new RelayHostOptions();
			var basis = JsonSerializer.Deserialize<RelayHostOptions>(JsonSerializer.Serialize(defaults ?? new RelayHostOptions())) ?? new RelayHostOptions();
			ResolvePaths(basis);
			ResolvePaths(current);

			var diff = TryDiff(JsonSerializer.SerializeToNode(current), JsonSerializer.SerializeToNode(basis), out var changed)
				? changed as System.Text.Json.Nodes.JsonObject
				: null;
			return (diff ?? new System.Text.Json.Nodes.JsonObject()).ToJsonString(new JsonSerializerOptions { WriteIndented = true });
		}

		private static void ResolvePaths(RelayHostOptions o)
		{
			if (!string.IsNullOrWhiteSpace(o.AudioConfigPath) && !Path.IsPathRooted(o.AudioConfigPath))
				o.AudioConfigPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, o.AudioConfigPath));
			if (o.Resgrid != null && !string.IsNullOrWhiteSpace(o.Resgrid.TokenCachePath) && !Path.IsPathRooted(o.Resgrid.TokenCachePath))
				o.Resgrid.TokenCachePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, o.Resgrid.TokenCachePath));
		}

		/// <summary>
		/// True when <paramref name="value"/> differs from <paramref name="baseline"/>, with <paramref name="diff"/> holding what
		/// to write. A changed setting can be an explicit null (a value cleared over one appsettings.json sets), so the change
		/// is reported apart from the node: dropping it as "unchanged" let the lower layer's value come back on the next load.
		/// </summary>
		private static bool TryDiff(System.Text.Json.Nodes.JsonNode value, System.Text.Json.Nodes.JsonNode baseline, out System.Text.Json.Nodes.JsonNode diff)
		{
			if (value is System.Text.Json.Nodes.JsonObject obj)
			{
				var baseObj = baseline as System.Text.Json.Nodes.JsonObject;
				var result = new System.Text.Json.Nodes.JsonObject();
				foreach (var property in obj)
				{
					if (TryDiff(property.Value, baseObj?[property.Key], out var changed))
						result[property.Key] = changed;
				}
				diff = result;
				return result.Count > 0;
			}

			// Leaves and arrays: kept whole when they differ (an array is one setting).
			if (System.Text.Json.Nodes.JsonNode.DeepEquals(value, baseline))
			{
				diff = null;
				return false;
			}

			// Null over a scalar is written out so it overrides that value. Null over a section or a list is left out: the
			// binder would null the whole section (which code expects to exist), and an empty domain list keeps the default.
			if (value == null && !(baseline is System.Text.Json.Nodes.JsonValue))
			{
				diff = null;
				return false;
			}

			diff = value?.DeepClone();
			return true;
		}

		private static string[] OrDefault(string[] configured, string[] fallback) =>
			configured == null || configured.Length == 0 ? fallback : configured;

		/// <summary>The per-user token cache the desktop app uses (%LOCALAPPDATA%\Resgrid\Relay\resgrid-token.json on Windows).</summary>
		public static string DefaultTokenCachePath()
		{
			string localAppData;
			try
			{
				localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			}
			catch
			{
				localAppData = null;
			}

			var root = string.IsNullOrWhiteSpace(localAppData)
				? Path.GetDirectoryName(UserConfigPath)
				: Path.Combine(localAppData, "Resgrid", "Relay");

			return Path.Combine(root, "resgrid-token.json");
		}

		/// <summary>
		/// Persists <paramref name="options"/> to <see cref="UserConfigPath"/> as indented JSON,
		/// creating the containing directory if needed. The object is serialized verbatim, so callers
		/// MUST pass the user-owned model — build it from <see cref="LoadFromDisk"/> (appsettings +
		/// user.json), NOT the env-merged <see cref="Load"/> result — otherwise <c>RESGRID__RELAY__</c>
		/// overrides get baked into the user file. Env overrides still win on the next <see cref="Load"/>.
		/// </summary>
		public static void Save(RelayHostOptions options)
		{
			if (options == null)
				throw new ArgumentNullException(nameof(options));

			var directory = Path.GetDirectoryName(UserConfigPath);
			if (!string.IsNullOrWhiteSpace(directory))
				Directory.CreateDirectory(directory);

			// Write only what differs from appsettings.json (and the built-in defaults). Saving the whole model pinned every
			// value in the user file, which outranks appsettings.json, so later appsettings.json edits were silently ignored
			// by a console host on the same account.
			var json = BuildUserFileJson(options);

			// Atomic write: serialize to a temp file in the same directory (so the replace is a
			// same-volume rename), then move it over the target only after the write completes, so
			// relay.user.json is never left partially written if the process dies mid-write.
			var tempPath = UserConfigPath + ".tmp";
			File.WriteAllText(tempPath, json);
			File.Move(tempPath, UserConfigPath, overwrite: true);
		}

		/// <summary>
		/// True when any <c>RESGRID__RELAY__</c> environment variable is set, so the UI can
		/// warn the operator that some settings are locked by the environment.
		/// </summary>
		public static bool HasEnvOverrides()
		{
			foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
			{
				if (entry.Key is string key && key.StartsWith(EnvPrefix, StringComparison.OrdinalIgnoreCase))
					return true;
			}

			return false;
		}
	}
}
