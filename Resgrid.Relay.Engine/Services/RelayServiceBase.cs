using System;
using System.Threading;
using System.Threading.Tasks;
using Resgrid.Relay.Engine.Configuration;
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
		private CancellationTokenSource _cts;
		private RelayServiceState _state = RelayServiceState.Stopped;

		protected RelayServiceBase(string mode, RelayHostOptions options, ILogger logger)
		{
			Mode = mode ?? throw new ArgumentNullException(nameof(mode));
			Options = options ?? throw new ArgumentNullException(nameof(options));
			Logger = logger;
		}

		public string Mode { get; }
		public RelayServiceState State => _state;
		public event EventHandler<RelayStateChangedEventArgs> StateChanged;

		public IRelayStatus Status => MutableStatus;

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
				// Publish the CTS inside the same lock as the state change so a StopAsync arriving
				// during the startup window cancels THIS source — no stop can slip through.
				_cts = CancellationTokenSource.CreateLinkedTokenSource(token);
			}
			StateChanged?.Invoke(this, new RelayStateChangedEventArgs(previous, RelayServiceState.Starting));

			try
			{
				TransitionTo(RelayServiceState.Running);
				await ExecuteAsync(_cts.Token).ConfigureAwait(false);
				TransitionTo(RelayServiceState.Stopped);
			}
			catch (OperationCanceledException) when (_cts.IsCancellationRequested)
			{
				// Graceful stop: only when OUR shutdown token was actually requested. A cancellation
				// from elsewhere (e.g. a dependency/HttpClient timeout) is a real fault and flows to
				// the catch below so Program surfaces a failure exit code.
				TransitionTo(RelayServiceState.Stopped);
			}
			catch (Exception ex)
			{
				// IRelayService contract: StartAsync returns on fault — surface it via
				// State/StateChanged rather than throwing back to the caller.
				Logger?.Error(ex, "Relay mode '{Mode}' faulted", Mode);
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
			await Task.CompletedTask.ConfigureAwait(false);
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
	}
}
