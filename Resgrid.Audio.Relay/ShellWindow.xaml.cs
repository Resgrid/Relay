using System;
using System.ComponentModel;
using System.Windows;
using H.NotifyIcon.Core;
using Microsoft.Extensions.DependencyInjection;
using Resgrid.Audio.Relay.Services;
using Resgrid.Audio.Relay.ViewModels;
using Resgrid.Audio.Relay.Views;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;

namespace Resgrid.Audio.Relay
{
	/// <summary>
	/// Main shell: a Fluent window hosting the left navigation rail, the screens, a footer status bar and the system-tray
	/// integration (tooltip with live state, start/stop menu, fault notifications). Closing the window hides it to the tray
	/// while the relay keeps running (unless that preference is off); a real quit stops every mode first.
	/// </summary>
	public partial class ShellWindow : FluentWindow
	{
		private readonly IServiceProvider _services;
		private readonly ShellViewModel _viewModel;
		private readonly DesktopSettingsService _desktop;
		private bool _isExiting;
		private bool _trayHintShown;

		public ShellWindow(ShellViewModel viewModel, IServiceProvider services)
		{
			_services = services;
			_viewModel = viewModel;
			_desktop = services.GetRequiredService<DesktopSettingsService>();
			InitializeComponent();
			DataContext = viewModel;

			// Resolve navigated pages from the DI container so each view gets its view-model.
			RootNavigation.SetPageProviderService(new DiPageProvider(services));
			services.GetRequiredService<NavigationService>().Register(page => RootNavigation.Navigate(page));
			services.GetRequiredService<RelayController>().ServiceFaulted += OnServiceFaulted;
			viewModel.PropertyChanged += OnViewModelPropertyChanged;

			Loaded += OnLoaded;
			Closing += OnClosing;
		}

		/// <summary>Screen to open first (from <c>--page dashboard|operations|configuration|logs|devices|about</c>).</summary>
		public string StartPage { get; set; }

		private void OnLoaded(object sender, RoutedEventArgs e)
		{
			TrySetTrayIcon();
			RootNavigation.Navigate(PageFor(StartPage));
		}

		private static Type PageFor(string name)
		{
			switch ((name ?? "").Trim().ToLowerInvariant())
			{
				case "operations": return typeof(OperationsView);
				case "configuration":
				case "config":
				case "settings": return typeof(ConfigurationView);
				case "logs": return typeof(LogsView);
				case "devices": return typeof(DevicesView);
				case "about": return typeof(AboutView);
				default: return typeof(DashboardView);
			}
		}

		/// <summary>Called by <see cref="App"/> when launched with --minimized: stay hidden to tray.</summary>
		public void StartHiddenToTray()
		{
			Visibility = Visibility.Hidden;
			ShowInTaskbar = false;
			TrySetTrayIcon();

			// The tray icon lives in this window's visual tree, which is never loaded while the window stays hidden, so it
			// must be created explicitly or the app would run with no window and no tray icon. Efficiency mode stays off:
			// Windows throttles "efficient" processes, which would starve the radio bridge's audio.
			try { TrayIcon.ForceCreate(enablesEfficiencyMode: false); }
			catch { /* already created */ }
		}

		private void TrySetTrayIcon()
		{
			try
			{
				// Rendered from the PNG with a health dot; IconSource can't take a PNG (H.NotifyIcon needs ICO data).
				TrayIcon.Icon = TrayIconFactory.Create(_viewModel.OverallState, _viewModel.IsRunning);
			}
			catch (Exception ex)
			{
				Serilog.Log.Warning(ex, "Could not render the tray icon");
			}
		}

		private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == nameof(ShellViewModel.OverallState) || e.PropertyName == nameof(ShellViewModel.IsRunning))
				TrySetTrayIcon();
		}

		private void OnServiceFaulted(object sender, RelayFaultEventArgs e)
		{
			if (!_desktop.Current.NotifyOnFault)
				return;

			ShowTrayNotification($"{RelayModeLauncher.FriendlyName(e.Mode)} faulted", e.Error, NotificationIcon.Error);
		}

		private void ShowTrayNotification(string title, string message, NotificationIcon icon)
		{
			try
			{
				// Balloon text is capped by Windows (~255 chars).
				message ??= "";
				if (message.Length > 240)
					message = message.Substring(0, 240) + "…";
				TrayIcon.ShowNotification(title, message, icon);
			}
			catch
			{
				// Notifications unavailable (no shell / focus assist) — the Operations screen still shows the fault.
			}
		}

		private async void OnClosing(object sender, CancelEventArgs e)
		{
			if (_isExiting)
				return;

			e.Cancel = true;

			if (_desktop.Current.CloseToTray)
			{
				// Hide to tray, keep the relay running.
				Hide();
				ShowInTaskbar = false;
				if (!_trayHintShown)
				{
					_trayHintShown = true;
					ShowTrayNotification("Resgrid Relay is still running", "Relay keeps running in the tray. Right-click the tray icon to quit.", NotificationIcon.Info);
				}
				return;
			}

			await QuitAsync(confirm: true);
		}

		private void RestoreFromTray()
		{
			Show();
			ShowInTaskbar = true;
			if (WindowState == WindowState.Minimized)
				WindowState = WindowState.Normal;
			Activate();
			Topmost = true;
			Topmost = false;
		}

		private void TrayIcon_OnTrayLeftMouseUp(object sender, RoutedEventArgs e) => RestoreFromTray();

		private void TrayOpen_OnClick(object sender, RoutedEventArgs e) => RestoreFromTray();

		private void TrayStart_OnClick(object sender, RoutedEventArgs e)
		{
			if (_viewModel.StartCommand.CanExecute(null))
				_viewModel.StartCommand.Execute(null);
		}

		private void TrayStop_OnClick(object sender, RoutedEventArgs e)
		{
			if (_viewModel.StopCommand.CanExecute(null))
				_viewModel.StopCommand.Execute(null);
		}

		private async void TrayQuit_OnClick(object sender, RoutedEventArgs e) => await QuitAsync(confirm: true);

		private async System.Threading.Tasks.Task QuitAsync(bool confirm)
		{
			if (_isExiting)
				return;

			var controller = _services.GetRequiredService<RelayController>();
			if (confirm && controller.IsRunning)
			{
				var result = System.Windows.MessageBox.Show(
					"Quit Resgrid Relay? Running relay modes will be stopped.",
					"Resgrid Relay",
					System.Windows.MessageBoxButton.YesNo,
					System.Windows.MessageBoxImage.Question);

				if (result != System.Windows.MessageBoxResult.Yes)
					return;
			}

			_isExiting = true;
			controller.ServiceFaulted -= OnServiceFaulted;

			// Stop all running relay modes before shutting down — done here (not in App.OnExit, which is synchronous) so the
			// async teardown completes. Best-effort; the await resumes on the UI thread so Shutdown() runs there.
			try
			{
				await controller.StopAllAsync();
			}
			catch
			{
				// Proceed to shutdown regardless.
			}

			TrayIcon?.Dispose();
			Application.Current.Shutdown();
		}

		/// <summary>
		/// Bridges WPF-UI's navigation page resolution to the DI container so navigated views
		/// are constructed with their view-models.
		/// </summary>
		private sealed class DiPageProvider : INavigationViewPageProvider
		{
			private readonly IServiceProvider _provider;

			public DiPageProvider(IServiceProvider provider)
			{
				_provider = provider;
			}

			public object GetPage(Type pageType)
			{
				return _provider.GetService(pageType) ?? Activator.CreateInstance(pageType);
			}
		}
	}
}
