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

			var configuration = builder.Build();

			var options = new RelayHostOptions();
			configuration.Bind(options);

			if (!string.IsNullOrWhiteSpace(options.AudioConfigPath) && !Path.IsPathRooted(options.AudioConfigPath))
				options.AudioConfigPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, options.AudioConfigPath));

			if (options.Resgrid != null
				&& !string.IsNullOrWhiteSpace(options.Resgrid.TokenCachePath)
				&& !Path.IsPathRooted(options.Resgrid.TokenCachePath))
			{
				options.Resgrid.TokenCachePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, options.Resgrid.TokenCachePath));
			}

			return options;
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

			var json = JsonSerializer.Serialize(options, new JsonSerializerOptions { WriteIndented = true });

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
