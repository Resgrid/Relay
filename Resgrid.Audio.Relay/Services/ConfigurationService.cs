using System;
using Resgrid.Relay.Engine;
using Resgrid.Relay.Engine.Configuration;

namespace Resgrid.Audio.Relay.Services
{
	/// <summary>
	/// Thin wrapper over <see cref="RelayConfiguration"/> giving the UI a single view of the current
	/// <see cref="RelayHostOptions"/> plus save / reload helpers and the "environment overrides present" flag.
	///
	/// A settings layer that can't be read (malformed relay.user.json, an unconvertible RESGRID__RELAY__ value) no longer
	/// throws out of start-up — that left a window-less process holding the single-instance lock. Defaults are used instead
	/// and <see cref="LoadError"/> says why, for the Configuration screen and the pre-flight checks to show.
	/// </summary>
	public sealed class ConfigurationService
	{
		private readonly Func<RelayHostOptions> _load;

		public ConfigurationService()
			: this(RelayConfiguration.Load)
		{
		}

		/// <summary>Reads the effective options with <paramref name="load"/> instead of the settings files (tests).</summary>
		public ConfigurationService(Func<RelayHostOptions> load)
		{
			_load = load ?? throw new ArgumentNullException(nameof(load));
			Current = SafeLoad(_load);
		}

		/// <summary>The effective options (files + environment). Services start from a copy of this.</summary>
		public RelayHostOptions Current { get; private set; }

		/// <summary>Why the settings could not be read, or null when they loaded.</summary>
		public string LoadError { get; private set; }

		/// <summary>Path of the per-user settings file.</summary>
		public string UserConfigPath => RelayConfiguration.UserConfigPath;

		/// <summary>True when one or more <c>RESGRID__RELAY__</c> environment variables are set.</summary>
		public bool HasEnvOverrides => RelayConfiguration.HasEnvOverrides();

		/// <summary>Re-reads the layered configuration and replaces <see cref="Current"/>.</summary>
		public RelayHostOptions Reload()
		{
			Current = SafeLoad(_load);
			return Current;
		}

		/// <summary>
		/// The on-disk layers only (appsettings + relay.user.json, no environment) — what the Configuration form edits and
		/// saves, so environment values are never written into the file. Defaults when the files can't be read.
		/// </summary>
		public RelayHostOptions LoadFromDisk()
		{
			try
			{
				return RelayConfiguration.LoadFromDisk();
			}
			catch (Exception ex) when (ex is RelayConfigurationException || ex is RelaySettingsUnavailableException)
			{
				return new RelayHostOptions();
			}
		}

		/// <summary>
		/// Persists <paramref name="options"/> (a disk model built from <see cref="LoadFromDisk"/>) and reloads
		/// <see cref="Current"/>, so services started after a save run with the saved values plus any environment overrides.
		/// </summary>
		public void Save(RelayHostOptions options)
		{
			RelayConfiguration.Save(options);
			Current = SafeLoad(_load);
		}

		private RelayHostOptions SafeLoad(Func<RelayHostOptions> load)
		{
			try
			{
				var options = load();
				LoadError = null;
				return options;
			}
			catch (Exception ex) when (ex is RelayConfigurationException || ex is RelaySettingsUnavailableException)
			{
				// Wrong settings, or a file that could not be opened (locked by another program): start on defaults and say why.
				LoadError = ex.Message;
				// Log the exception itself (type + stack), not just its message, alongside the user settings path.
				Serilog.Log.Error(ex, "Relay settings could not be read; using defaults (user settings: {ConfigPath})", RelayConfiguration.UserConfigPath);
				return new RelayHostOptions();
			}
		}
	}
}
