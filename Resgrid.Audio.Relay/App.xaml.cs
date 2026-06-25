using System;
using System.Linq;
using System.Threading;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Resgrid.Audio.Relay.Services;
using Resgrid.Audio.Relay.ViewModels;
using Resgrid.Audio.Relay.Views;
using Resgrid.Relay.Engine;
using Resgrid.Relay.Engine.Logging;
using Serilog;
using Wpf.Ui.Appearance;

namespace Resgrid.Audio.Relay
{
	/// <summary>
	/// Application entry point. Owns the single-instance guard, the DI container, theme
	/// application and the start-to-tray vs start-visible decision. The engine is composed via
	/// <see cref="ServiceCollectionExtensions.AddRelayEngine"/>; logging is fanned out to a
	/// <see cref="UiLogBus"/> for the Logs screen.
	/// </summary>
	public partial class App : Application
	{
		private const string SingleInstanceMutexName = "Global\\ResgridRelayDesktop";

		private Mutex _singleInstanceMutex;
		private ServiceProvider _services;

		/// <summary>Process-wide service provider, exposed for XAML-created view-models if needed.</summary>
		public static IServiceProvider Services { get; private set; }

		protected override void OnStartup(StartupEventArgs e)
		{
			base.OnStartup(e);

			// (1) Single-instance guard — surface the existing instance instead of starting twice.
			_singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
			if (!createdNew)
			{
				MessageBox.Show(
					"Resgrid Relay is already running.",
					"Resgrid Relay",
					MessageBoxButton.OK,
					MessageBoxImage.Information);
				Shutdown();
				return;
			}

			// (2) Build the DI container.
			_services = BuildServiceProvider();
			Services = _services;

			// (3) Apply the WPF-UI theme.
			ApplicationThemeManager.Apply(ApplicationTheme.Dark);

			// (4) Resolve the shell. Start hidden to tray when launched with --minimized.
			var startMinimized = e.Args.Any(a =>
				string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(a, "/minimized", StringComparison.OrdinalIgnoreCase));

			var shell = _services.GetRequiredService<ShellWindow>();
			MainWindow = shell;

			if (startMinimized)
			{
				// Stay hidden — the tray icon (created with the window) keeps the app alive.
				shell.StartHiddenToTray();
			}
			else
			{
				shell.Show();
			}
		}

		private static ServiceProvider BuildServiceProvider()
		{
			var services = new ServiceCollection();

			// Engine (per-mode service factory delegate).
			services.AddRelayEngine();

			// UI log bus + a Serilog logger fanned out to it (plus Debug for local diagnostics).
			var logBus = new UiLogBus();
			services.AddSingleton(logBus);

			ILogger logger = new LoggerConfiguration()
				.MinimumLevel.Information()
				.WriteTo.UiBus(logBus)
				.CreateLogger();
			services.AddSingleton(logger);
			Log.Logger = logger;

			// App services.
			services.AddSingleton<ConfigurationService>();
			services.AddSingleton<DeviceEnumerationService>();
			services.AddSingleton<RelayController>();

			// View-models.
			services.AddSingleton<ShellViewModel>();
			services.AddTransient<DashboardViewModel>();
			services.AddTransient<OperationsViewModel>();
			services.AddTransient<ConfigurationViewModel>();
			// Singleton: owns a long-lived log pump consuming the singleton UiLogBus and must survive
			// navigation (so logs aren't lost and a reused LogsView page keeps working). Disposed by
			// the container at app shutdown.
			services.AddSingleton<LogsViewModel>();
			services.AddTransient<DevicesViewModel>();
			services.AddTransient<AboutViewModel>();

			// Views.
			services.AddTransient<DashboardView>();
			services.AddTransient<OperationsView>();
			services.AddTransient<ConfigurationView>();
			services.AddTransient<LogsView>();
			services.AddTransient<DevicesView>();
			services.AddTransient<AboutView>();

			// Shell window.
			services.AddSingleton<ShellWindow>();

			return services.BuildServiceProvider();
		}

		protected override void OnExit(ExitEventArgs e)
		{
			try
			{
				// Relay modes are stopped in the explicit quit flow (ShellWindow) before
				// Shutdown(); OnExit now only disposes and flushes synchronously so it always
				// runs to completion (an async-void OnExit could return before cleanup ran).
				_services?.Dispose();
			}
			catch
			{
				// Best-effort shutdown — never block process exit.
			}
			finally
			{
				Log.CloseAndFlush();
				_singleInstanceMutex?.Dispose();
				base.OnExit(e);
			}
		}
	}
}
