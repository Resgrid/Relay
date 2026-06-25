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

		public async Task StartAsync(CancellationToken token)
		{
			_cts = CancellationTokenSource.CreateLinkedTokenSource(token);
			TransitionTo(RelayServiceState.Starting);
			try
			{
				TransitionTo(RelayServiceState.Running);
				await ExecuteAsync(_cts.Token).ConfigureAwait(false);
				TransitionTo(RelayServiceState.Stopped);
			}
			catch (OperationCanceledException)
			{
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
			if (_state == RelayServiceState.Starting || _state == RelayServiceState.Running)
				TransitionTo(RelayServiceState.Stopping);
			try { _cts?.Cancel(); }
			catch (ObjectDisposedException) { }
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
