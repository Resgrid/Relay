using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
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
	/// Application entry point. Owns the single-instance guard, the DI container, logging (live Logs screen + a rolling
	/// file), last-chance exception handling, theme, the start-to-tray decision and auto-starting the chosen relay modes.
	/// </summary>
	public partial class App : Application
	{
		private const string SingleInstanceMutexName = "Global\\ResgridRelayDesktop";

		private Mutex _singleInstanceMutex;
		private ServiceProvider _services;

		/// <summary>Process-wide service provider, exposed for XAML-created view-models if needed.</summary>
		public static IServiceProvider Services { get; private set; }

		/// <summary>Folder holding the desktop app's rolling log files.</summary>
		public static string LogDirectory { get; } = ResolveLogDirectory();

		protected override void OnStartup(StartupEventArgs e)
		{
			base.OnStartup(e);

			// (1) Single-instance guard — surface the existing instance instead of starting twice.
			_singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
			if (!createdNew)
			{
				MessageBox.Show(
					"Resgrid Relay is already running. Open it from the tray icon.",
					"Resgrid Relay",
					MessageBoxButton.OK,
					MessageBoxImage.Information);
				_singleInstanceMutex.Dispose();
				_singleInstanceMutex = null;
				Shutdown();
				return;
			}

			// (2) Build the DI container (configures logging). Create the Logs view-model now so its pump captures start-up and
			// auto-start messages, not just what is logged after the Logs screen is first opened.
			try
			{
				_services = BuildServiceProvider();
				Services = _services;
				_services.GetRequiredService<LogsViewModel>();
				_services.GetRequiredService<ShellWindow>();
			}
			catch (Exception ex)
			{
				// Without a window there is no way to reach the app, and the instance lock would block every relaunch: say why
				// and exit instead of lingering invisibly.
				Log.Fatal(ex, "Resgrid Relay could not start");
				MessageBox.Show($"Resgrid Relay could not start:\n\n{ex.Message}", "Resgrid Relay", MessageBoxButton.OK, MessageBoxImage.Error);
				_singleInstanceMutex?.Dispose();
				_singleInstanceMutex = null;
				Shutdown(1);
				return;
			}

			// (3) Last-chance exception handling: a bug in a view or command must not take down a relay that is bridging
			// a radio or receiving dispatch email.
			DispatcherUnhandledException += OnDispatcherUnhandledException;
			TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
			AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

			// (4) Apply the WPF-UI theme.
			ApplicationThemeManager.Apply(ApplicationTheme.Dark);

			// (5) Resolve the shell. Start hidden to tray when launched with --minimized.
			var startMinimized = e.Args.Any(a =>
				string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(a, "/minimized", StringComparison.OrdinalIgnoreCase));

			var shell = _services.GetRequiredService<ShellWindow>();
			MainWindow = shell;

			// --page <name> opens a specific screen (e.g. a "Relay logs" shortcut: ResgridRelay.exe --page logs).
			var pageIndex = Array.FindIndex(e.Args, a => string.Equals(a, "--page", StringComparison.OrdinalIgnoreCase));
			if (pageIndex >= 0 && pageIndex + 1 < e.Args.Length)
				shell.StartPage = e.Args[pageIndex + 1];

			if (startMinimized)
				shell.StartHiddenToTray();
			else
				shell.Show();

			Log.Information("Resgrid Relay desktop started (logs: {LogDirectory})", DisplayPath(LogDirectory));

			// (6) Auto-start the modes chosen on the Operations screen.
			_ = AutoStartAsync();
		}

		private async Task AutoStartAsync()
		{
			try
			{
				var launcher = _services.GetRequiredService<RelayModeLauncher>();
				var problems = await launcher.StartAutoStartModesAsync().ConfigureAwait(true);
				foreach (var problem in problems)
					Log.Warning("Auto-start: {Problem}", problem);
			}
			catch (Exception ex)
			{
				Log.Error(ex, "Auto-start failed");
			}
		}

		private static ServiceProvider BuildServiceProvider()
		{
			var services = new ServiceCollection();

			// Engine (per-mode service factory delegate).
			services.AddRelayEngine();

			// UI log bus + a Serilog logger fanned out to it and to a rolling file (kept 14 days) for after-the-fact
			// troubleshooting of an unattended relay.
			var logBus = new UiLogBus();
			services.AddSingleton(logBus);

			var loggerConfiguration = new LoggerConfiguration()
				.MinimumLevel.Information()
				.WriteTo.UiBus(logBus);
			try
			{
				Directory.CreateDirectory(LogDirectory);
				loggerConfiguration = loggerConfiguration.WriteTo.File(
					Path.Combine(LogDirectory, "relay-.log"),
					rollingInterval: RollingInterval.Day,
					retainedFileCountLimit: 14,
					fileSizeLimitBytes: 50L * 1024 * 1024,
					rollOnFileSizeLimit: true,
					shared: true,
					outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
			}
			catch
			{
				// No writable log folder — the Logs screen still works.
			}

			ILogger logger = loggerConfiguration.CreateLogger();
			services.AddSingleton(logger);
			Log.Logger = logger;

			// App services.
			services.AddSingleton<ConfigurationService>();
			services.AddSingleton<DesktopSettingsService>();
			services.AddSingleton<DeviceEnumerationService>();
			services.AddSingleton<RelayController>();
			services.AddSingleton<RelayModeLauncher>();
			services.AddSingleton<NavigationService>();

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

		/// <summary>A path with the user's profile folders shown as %LOCALAPPDATA% / %APPDATA%, for logs people share.</summary>
		public static string DisplayPath(string path)
		{
			if (string.IsNullOrEmpty(path))
				return path;

			foreach (var (folder, token) in new[]
			{
				(Environment.SpecialFolder.LocalApplicationData, "%LOCALAPPDATA%"),
				(Environment.SpecialFolder.ApplicationData, "%APPDATA%"),
				(Environment.SpecialFolder.UserProfile, "%USERPROFILE%")
			})
			{
				try
				{
					var root = Environment.GetFolderPath(folder);
					if (!string.IsNullOrEmpty(root) && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
						return token + path.Substring(root.Length);
				}
				catch
				{
				}
			}

			return path;
		}

		private static string ResolveLogDirectory()
		{
			try
			{
				var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
				if (!string.IsNullOrWhiteSpace(local))
					return Path.Combine(local, "Resgrid", "Relay", "logs");
			}
			catch
			{
			}

			return Path.Combine(AppContext.BaseDirectory, "logs");
		}

		private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
		{
			Log.Error(e.Exception, "Unhandled UI exception");
			e.Handled = true;

			try
			{
				MessageBox.Show(
					$"Something went wrong in the Relay window:\n\n{e.Exception.Message}\n\nRunning relay modes were not affected. Details are in the log ({LogDirectory}).",
					"Resgrid Relay",
					MessageBoxButton.OK,
					MessageBoxImage.Warning);
			}
			catch
			{
				// Never let the handler itself throw.
			}
		}

		private static void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
		{
			Log.Error(e.Exception, "Unobserved background task exception");
			e.SetObserved();
		}

		private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
		{
			Log.Fatal(e.ExceptionObject as Exception, "Fatal unhandled exception (terminating: {Terminating})", e.IsTerminating);
			Log.CloseAndFlush();
		}

		protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
		{
			// Windows sign-out / shutdown: stop the relay modes (closes LiveKit rooms, unkeys the radio, flushes recordings)
			// within a bounded time. Run off the UI thread so the teardown can't wait on the dispatcher we are blocking.
			try
			{
				var controller = _services?.GetService<RelayController>();
				if (controller != null && controller.IsRunning)
					Task.Run(() => controller.StopAllAsync()).Wait(TimeSpan.FromSeconds(8));
			}
			catch (Exception ex)
			{
				Log.Warning(ex, "Stopping relay modes at session end failed");
			}

			base.OnSessionEnding(e);
		}

		protected override void OnExit(ExitEventArgs e)
		{
			try
			{
				// Relay modes are stopped in the explicit quit flow (ShellWindow) before Shutdown(); OnExit only disposes and
				// flushes synchronously so it always runs to completion.
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
