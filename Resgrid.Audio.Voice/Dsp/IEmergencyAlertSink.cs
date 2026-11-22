using System;
using System.Threading;
using System.Threading.Tasks;
using Resgrid.Providers.ApiClient.V4;
using Resgrid.Providers.ApiClient.V4.Models;
using Serilog;

namespace Resgrid.Audio.Voice.Dsp
{
	/// <summary>
	/// Receives emergency signals detected on the RF side (emergency tones, MDC-1200
	/// emergency op / ANI) and surfaces them to Resgrid.
	/// </summary>
	public interface IEmergencyAlertSink
	{
		Task RaiseAsync(string source, string detail, CancellationToken cancellationToken = default);
	}

	/// <summary>
	/// Raises detected radio emergencies into Resgrid by creating a high-priority call
	/// (when enabled) and always logging. Call creation is opt-in to avoid duplicate
	/// dispatches in environments that already handle alerting another way.
	/// </summary>
	public sealed class ResgridEmergencyAlertSink : IEmergencyAlertSink
	{
		private readonly ILogger _logger;
		private readonly bool _createCall;
		private readonly int _priority;
		private readonly string _dispatchList;
		private readonly IResgridCallsApi _callsApi;
		private readonly string _departmentId;
		private readonly TimeSpan _cooldown;
		private readonly Func<DateTime> _utcNow;
		private readonly object _sync = new object();
		private readonly System.Collections.Generic.Dictionary<string, DateTime> _lastRaisedUtc =
			new System.Collections.Generic.Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

		public ResgridEmergencyAlertSink(ILogger logger, bool createCall = false, int priority = 1, string dispatchList = null, IResgridCallsApi callsApi = null,
			string departmentId = null, TimeSpan? cooldown = null, Func<DateTime> utcNow = null)
		{
			_logger = logger;
			_createCall = createCall;
			_priority = priority;
			_dispatchList = dispatchList;
			_callsApi = callsApi;
			_departmentId = string.IsNullOrWhiteSpace(departmentId) ? null : departmentId;
			_cooldown = cooldown ?? TimeSpan.FromMinutes(2);
			_utcNow = utcNow ?? (() => DateTime.UtcNow);
		}

		public async Task RaiseAsync(string source, string detail, CancellationToken cancellationToken = default)
		{
			_logger?.Warning("RADIO EMERGENCY [{Source}]: {Detail}", source, detail);

			if (!_createCall)
				return;

			// A radio in emergency keeps re-sending its MDC-1200 emergency packet until it is acknowledged, and every packet is
			// decoded on its own. One emergency must create one call, not one per packet: the same signal (source + unit/tone)
			// inside the cooldown is logged above but not turned into another call.
			var key = $"{source}|{detail}";
			var now = _utcNow();
			lock (_sync)
			{
				if (_lastRaisedUtc.TryGetValue(key, out var last) && now - last < _cooldown)
					return;

				_lastRaisedUtc[key] = now;
			}

			try
			{
				var input = new NewCallInput
				{
					Priority = _priority,
					Name = $"RADIO EMERGENCY ({source})",
					Nature = detail,
					Note = $"Emergency signaling detected on the radio network by Resgrid Relay at {DateTime.UtcNow:F} UTC. Source: {source}. {detail}",
					Type = "Radio Emergency",
					DispatchList = _dispatchList,
					ReferenceId = DateTime.UtcNow.ToString("O"),
					DepartmentId = _departmentId
				};

				var callId = await _callsApi.SaveCallAsync(input, cancellationToken).ConfigureAwait(false);
				_logger?.Information("Created emergency call {CallId} from radio signaling", callId);
			}
			catch (Exception ex)
			{
				_logger?.Error(ex, "Failed to create Resgrid emergency call from radio signaling");

				// Let the radio's next resend of the same emergency try again rather than be swallowed by the cooldown.
				lock (_sync)
				{
					if (_lastRaisedUtc.TryGetValue(key, out var stamped) && stamped == now)
						_lastRaisedUtc.Remove(key);
				}
			}
		}
	}
}
