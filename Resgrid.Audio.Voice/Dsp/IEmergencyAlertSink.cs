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

		public ResgridEmergencyAlertSink(ILogger logger, bool createCall = false, int priority = 1, string dispatchList = null, IResgridCallsApi callsApi = null)
		{
			_logger = logger;
			_createCall = createCall;
			_priority = priority;
			_dispatchList = dispatchList;
			_callsApi = callsApi;
		}

		public async Task RaiseAsync(string source, string detail, CancellationToken cancellationToken = default)
		{
			_logger?.Warning("RADIO EMERGENCY [{Source}]: {Detail}", source, detail);

			if (!_createCall)
				return;

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
					ReferenceId = DateTime.UtcNow.ToString("O")
				};

				var callId = await _callsApi.SaveCallAsync(input, cancellationToken).ConfigureAwait(false);
				_logger?.Information("Created emergency call {CallId} from radio signaling", callId);
			}
			catch (Exception ex)
			{
				_logger?.Error(ex, "Failed to create Resgrid emergency call from radio signaling");
			}
		}
	}
}
