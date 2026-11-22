using System;
using System.Threading;
using System.Threading.Tasks;
using Resgrid.Relay.Engine.Configuration;
using Resgrid.Relay.Engine.Telemetry;
using Resgrid.Providers.ApiClient.V4;
using Serilog;

namespace Resgrid.Relay.Engine.Services
{
	/// <summary>
	/// Shared lifecycle / state-machine plumbing for <see cref="IRelayService"/>
	/// implementations. Subclasses implement <see cref="ExecuteAsync"/> — the long-lived
	/// run that returns when the linked token is cancelled or the mode faults — and update
	/// <see cref="MutableStatus"/> as they connect and pass traffic.
	/// </summary>
	public abstract class RelayServiceBase : IRelayService
	{
		private readonly object _sync = new object();
		private readonly IRelayModeTelemetry _telemetry;
		private CancellationTokenSource _cts;
		private RelayServiceState _state = RelayServiceState.Stopped;

		protected RelayServiceBase(string mode, RelayHostOptions options, ILogger logger)
		{
			Mode = mode ?? throw new ArgumentNullException(nameof(mode));
			Options = options ?? throw new ArgumentNullException(nameof(options));
			Logger = logger;
			// Only the LiveKit voice modes wrap their run in the retry/circuit-breaker
			// loop and report to Sentry; everything else uses the cheap no-op telemetry.
			_telemetry = IsLiveKitMode ? RelayModeTelemetry.Create(options.Telemetry, logger) : NullRelayModeTelemetry.Instance;
		}

		/// <summary>
		/// Whether this mode is a long-lived LiveKit voice mode (radio / record / dispatch)
		/// that should be wrapped in the resilience (retry + circuit-breaker) loop and
		/// reported to Sentry. Non-LiveKit modes (e.g. SMTP) leave this false.
		/// </summary>
		protected virtual bool IsLiveKitMode => false;

		public string Mode { get; }
		public RelayServiceState State => _state;
		public event EventHandler<RelayStateChangedEventArgs> StateChanged;

		public IRelayStatus Status => MutableStatus;

		/// <summary>
		/// What faulted the last run; null unless <see cref="State"/> is <see cref="RelayServiceState.Faulted"/>. A
		/// <see cref="RelayConfigurationException"/> means the settings must change before the mode can run (the console exits
		/// with its configuration-error code for it, so a service manager stops restarting the relay).
		/// </summary>
		public Exception Fault { get; private set; }

		/// <summary>The concrete, writable status a subclass mutates while running.</summary>
		protected RelayStatus MutableStatus { get; } = new RelayStatus();

		protected RelayHostOptions Options { get; }
		protected ILogger Logger { get; }

		/// <summary>
		/// The long-lived mode run. Returns normally when <paramref name="token"/> is
		/// cancelled; throw to fault the service.
		/// </summary>
		protected abstract Task ExecuteAsync(CancellationToken token);

		/// <summary>
		/// Checks, once before the first attempt, settings the mode can never run without. Throw
		/// <see cref="RelayConfigurationException"/> so the service faults at once instead of retrying a run that cannot succeed.
		/// </summary>
		protected virtual void ValidateConfiguration()
		{
		}

		/// <summary>
		/// Runs the Resgrid API client's own option validation, which throws a plain <see cref="InvalidOperationException"/> that
		/// the resilience loop would retry as if it were an outage, and reports a failure as a
		/// <see cref="RelayConfigurationException"/>.
		/// </summary>
		protected static void ValidateApiOptions(ResgridApiClientOptions apiOptions)
		{
			if (apiOptions == null)
				throw new RelayConfigurationException("The Resgrid API settings are missing.");

			try
			{
				apiOptions.Validate();
			}
			catch (InvalidOperationException ex)
			{
				throw new RelayConfigurationException($"Invalid Resgrid API settings: {ex.Message}");
			}
		}

		/// <summary>
		/// Faults the service when a mode runner returns a non-zero exit code (a pre-flight
		/// failure), by throwing — <see cref="StartAsync"/> catches it and transitions to
		/// <see cref="RelayServiceState.Faulted"/> rather than silently reporting success.
		/// </summary>
		protected void ThrowIfFailed(int exitCode)
		{
			if (exitCode != 0)
				throw new InvalidOperationException($"The '{Mode}' relay mode exited with code {exitCode}.");
		}

