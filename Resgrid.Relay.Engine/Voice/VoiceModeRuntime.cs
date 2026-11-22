using System;
using System.Collections.Generic;
using System.Text;
using Resgrid.Audio.Voice.ToneOut;
using System.Threading;
using System.Threading.Tasks;
using Resgrid.Audio.Voice.Abstractions;
using Resgrid.Audio.Voice.Connection;
using Resgrid.Providers.ApiClient.V4.Models;

namespace Resgrid.Relay.Engine.Voice
{
	/// <summary>Shared helpers for the cross-platform voice modes.</summary>
	public static class VoiceModeRuntime
	{
		/// <summary>
		/// The voice modes join LiveKit rooms through Resgrid's Voice API, which only accepts a user token: the system API key is
		/// refused outright, and a client_credentials token has no user for Resgrid to mint the LiveKit identity from. Fail at
		/// start with the reason instead of retrying a 401/500 until the circuit breaker opens.
		/// </summary>
		public static void EnsureUserTokenAuth(Resgrid.Relay.Engine.Configuration.RelayHostOptions options, string mode)
		{
			var grant = options?.Resgrid?.GrantType ?? Resgrid.Providers.ApiClient.V4.ResgridAuthGrantType.RefreshToken;
			if (grant != Resgrid.Providers.ApiClient.V4.ResgridAuthGrantType.RefreshToken)
				throw new RelayConfigurationException(
					$"The '{mode}' mode needs Resgrid:GrantType=RefreshToken (a user's refresh token). Resgrid's Voice API does not accept " +
					$"the {grant} grant: it refuses the system API key, and a client_credentials token has no user to join the channel as.");
		}

		/// <summary>Awaits until cancellation without throwing.</summary>
		public static async Task WaitForCancellationAsync(CancellationToken cancellationToken)
		{
			try
			{
				await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
			}
			catch (TaskCanceledException)
			{
				// expected on shutdown
			}
		}

		/// <summary>
		/// Builds the spoken dispatch for a call in Resgrid Core's own wording ("New call, {name}. Nature, ... Priority, High."),
		/// so the TTS audio Core already generated for it is reused from cache.
		/// </summary>
		public static string FormatCallAnnouncement(CallResultData call, IReadOnlyDictionary<int, string> priorityNames = null)
		{
			if (call == null)
				return string.Empty;

			var priorityText = priorityNames != null && priorityNames.TryGetValue(call.Priority, out var name) && !string.IsNullOrWhiteSpace(name)
				? name
				: DispatchPromptBuilder.DefaultPriorityText(call.Priority);

			return DispatchPromptBuilder.BuildDispatchPrompt(call.Name, call.Nature, call.Address, priorityText, call.AlarmLevel);
		}

		/// <summary>
		/// Honors <see cref="Resgrid.Relay.Engine.Configuration.VoiceConnectionOptions.EnforceSeatLimit"/> before a join: false
		/// when the department's concurrent voice seats are all in use. Unknown (the API cannot tell) proceeds.
		/// </summary>
		public static async Task<bool> HasVoiceSeatAsync(Resgrid.Relay.Engine.Configuration.RelayHostOptions options, IVoiceChannelProvider provider,
			string departmentId, CancellationToken cancellationToken)
		{
			if (options?.Voice == null || !options.Voice.EnforceSeatLimit)
				return true;

			var canConnect = await provider.CanConnectAsync(string.IsNullOrWhiteSpace(departmentId) ? null : departmentId, cancellationToken).ConfigureAwait(false);
			return canConnect != false;
		}
	}

	/// <summary>
	/// Follows one joined session's connection for a voice mode: mirrors it onto the LiveKit status pill, records a hard
	/// disconnect (anything but the SDK's own transient "reconnecting"), and lets the mode check that the session can carry
	/// audio right before and after it publishes. Create it immediately after JoinAsync and call
	/// <see cref="EnsureConnectedAsync"/>, so a drop between the join and the subscription is not missed.
	/// </summary>
	public sealed class VoiceSessionMonitor : IDisposable
	{
		private readonly IVoiceRoomSession _session;
		private readonly RelayStatus _status;
		private readonly TaskCompletionSource<string> _disconnected =
			new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		private volatile bool _degraded;
		private long _interruptions;
		private int _disposed;

		public VoiceSessionMonitor(IVoiceRoomSession session, RelayStatus status = null)
		{
			_session = session ?? throw new ArgumentNullException(nameof(session));
			_status = status;
			_session.ConnectionChanged += OnConnectionChanged;
		}

		/// <summary>Completes, with the reason, when the session is hard-disconnected (it will not come back by itself).</summary>
		public Task<string> Disconnected => _disconnected.Task;

		public bool IsDisconnected => _disconnected.Task.IsCompleted;

		/// <summary>True while the SDK is reconnecting (audio published now is likely lost).</summary>
		public bool IsDegraded => _degraded;

		/// <summary>
		/// Counts every loss of the connection, transient reconnects included. Equal values before and after an operation mean
		/// the link stayed up throughout it.
		/// </summary>
		public long Interruptions => Interlocked.Read(ref _interruptions);

		/// <summary>Whether the session can carry audio right now.</summary>
		public bool IsUsable => !IsDisconnected && !_degraded && _session.IsConnected;

		/// <summary>
		/// Throws when the session is not connected once the monitor is attached (it dropped during the join or the setup
		/// after it), allowing <paramref name="grace"/> for the SDK to report a connection it is still completing.
		/// </summary>
		public async Task EnsureConnectedAsync(TimeSpan grace, CancellationToken cancellationToken)
		{
			var deadline = DateTime.UtcNow + grace;
			while (true)
			{
				if (IsDisconnected)
					throw new InvalidOperationException($"LiveKit session for '{_session.ChannelName}' disconnected ({_disconnected.Task.Result}) right after joining; reconnecting");
				if (_session.IsConnected)
					return;
				if (DateTime.UtcNow >= deadline)
					throw new InvalidOperationException($"LiveKit session for '{_session.ChannelName}' is not connected after joining; reconnecting");

				await Task.Delay(50, cancellationToken).ConfigureAwait(false);
			}
		}

		private void OnConnectionChanged(object sender, VoiceConnectionStateChange change)
		{
			if (change == null || Volatile.Read(ref _disposed) == 1)
				return;

			if (change.Connected)
			{
				// A successful (re)connect clears a prior "reconnecting" degrade so the pill recovers to Connected.
				_degraded = false;
				if (_status != null && !IsDisconnected)
					_status.LiveKit = ConnectionState.Connected;
				return;
			}

			Interlocked.Increment(ref _interruptions);
			if (string.Equals(change.Reason, "reconnecting", StringComparison.OrdinalIgnoreCase))
			{
				_degraded = true;
				if (_status != null)
					_status.LiveKit = ConnectionState.Degraded;
				return;
			}

			_disconnected.TrySetResult(string.IsNullOrWhiteSpace(change.Reason) ? "disconnected" : change.Reason);
		}

		public void Dispose()
		{
			if (Interlocked.Exchange(ref _disposed, 1) == 1)
				return;
			_session.ConnectionChanged -= OnConnectionChanged;
		}
	}
}
