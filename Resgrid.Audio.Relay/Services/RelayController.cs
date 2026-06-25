using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Resgrid.Relay.Engine;
using Resgrid.Relay.Engine.Configuration;
using Resgrid.Relay.Engine.Services;
using Serilog;

namespace Resgrid.Audio.Relay.Services
{
	/// <summary>
	/// Owns the set of running <see cref="IRelayService"/> instances for the desktop app and
	/// exposes a button-friendly start/stop lifecycle. Each started mode runs its long-lived
	/// <see cref="IRelayService.StartAsync"/> on a tracked background task with its own
	/// <see cref="CancellationTokenSource"/>; stopping cancels, awaits, disposes and removes it.
	///
	/// All <see cref="RunningServices"/> mutations are marshalled onto the WPF UI thread via
	/// <see cref="Application.Current"/>'s dispatcher so the collection can be bound directly.
	/// </summary>
	public sealed class RelayController
	{
		private readonly ILogger _logger;
		private readonly object _gate = new object();
		private readonly Dictionary<IRelayService, RunningEntry> _entries = new Dictionary<IRelayService, RunningEntry>();

		public RelayController(ILogger logger)
		{
			_logger = logger ?? Log.Logger;
		}

		/// <summary>The currently running relay services. Bind UI lists against this.</summary>
		public ObservableCollection<IRelayService> RunningServices { get; } = new ObservableCollection<IRelayService>();

		/// <summary>Raised after <see cref="RunningServices"/> changes or a service changes state.</summary>
		public event EventHandler OverallStateChanged;

		/// <summary>
		/// Worst-of the running services' states, used to drive the shell traffic-light:
		/// Faulted &gt; Starting/Stopping &gt; Running &gt; Stopped. Returns
		/// <see cref="RelayServiceState.Stopped"/> when nothing is running.
		/// </summary>
		public RelayServiceState OverallState
		{
			get
			{
				lock (_gate)
				{
					if (_entries.Count == 0)
						return RelayServiceState.Stopped;

					var states = _entries.Keys.Select(s => s.State).ToList();
					if (states.Any(s => s == RelayServiceState.Faulted))
						return RelayServiceState.Faulted;
					if (states.Any(s => s == RelayServiceState.Starting || s == RelayServiceState.Stopping))
						return RelayServiceState.Starting;
					if (states.Any(s => s == RelayServiceState.Running))
						return RelayServiceState.Running;

					return RelayServiceState.Stopped;
				}
			}
		}

		/// <summary>True when at least one service is being tracked.</summary>
		public bool IsRunning
		{
			get
			{
				lock (_gate)
				{
					return _entries.Count > 0;
				}
			}
		}

		/// <summary>
		/// True when a service for <paramref name="mode"/> is currently tracked. Reads the
		/// authoritative <c>_entries</c> set under <c>_gate</c> — not the UI-marshalled
		/// <see cref="RunningServices"/> collection — so a just-started mode is seen immediately,
		/// independent of when the collection update is dispatched to the UI thread.
		/// </summary>
		public bool IsModeRunning(string mode)
		{
			lock (_gate)
			{
				foreach (var service in _entries.Keys)
				{
					if (string.Equals(service.Mode, mode, StringComparison.OrdinalIgnoreCase))
						return true;
				}

				return false;
			}
		}

		/// <summary>
		/// Creates the configured mode via <see cref="RelayServiceFactory"/>, adds it to the
		/// running set and launches its background run loop. Returns the created service.
		/// </summary>
		public IRelayService Start(RelayHostOptions options)
		{
			if (options == null)
				throw new ArgumentNullException(nameof(options));

			var service = RelayServiceFactory.Create(options, _logger);
			var cts = new CancellationTokenSource();
			var entry = new RunningEntry(service, cts);

			service.StateChanged += OnServiceStateChanged;

			// Fully initialise the entry (including RunTask) BEFORE publishing it to _entries, so a
			// concurrent StopAsync/StopAllAsync never observes a partially-built entry (RunTask == null).
			entry.RunTask = Task.Run(async () =>
			{
				try
				{
					await service.StartAsync(cts.Token).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					// Normal on Stop.
				}
				catch (Exception ex)
				{
					_logger?.Error(ex, "Relay mode '{Mode}' terminated unexpectedly.", service.Mode);
				}
			});

			lock (_gate)
			{
				_entries[service] = entry;
			}

			Post(() => RunningServices.Add(service));

			RaiseOverallStateChanged();
			return service;
		}

		/// <summary>Stops, disposes and removes a single running service.</summary>
		public async Task StopAsync(IRelayService service)
		{
			if (service == null)
				return;

			RunningEntry entry;
			lock (_gate)
			{
				if (!_entries.TryGetValue(service, out entry))
					return;
				_entries.Remove(service);
			}

			service.StateChanged -= OnServiceStateChanged;

			try
			{
				entry.Cts.Cancel();
				try { await service.StopAsync().ConfigureAwait(false); }
				catch (Exception ex) { _logger?.Warning(ex, "Error stopping relay mode '{Mode}'.", service.Mode); }

				if (entry.RunTask != null)
				{
					try { await entry.RunTask.ConfigureAwait(false); }
					catch { /* already logged in run loop */ }
				}
			}
			finally
			{
				try { await service.DisposeAsync().ConfigureAwait(false); }
				catch (Exception ex) { _logger?.Warning(ex, "Error disposing relay mode '{Mode}'.", service.Mode); }

				entry.Cts.Dispose();
				Post(() => RunningServices.Remove(service));
				RaiseOverallStateChanged();
			}
		}

		/// <summary>Stops every running service. Safe to call on shutdown.</summary>
		public async Task StopAllAsync()
		{
			IRelayService[] running;
			lock (_gate)
			{
				running = _entries.Keys.ToArray();
			}

			foreach (var service in running)
				await StopAsync(service).ConfigureAwait(false);
		}

		private void OnServiceStateChanged(object sender, RelayStateChangedEventArgs e)
		{
			RaiseOverallStateChanged();
		}

		private void RaiseOverallStateChanged()
		{
			Post(() => OverallStateChanged?.Invoke(this, EventArgs.Empty));
		}

		private void Post(Action action)
		{
			// Resolve the UI dispatcher at call time (not construction) so updates always land on the
			// WPF UI thread even if no SynchronizationContext was current when the controller was built.
			var dispatcher = Application.Current?.Dispatcher;
			if (dispatcher == null || dispatcher.CheckAccess())
				action();
			else
				dispatcher.BeginInvoke(action);
		}

		private sealed class RunningEntry
		{
			public RunningEntry(IRelayService service, CancellationTokenSource cts)
			{
				Service = service;
				Cts = cts;
			}

			public IRelayService Service { get; }
			public CancellationTokenSource Cts { get; }
			public Task RunTask { get; set; }
		}
	}
}
