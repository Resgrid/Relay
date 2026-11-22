using System;
using System.IO;
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
			options.Smtp.DepartmentAddressDomains = OrDefault(options.Smtp.DepartmentAddressDomains, defaults.DepartmentAddressDomains);
			options.Smtp.GroupAddressDomains = OrDefault(options.Smtp.GroupAddressDomains, defaults.GroupAddressDomains);
			options.Smtp.GroupMessageAddressDomains = OrDefault(options.Smtp.GroupMessageAddressDomains, defaults.GroupMessageAddressDomains);
			options.Smtp.ListAddressDomains = OrDefault(options.Smtp.ListAddressDomains, defaults.ListAddressDomains);
			return options;
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
			return Bind(configuration);
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
					root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(UserConfigPath)) as System.Text.Json.Nodes.JsonObject;
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

			// Path-like values are resolved to absolute paths on load; resolve both sides the same way (on a copy of the
			// options) so an unchanged relative path doesn't count as a difference.
			var current = JsonSerializer.Deserialize<RelayHostOptions>(JsonSerializer.Serialize(options)) ?? new RelayHostOptions();
			ResolvePaths(defaults);
			ResolvePaths(current);

			var full = JsonSerializer.SerializeToNode(current);
			var basis = JsonSerializer.SerializeToNode(defaults);
			var diff = Diff(full, basis) as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
			return diff.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
		}

		private static void ResolvePaths(RelayHostOptions o)
		{
			if (!string.IsNullOrWhiteSpace(o.AudioConfigPath) && !Path.IsPathRooted(o.AudioConfigPath))
				o.AudioConfigPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, o.AudioConfigPath));
			if (o.Resgrid != null && !string.IsNullOrWhiteSpace(o.Resgrid.TokenCachePath) && !Path.IsPathRooted(o.Resgrid.TokenCachePath))
				o.Resgrid.TokenCachePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, o.Resgrid.TokenCachePath));
		}

		private static System.Text.Json.Nodes.JsonNode Diff(System.Text.Json.Nodes.JsonNode value, System.Text.Json.Nodes.JsonNode baseline)
		{
			if (value is System.Text.Json.Nodes.JsonObject obj)
			{
				var baseObj = baseline as System.Text.Json.Nodes.JsonObject;
				var result = new System.Text.Json.Nodes.JsonObject();
				foreach (var property in obj)
				{
					var changed = Diff(property.Value, baseObj?[property.Key]);
					if (changed != null)
						result[property.Key] = changed;
				}
				return result.Count > 0 ? result : null;
			}

			// Leaves and arrays: kept whole when they differ (an array is one setting).
			return System.Text.Json.Nodes.JsonNode.DeepEquals(value, baseline) ? null : value?.DeepClone();
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