		public async Task StartAsync(CancellationToken token)
		{
			// Reject re-entrant starts: only (re)start from a terminal state, and claim Starting
			// atomically before creating the CTS so two concurrent/overlapping starts can't run
			// ExecuteAsync twice or orphan the cancellation source (leaving the run unstoppable).
			RelayServiceState previous;
			lock (_sync)
			{
				if (_state == RelayServiceState.Starting || _state == RelayServiceState.Running || _state == RelayServiceState.Stopping)
					throw new InvalidOperationException($"The '{Mode}' relay service is already active (state: {_state}).");
				previous = _state;
				_state = RelayServiceState.Starting;
				Fault = null;
				// Publish the CTS inside the same lock as the state change so a StopAsync arriving
				// during the startup window cancels THIS source — no stop can slip through.
				_cts = CancellationTokenSource.CreateLinkedTokenSource(token);
			}
			StateChanged?.Invoke(this, new RelayStateChangedEventArgs(previous, RelayServiceState.Starting));

			try
			{
				// Only advance to Running if a concurrent StopAsync hasn't already moved us out of
				// Starting during the startup window (it sets Stopping + cancels _cts under _sync).
				// Atomic, so a stop that wins the race keeps Stopping/Stopped instead of reverting.
				TryTransition(RelayServiceState.Starting, RelayServiceState.Running);
				await RunWithResilienceAsync(_cts.Token).ConfigureAwait(false);
				_telemetry.ModeStopped(Mode);
				TransitionTo(RelayServiceState.Stopped);
			}
			catch (OperationCanceledException) when (_cts.IsCancellationRequested)
			{
				// Graceful stop: only when OUR shutdown token was actually requested. A cancellation
				// from elsewhere (e.g. a dependency/HttpClient timeout) is a real fault and flows to
				// the catch below so Program surfaces a failure exit code.
				_telemetry.ModeStopped(Mode);
				TransitionTo(RelayServiceState.Stopped);
			}
			catch (Exception ex)
			{
				// IRelayService contract: StartAsync returns on fault — surface it via
				// State/StateChanged rather than throwing back to the caller.
				Logger?.Error(ex, "Relay mode '{Mode}' faulted", Mode);
				_telemetry.ModeFaulted(Mode, ex);
				Fault = ex;
				TransitionTo(RelayServiceState.Faulted, ex.Message);
			}
		}

		public Task StopAsync()
		{
			var previous = RelayServiceState.Stopped;
			var transitioned = false;
			lock (_sync)
			{
				if (_state == RelayServiceState.Starting || _state == RelayServiceState.Running)
				{
					previous = _state;
					_state = RelayServiceState.Stopping;
					transitioned = true;
				}
				// Cancel the captured CTS under the same lock StartAsync publishes it in, so a stop
				// can't slip through the startup window. (Fire StateChanged outside the lock.)
				try { _cts?.Cancel(); }
				catch (ObjectDisposedException) { }
			}
			if (transitioned)
				StateChanged?.Invoke(this, new RelayStateChangedEventArgs(previous, RelayServiceState.Stopping));
			return Task.CompletedTask;
		}

		public virtual async ValueTask DisposeAsync()
		{
			try { _cts?.Cancel(); }
			catch (ObjectDisposedException) { }
			_cts?.Dispose();
			if (_telemetry != null)
				await _telemetry.DisposeAsync().ConfigureAwait(false);
		}

