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
	///
	/// A stop leaves the tracked set at once but keeps its teardown (recording flush, LiveKit leave) in a pending set until
	/// disposal completes, so <see cref="StopAllAsync"/> — and with it quit and session end — waits for stops already in
	/// progress, not just for the services still running. <see cref="BeginShutdown"/> refuses every later start.
	/// </summary>
	public sealed class RelayController
	{
		private readonly ILogger _logger;
		private readonly Func<RelayHostOptions, ILogger, IRelayService> _factory;
		private readonly object _gate = new object();
		private readonly Dictionary<IRelayService, RunningEntry> _entries = new Dictionary<IRelayService, RunningEntry>();
		private readonly Dictionary<IRelayService, Task> _stopping = new Dictionary<IRelayService, Task>();
		private readonly Dictionary<string, string> _lastErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		private bool _shuttingDown;
		private long _stopAllRequests;

		public RelayController(ILogger logger)
			: this(logger, RelayServiceFactory.Create)
		{
		}

		/// <summary>
		/// Builds services with <paramref name="factory"/> — the delegate <c>AddRelayEngine</c> registers, which the container
		/// picks, or a fake in tests.
		/// </summary>
		public RelayController(ILogger logger, Func<RelayHostOptions, ILogger, IRelayService> factory)
		{
			_logger = logger ?? Log.Logger;
			_factory = factory ?? throw new ArgumentNullException(nameof(factory));
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

		/// <summary>True while a stopped service is still being torn down (recordings flushing, rooms closing).</summary>
		public bool HasPendingStops
		{
			get
			{
				lock (_gate)
				{
					return _stopping.Count > 0;
				}
			}
		}

		/// <summary>True once <see cref="BeginShutdown"/> was called: the app is quitting and no mode may start.</summary>
		public bool IsShuttingDown
		{
			get
			{
				lock (_gate)
				{
					return _shuttingDown;
				}
			}
		}

		/// <summary>
		/// How many times <see cref="StopAllAsync"/> has been requested. A restart compares it across its stop so a
		/// "stop all" issued meanwhile is not reversed by the restart's start.
		/// </summary>
		public long StopAllRequests => Interlocked.Read(ref _stopAllRequests);

		/// <summary>
		/// Marks the app as quitting: every later <see cref="Start"/> is refused, so a start racing the quit/session-end
		/// stop pass can't publish a mode that is never stopped. Call before <see cref="StopAllAsync"/>. Idempotent.
		/// </summary>
		public void BeginShutdown()
		{
			lock (_gate)
			{
				if (_shuttingDown)
					return;
				_shuttingDown = true;
			}

			RaiseOverallStateChanged();
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
		/// Throws <see cref="InvalidOperationException"/> once <see cref="BeginShutdown"/> was called.
		/// </summary>
		public IRelayService Start(RelayHostOptions options)
		{
			if (options == null)
				throw new ArgumentNullException(nameof(options));
			if (IsShuttingDown)
				throw ShuttingDownException();

			var service = _factory(options, _logger);
			lock (_gate)
			{
				_lastErrors.Remove(service.Mode);
			}
			var cts = new CancellationTokenSource();
			var entry = new RunningEntry(service, cts);

			service.StateChanged += OnServiceStateChanged;

			// Fully initialise the entry (including RunTask) BEFORE publishing it to _entries, so a
			// concurrent StopAsync/StopAllAsync never observes a partially-built entry (RunTask == null).
			// The run is created cold and only launched once published: a shutdown that began meanwhile
			// refuses it before anything runs, instead of leaving a mode the quit's stop pass never saw.
			var run = new Task<Task>(async () =>
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
			entry.RunTask = run.Unwrap();

			bool accepted;
			lock (_gate)
			{
				accepted = !_shuttingDown;
				if (accepted)
					_entries[service] = entry;
			}

			if (!accepted)
			{
				service.StateChanged -= OnServiceStateChanged;
				cts.Dispose();
				_ = DisposeUnstartedAsync(service);
				throw ShuttingDownException();
			}

			run.Start(TaskScheduler.Default);
			Post(() => RunningServices.Add(service));

			RaiseOverallStateChanged();
			return service;
		}

		private static InvalidOperationException ShuttingDownException() =>
			new InvalidOperationException("Resgrid Relay is shutting down; no relay mode can be started.");

		private async Task DisposeUnstartedAsync(IRelayService service)
		{
			try { await service.DisposeAsync().ConfigureAwait(false); }
			catch (Exception ex) { _logger?.Warning(ex, "Error disposing relay mode '{Mode}'.", service.Mode); }
		}

		/// <summary>
		/// Stops, disposes and removes a single running service. A service whose stop is already under way is not
		/// stopped twice; the returned task then completes when that earlier teardown does.
		/// </summary>
		public Task StopAsync(IRelayService service)
		{
			if (service == null)
				return Task.CompletedTask;

			RunningEntry entry;
			TaskCompletionSource<bool> stopped;
			lock (_gate)
			{
				if (_stopping.TryGetValue(service, out var pending))
					return pending;
				if (!_entries.TryGetValue(service, out entry))
					return Task.CompletedTask;

				// Leave the tracked set at once (so the mode reads as stopped and can be started again) but stay in the pending
				// set until disposal completes, so StopAllAsync still waits for this teardown.
				_entries.Remove(service);
				stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
				_stopping[service] = stopped.Task;
			}

			return StopEntryAsync(entry, stopped);
		}

		private async Task StopEntryAsync(RunningEntry entry, TaskCompletionSource<bool> stopped)
		{
			var service = entry.Service;
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
				lock (_gate)
				{
					_stopping.Remove(service);
				}
				stopped.TrySetResult(true);
				Post(() => RunningServices.Remove(service));
				RaiseOverallStateChanged();
			}
		}

		/// <summary>
		/// Stops every running service and waits for stops already in progress. Safe to call on shutdown. The modes stop in
		/// parallel: each teardown can take the recorder's whole upload grace, and one at a time multiplied that by the number
		/// of modes.
		/// </summary>
		public Task StopAllAsync()
		{
			Interlocked.Increment(ref _stopAllRequests);

			IRelayService[] running;
			Task[] pending;
			lock (_gate)
			{
				running = _entries.Keys.ToArray();
				pending = _stopping.Values.ToArray();
			}

			if (running.Length == 0 && pending.Length == 0)
				return Task.CompletedTask;

			return Task.WhenAll(running.Select(StopAsync).Concat(pending).ToArray());
		}

		/// <summary>The fault message from the last time <paramref name="mode"/> faulted, or null.</summary>
		public string GetLastError(string mode)
		{
			if (string.IsNullOrEmpty(mode))
				return null;

			lock (_gate)
			{
				return _lastErrors.TryGetValue(mode, out var error) ? error : null;
			}
		}

		/// <summary>Raised (on the UI thread) when a running service faults, with its mode and fault message.</summary>
		public event EventHandler<RelayFaultEventArgs> ServiceFaulted;

		private void OnServiceStateChanged(object sender, RelayStateChangedEventArgs e)
		{
			if (e.NewState == RelayServiceState.Faulted && sender is IRelayService service)
			{
				var message = string.IsNullOrWhiteSpace(e.Error) ? "The service faulted." : e.Error;
				lock (_gate)
				{
					_lastErrors[service.Mode] = message;
				}
				Post(() => ServiceFaulted?.Invoke(this, new RelayFaultEventArgs(service.Mode, message)));
			}

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

		/// <summary>The current tracked service for <paramref name="mode"/>, or null.</summary>
		public IRelayService Find(string mode)
		{
			lock (_gate)
			{
				return _entries.Keys.FirstOrDefault(s => string.Equals(s.Mode, mode, StringComparison.OrdinalIgnoreCase));
			}
		}

		/// <summary>When the tracked service for <paramref name="mode"/> was started (UTC), or null.</summary>
		public DateTime? StartedAtUtc(string mode)
		{
			lock (_gate)
			{
				foreach (var pair in _entries)
				{
					if (string.Equals(pair.Key.Mode, mode, StringComparison.OrdinalIgnoreCase))
						return pair.Value.StartedAtUtc;
				}
				return null;
			}
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
			public DateTime StartedAtUtc { get; } = DateTime.UtcNow;
		}
	}

	/// <summary>A relay mode fault, raised by <see cref="RelayController.ServiceFaulted"/>.</summary>
	public sealed class RelayFaultEventArgs : EventArgs
	{
		public RelayFaultEventArgs(string mode, string error)
		{
			Mode = mode;
			Error = error;
		}

		public string Mode { get; }
		public string Error { get; }
	}
}
