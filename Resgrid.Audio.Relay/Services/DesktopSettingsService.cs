using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;
using Resgrid.Relay.Engine.Configuration;
using Serilog;

namespace Resgrid.Audio.Relay.Services
{
	/// <summary>Desktop-only preferences (not used by the console host).</summary>
	public sealed class DesktopSettings
	{
		/// <summary>Relay modes started automatically when the app launches.</summary>
		public List<string> AutoStartModes { get; set; } = new List<string>();

		/// <summary>Closing the window hides it to the tray (relay keeps running) instead of quitting.</summary>
		public bool CloseToTray { get; set; } = true;

		/// <summary>Show a Windows notification when a relay mode faults.</summary>
		public bool NotifyOnFault { get; set; } = true;
	}

	/// <summary>
	/// Loads and saves <see cref="DesktopSettings"/> (<c>%APPDATA%\Resgrid\Relay\desktop.json</c>, next to the relay settings)
	/// and manages the "launch at Windows sign-in" entry under the current user's Run key.
	/// </summary>
	public sealed class DesktopSettingsService
	{
		private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
		private const string RunValueName = "ResgridRelay";

		private readonly ILogger _logger;

		public DesktopSettingsService(ILogger logger)
		{
			_logger = logger;
			SettingsPath = Path.Combine(Path.GetDirectoryName(RelayConfiguration.UserConfigPath) ?? ".", "desktop.json");
			Current = Load();
		}

		public string SettingsPath { get; }

		public DesktopSettings Current { get; private set; }

		public bool IsAutoStart(string mode) =>
			Current.AutoStartModes.Any(m => string.Equals(m, mode, StringComparison.OrdinalIgnoreCase));

		public void SetAutoStart(string mode, bool enabled)
		{
			Current.AutoStartModes.RemoveAll(m => string.Equals(m, mode, StringComparison.OrdinalIgnoreCase));
			if (enabled)
				Current.AutoStartModes.Add(mode);
			Save();
		}

		public void Save()
		{
			try
			{
				var directory = Path.GetDirectoryName(SettingsPath);
				if (!string.IsNullOrWhiteSpace(directory))
					Directory.CreateDirectory(directory);

				var temp = SettingsPath + ".tmp";
				File.WriteAllText(temp, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
				File.Move(temp, SettingsPath, overwrite: true);
			}
			catch (Exception ex)
			{
				_logger?.Warning(ex, "Could not save desktop settings to {Path}", SettingsPath);
			}
		}

		private DesktopSettings Load()
		{
			try
			{
				if (File.Exists(SettingsPath))
				{
					var settings = JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(SettingsPath));
					if (settings != null)
					{
						settings.AutoStartModes ??= new List<string>();
						return settings;
					}
				}
			}
			catch (Exception ex)
			{
				_logger?.Warning(ex, "Could not read desktop settings from {Path}; using defaults", SettingsPath);
			}

			return new DesktopSettings();
		}

		/// <summary>True when Windows starts the app (minimized to the tray) at sign-in.</summary>
		public bool LaunchAtSignIn
		{
			get
			{
				try
				{
					using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
					return key?.GetValue(RunValueName) is string value && !string.IsNullOrWhiteSpace(value);
				}
				catch
				{
					return false;
				}
			}
			set
			{
				try
				{
					using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
					if (key == null)
						return;

					if (value)
					{
						var exe = Environment.ProcessPath;
						if (string.IsNullOrWhiteSpace(exe))
							return;
						key.SetValue(RunValueName, $"\"{exe}\" --minimized");
					}
					else
					{
						key.DeleteValue(RunValueName, throwOnMissingValue: false);
					}
				}
				catch (Exception ex)
				{
					_logger?.Warning(ex, "Could not update the Windows sign-in startup entry");
				}
			}
		}
	}
}