		/// <summary>
		/// Runs <see cref="ExecuteAsync"/>, wrapping the LiveKit voice modes in a
		/// retry-with-back-off loop guarded by a simple circuit breaker. A run that faults
		/// is restarted after an exponential, jittered delay; the breaker opens once
		/// <see cref="ResilienceOptions.MaxConsecutiveFailures"/> failures occur without a healthy run resetting the counter.
		/// An open breaker then retries every <see cref="ResilienceOptions.MaxBackoffSeconds"/> while the service stays
		/// Running (<see cref="ResilienceOptions.KeepRetrying"/>, the default), or rethrows so <see cref="StartAsync"/> faults
		/// the service. A <see cref="RelayConfigurationException"/>, including one from <see cref="ValidateConfiguration"/>,
		/// always faults at once. Non-LiveKit modes, and any mode with resilience disabled, run <see cref="ExecuteAsync"/> once directly.
		/// </summary>
		private async Task RunWithResilienceAsync(CancellationToken token)
		{
			ValidateConfiguration();

			var r = Options.Resilience ?? new ResilienceOptions();
			if (!IsLiveKitMode || !r.Enabled)
			{
				await ExecuteAsync(token).ConfigureAwait(false);
				return;
			}

			_telemetry.ModeStarting(Mode);
			var consecutiveFailures = 0;
			var breakerOpen = false;
			while (true)
			{
				token.ThrowIfCancellationRequested();
				var startTs = System.Diagnostics.Stopwatch.GetTimestamp();
				try
				{
					await ExecuteAsync(token).ConfigureAwait(false);
					return;
				}
				catch (OperationCanceledException) when (token.IsCancellationRequested)
				{
					throw;
				}
				catch (RelayConfigurationException)
				{
					// Retrying cannot fix a configuration error; fault now with its message.
					throw;
				}
				catch (Exception ex)
				{
					var ran = System.Diagnostics.Stopwatch.GetElapsedTime(startTs).TotalSeconds;
					if (ran >= r.HealthyRunSeconds)
					{
						consecutiveFailures = 0;   // it had been healthy ⇒ fresh transient failure
						if (breakerOpen)
						{
							breakerOpen = false;
							Logger?.Information("Relay mode '{Mode}' recovered (ran {Seconds:n0}s) after its circuit breaker had opened", Mode, ran);
						}
					}
					consecutiveFailures++;
					MutableStatus.LiveKit = ConnectionState.Degraded;
					if (consecutiveFailures >= r.MaxConsecutiveFailures)
					{
						if (!r.KeepRetrying)
						{
							// Circuit open ⇒ stop retrying and let the outer catch fault the service.
							Logger?.Warning(ex, "Relay mode '{Mode}' circuit breaker open after {N} consecutive failures; faulting", Mode, consecutiveFailures);
							throw;
						}

						// Circuit open, but an outage (ISP, LiveKit, the Resgrid API) outlasting the breaker must not take the mode
						// down until someone restarts it: stay Running, report LiveKit Disconnected, and keep trying at the slowest
						// back-off rate. Reported once per outage; each later attempt is a Warning.
						var openDelay = TimeSpan.FromSeconds(Math.Max(0.5, r.MaxBackoffSeconds));
						MutableStatus.LiveKit = ConnectionState.Disconnected;
						if (!breakerOpen)
						{
							breakerOpen = true;
							Logger?.Error(ex, "Relay mode '{Mode}' circuit breaker open after {N} consecutive failures; still retrying every {Delay:n0}s",
								Mode, consecutiveFailures, openDelay.TotalSeconds);
							_telemetry.ModeFaulted(Mode, ex);
						}
						else
						{
							Logger?.Warning(ex, "Relay mode '{Mode}' still failing (failure {N}); retrying in {Delay:n0}s", Mode, consecutiveFailures, openDelay.TotalSeconds);
							_telemetry.ModeRetrying(Mode, ex, consecutiveFailures, openDelay);
						}
						await Task.Delay(openDelay, token).ConfigureAwait(false);
						continue;
					}
					var delay = ComputeBackoff(r, consecutiveFailures);
					_telemetry.ModeRetrying(Mode, ex, consecutiveFailures, delay);
					Logger?.Warning(ex, "Relay mode '{Mode}' failed (failure {N}/{Max}); reconnecting in {Delay:n1}s", Mode, consecutiveFailures, r.MaxConsecutiveFailures, delay.TotalSeconds);
					await Task.Delay(delay, token).ConfigureAwait(false);
				}
			}
		}

		/// <summary>
		/// Exponential back-off (doubling from <see cref="ResilienceOptions.InitialBackoffSeconds"/>,
		/// capped at <see cref="ResilienceOptions.MaxBackoffSeconds"/>) with ±20% jitter, floored at
		/// 0.5s so retries never busy-spin.
		/// </summary>
		internal static TimeSpan ComputeBackoff(ResilienceOptions r, int failures)
		{
			var baseSecs = Math.Min(r.InitialBackoffSeconds * Math.Pow(2, failures - 1), r.MaxBackoffSeconds);
			var jitter = baseSecs * 0.2 * (Random.Shared.NextDouble() * 2 - 1);
			return TimeSpan.FromSeconds(Math.Max(0.5, baseSecs + jitter));
		}

		private void TransitionTo(RelayServiceState next, string error = null)
		{
			RelayServiceState prev;
			lock (_sync)
			{
				prev = _state;
				if (prev == next)
					return;
				_state = next;
			}
			StateChanged?.Invoke(this, new RelayStateChangedEventArgs(prev, next, error));
		}

		/// <summary>
		/// Atomically transitions <see cref="_state"/> from <paramref name="from"/> to
		/// <paramref name="to"/> only if it is currently <paramref name="from"/>, returning whether
		/// the change happened. Lets the Running transition be skipped when a concurrent StopAsync
		/// has already left Starting, so the stop isn't overwritten.
		/// </summary>
		private bool TryTransition(RelayServiceState from, RelayServiceState to)
		{
			lock (_sync)
			{
				if (_state != from)
					return false;
				_state = to;
			}
			StateChanged?.Invoke(this, new RelayStateChangedEventArgs(from, to));
			return true;
		}
	}
}
