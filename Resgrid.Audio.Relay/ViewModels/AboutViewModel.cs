using System;
using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Resgrid.Audio.Relay.Services;

namespace Resgrid.Audio.Relay.ViewModels
{
	/// <summary>
	/// View-model for the About screen: product/version info, the "start at Windows sign-in" toggle (the same setting as on
	/// the Operations screen, through <see cref="DesktopSettingsService"/>), and links to Resgrid and the Relay docs.
	/// </summary>
	public partial class AboutViewModel : ObservableObject
	{
		private readonly DesktopSettingsService _desktop;
		private readonly bool _loading;

		public AboutViewModel(DesktopSettingsService desktop)
		{
			_desktop = desktop;
			var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
			var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
			Version = FormatVersion(informational) ?? assembly.GetName().Version?.ToString() ?? "unknown";

			_loading = true;
			RunAtStartup = desktop.LaunchAtSignIn;
			_loading = false;
		}

		/// <summary>"2.1.42+&lt;full commit sha&gt;" → "2.1.42 (abc1234)".</summary>
		private static string FormatVersion(string informational)
		{
			if (string.IsNullOrWhiteSpace(informational))
				return null;
			var plus = informational.IndexOf('+');
			if (plus < 0)
				return informational;
			var sha = informational.Substring(plus + 1);
			return $"{informational.Substring(0, plus)} ({(sha.Length > 7 ? sha.Substring(0, 7) : sha)})";
		}

		[ObservableProperty]
		private string _title = "About";

		[ObservableProperty]
		private string _productName = "Resgrid Relay";

		[ObservableProperty]
		private string _version;

		[ObservableProperty]
		private string _copyright = $"© {DateTime.Now.Year} Resgrid, LLC.";

		/// <summary>Starts Relay minimized to the tray at Windows sign-in (HKCU Run entry).</summary>
		[ObservableProperty]
		private bool _runAtStartup;

		public bool CanConfigureStartup => OperatingSystem.IsWindows();

		public string ResgridUrl => "https://resgrid.com";

		public string DocsUrl => "https://docs.resgrid.com/apps/relay/";

		partial void OnRunAtStartupChanged(bool value)
		{
			if (!_loading)
				_desktop.LaunchAtSignIn = value;
		}

		[RelayCommand]
		private void OpenResgrid() => OpenUrl(ResgridUrl);

		[RelayCommand]
		private void OpenDocs() => OpenUrl(DocsUrl);

		private static void OpenUrl(string url)
		{
			try
			{
				Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
			}
			catch
			{
				// No default browser / sandboxed — nothing actionable for the operator.
			}
		}
	}
}
