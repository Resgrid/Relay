using Resgrid.Relay.Engine.Configuration;

namespace Resgrid.Audio.Relay.Services
{
	/// <summary>
	/// Thin wrapper over <see cref="RelayConfiguration"/> giving the UI a single, mutable
	/// view of the current <see cref="RelayHostOptions"/> plus save / reload helpers and the
	/// "environment overrides present" flag used to warn the operator.
	/// </summary>
	public sealed class ConfigurationService
	{
		public ConfigurationService()
		{
			Current = RelayConfiguration.Load();
		}

		/// <summary>The options last loaded or saved. Bind UI editors against this instance.</summary>
		public RelayHostOptions Current { get; private set; }

		/// <summary>Path of the per-user settings file (for display in the UI).</summary>
		public string UserConfigPath => RelayConfiguration.UserConfigPath;

		/// <summary>True when one or more <c>RESGRID__RELAY__</c> environment variables are set.</summary>
		public bool HasEnvOverrides => RelayConfiguration.HasEnvOverrides();

		/// <summary>Re-reads the layered configuration and replaces <see cref="Current"/>.</summary>
		public RelayHostOptions Reload()
		{
			Current = RelayConfiguration.Load();
			return Current;
		}

		/// <summary>
		/// Returns a disk-only snapshot (appsettings + user.json, without <c>RESGRID__RELAY__</c>
		/// env overrides) for use as the base when saving, so environment values aren't baked into
		/// the file. Does not change <see cref="Current"/>.
		/// </summary>
		public RelayHostOptions LoadFromDisk()
		{
			return RelayConfiguration.LoadFromDisk();
		}

		/// <summary>
		/// Persists <paramref name="options"/> and adopts it as <see cref="Current"/>. The caller
		/// must pass a disk-based model (build it from <see cref="LoadFromDisk"/>), never the
		/// env-merged <see cref="Current"/>, so <c>RESGRID__RELAY__</c> overrides aren't written out.
		/// </summary>
		public void Save(RelayHostOptions options)
		{
			RelayConfiguration.Save(options);
			Current = options;
		}
	}
}
