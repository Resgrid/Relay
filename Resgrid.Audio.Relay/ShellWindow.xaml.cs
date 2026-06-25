using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Resgrid.Audio.Relay.Services;
using Resgrid.Audio.Relay.ViewModels;
using Resgrid.Audio.Relay.Views;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;

namespace Resgrid.Audio.Relay
{
	/// <summary>
	/// Main shell: a Fluent window hosting the left navigation rail, the six screens, a footer
	/// status bar and the system-tray integration. Closing the window hides it to the tray
	/// while the relay keeps running; a real quit only happens via the tray menu (or explicit
	/// shutdown), at which point <see cref="App.OnExit"/> stops the controller.
	/// </summary>
	public partial class ShellWindow : FluentWindow
	{
		private readonly IServiceProvider _services;
		private bool _isExiting;

		public ShellWindow(ShellViewModel viewModel, IServiceProvider services)
		{
			_services = services;
			InitializeComponent();
			DataContext = viewModel;

			// Resolve navigated pages from the DI container so each view gets its view-model.
			RootNavigation.SetPageProviderService(new DiPageProvider(services));

			Loaded += OnLoaded;
			Closing += OnClosing;
		}

		private void OnLoaded(object sender, RoutedEventArgs e)
		{
			TrySetTrayIcon();

			// Navigate to the first screen.
			RootNavigation.Navigate(typeof(DashboardView));
		}

		/// <summary>Called by <see cref="App"/> when launched with --minimized: stay hidden to tray.</summary>
		public void StartHiddenToTray()
		{
			// Ensure the tray icon is realized even though the window is never shown.
			Visibility = Visibility.Hidden;
			ShowInTaskbar = false;
			TrySetTrayIcon();
		}

		private void TrySetTrayIcon()
		{
			try
			{
				// Use the bundled logo as the tray icon source.
				// TODO: health-colored tray icon + balloon notifications (runtime-only; can't verify on Linux).
				var uri = new Uri("pack://application:,,,/Resources/ResgridLogo.png", UriKind.Absolute);
				TrayIcon.IconSource = new BitmapImage(uri);
			}
			catch
			{
				// Asset missing or running headless — leave the default icon.
			}
		}

		private void OnClosing(object sender, CancelEventArgs e)
		{
			if (_isExiting)
				return;

			// Intercept close → hide to tray, keep the controller running.
			e.Cancel = true;
			Hide();
			ShowInTaskbar = false;
		}

		private void RestoreFromTray()
		{
			Show();
			ShowInTaskbar = true;
			WindowState = WindowState.Normal;
			Activate();
			Topmost = true;
			Topmost = false;
		}

		private void TrayIcon_OnTrayLeftMouseUp(object sender, RoutedEventArgs e)
		{
			RestoreFromTray();
		}

		private void TrayOpen_OnClick(object sender, RoutedEventArgs e)
		{
			RestoreFromTray();
		}

		private async void TrayQuit_OnClick(object sender, RoutedEventArgs e)
		{
			var result = System.Windows.MessageBox.Show(
				"Quit Resgrid Relay? Any running relay modes will be stopped.",
				"Resgrid Relay",
				System.Windows.MessageBoxButton.YesNo,
				System.Windows.MessageBoxImage.Question);

			if (result != System.Windows.MessageBoxResult.Yes)
				return;

			_isExiting = true;
			TrayIcon?.Dispose();

			// Stop all running relay modes before shutting down — done here (not in App.OnExit,
			// which is now synchronous) so the async teardown actually completes. Best-effort;
			// the default await resumes on the UI thread so Shutdown() is called there.
			try
			{
				await _services.GetRequiredService<RelayController>().StopAllAsync();
			}
			catch
			{
				// Proceed to shutdown regardless.
			}

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
