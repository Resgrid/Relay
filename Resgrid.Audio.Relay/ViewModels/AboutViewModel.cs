using System;
using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Resgrid.Audio.Relay.ViewModels
{
	/// <summary>
	/// View-model for the About screen: product/version info (from the entry assembly), a
	/// "run at Windows startup" toggle backed by the HKCU Run registry key, and a couple of
	/// outbound links. The registry interaction is Windows-only and guarded so the type still
	/// compiles/loads on non-Windows build hosts.
	/// </summary>
	public partial class AboutViewModel : ObservableObject
	{
		// HKCU registry location Windows uses to auto-launch apps at sign-in.
		private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
		private const string RunValueName = "ResgridRelay";

		public AboutViewModel()
		{
			var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
			Version = assembly.GetName().Version?.ToString() ?? "unknown";
			_runAtStartup = ReadRunAtStartup();
		}

		[ObservableProperty]
		private string _title = "About";

		[ObservableProperty]
		private string _productName = "Resgrid Relay";

		[ObservableProperty]
		private string _version;

		[ObservableProperty]
		private string _copyright = $"© {DateTime.Now.Year} Resgrid, LLC.";

		/// <summary>
		/// When true the app registers itself in HKCU\...\Run with a <c>--minimized</c>
		/// argument so it starts to the tray at sign-in. Writing the registry is Windows-only.
		/// </summary>
		[ObservableProperty]
		private bool _runAtStartup;

		/// <summary>True only on Windows — the run-at-startup toggle is disabled elsewhere.</summary>
		public bool CanConfigureStartup => OperatingSystem.IsWindows();

		public string ResgridUrl => "https://resgrid.com";

		public string DocsUrl => "https://docs.resgrid.com";

		partial void OnRunAtStartupChanged(bool value)
		{
			WriteRunAtStartup(value);
		}

		[RelayCommand]
		private void OpenResgrid() => OpenUrl(ResgridUrl);

		[RelayCommand]
		private void OpenDocs() => OpenUrl(DocsUrl);

		private static void OpenUrl(string url)
		{
			try
			{
				Process.Start(new ProcessStartInfo
				{
					FileName = url,
					UseShellExecute = true
				});
			}
			catch
			{
				// No default browser / sandboxed — nothing actionable for the operator.
			}
		}

		private static bool ReadRunAtStartup()
		{
#if NET10_0_WINDOWS
			if (!OperatingSystem.IsWindows())
				return false;

			try
			{
				using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
				return key?.GetValue(RunValueName) is string value && !string.IsNullOrWhiteSpace(value);
			}
			catch
			{
				return false;
			}
#else
			return false;
#endif
		}

		private static void WriteRunAtStartup(bool enabled)
		{
#if NET10_0_WINDOWS
			if (!OperatingSystem.IsWindows())
				return;

			try
			{
				using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
				if (key == null)
					return;

				if (enabled)
				{
					var exePath = Environment.ProcessPath
						?? Process.GetCurrentProcess().MainModule?.FileName;
					if (!string.IsNullOrWhiteSpace(exePath))
						key.SetValue(RunValueName, $"\"{exePath}\" --minimized");
				}
				else
				{
					key.DeleteValue(RunValueName, throwOnMissingValue: false);
				}
			}
			catch
			{
				// Best-effort: a locked-down profile may deny HKCU writes; surface nothing.
			}
#endif
		}
	}
}
