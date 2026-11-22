using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Resgrid.Audio.Core.Events;
using Resgrid.Audio.Core.Model;
using Resgrid.Providers.ApiClient.V4;
using Resgrid.Providers.ApiClient.V4.Models;
using Serilog;
using Serilog.Core;

namespace Resgrid.Audio.Core
{
	public class ComService
	{
		private readonly ILogger _logger;
		private readonly AudioProcessor _audioProcessor;
		private readonly IResgridApiClient _apiClient;
		private readonly IResgridHealthApi _healthApi;
		private readonly IResgridCallsApi _callsApi;
		private readonly IResgridLookupsApi _lookupsApi;
		private readonly string _departmentId;

		private Config _config;

		public event EventHandler<CallCreatedEventArgs> CallCreatedEvent;

		public ComService(ILogger logger, AudioProcessor audioProcessor, IResgridApiClient apiClient, IResgridHealthApi healthApi, IResgridCallsApi callsApi,
			IResgridLookupsApi lookupsApi = null, string departmentId = null)
		{
			_logger = logger;
			_audioProcessor = audioProcessor;
			_apiClient = apiClient;
			_healthApi = healthApi;
			_callsApi = callsApi;
			_lookupsApi = lookupsApi ?? (apiClient != null ? new LookupsApi(apiClient) : null);
			_departmentId = String.IsNullOrWhiteSpace(departmentId) ? null : departmentId.Trim();
		}

		public void Init(Config config)
		{
			_config = config;

			_audioProcessor.TriggerProcessingFinished += _audioProcessor_TriggerProcessingFinished;
		}

		public bool IsConnectionValid()
		{
			try
			{
				return _healthApi.IsHealthyAsync().GetAwaiter().GetResult();
			}
			catch (HttpRequestException ex)
			{
				_logger.Error(ex.ToString());
				return false;
			}
			catch (InvalidOperationException ex)
			{
				_logger.Error(ex.ToString());
				return false;
			}
		}

		private void _audioProcessor_TriggerProcessingFinished(object sender, Events.TriggerProcessedEventArgs e)
		{
			if (e.Mp3Audio == null || e.Mp3Audio.Length == 0)
			{
				_logger.Warning($"No Dispatch Audio detected, unable to save call for {e.Watcher.Name}");
				return;
			}

			try
			{
				CreateCallAsync(e).GetAwaiter().GetResult();
			}
			catch (Exception ex)
			{
				// Runs on the audio processor's timer: nothing may escape, or the pipeline stops creating calls.
				_logger.Error(ex, "Failed to create the Resgrid call for watcher {Watcher}; this dispatch was not sent to Resgrid", e.Watcher?.Name);
			}
		}

		/// <summary>
		/// Creates the call and uploads the captured audio. Both requests carry an idempotency key derived from the trigger, so a
		/// retry after a timeout or a 5xx returns the call/file Resgrid already saved instead of paging twice.
		/// </summary>
		private async Task CreateCallAsync(Events.TriggerProcessedEventArgs e)
		{
			var dispatchContext = BuildDispatchContext(e.Watcher);
			var watchersToned = String.Join(", ", dispatchContext.WatcherNames);
			// A department watcher pages everyone ("0") whatever its code, so decide from the watcher type (primary or riders):
			// keyed off a non-empty Code, a department watcher saved with no code (setup's default) built no targets and every
			// page failed with "no valid dispatch targets".
			var isDepartmentDispatch = e.Watcher.Type == (int)DispatchCodeType.Department
				|| (e.Watcher.GetAdditionalWatchers()?.Any(w => w.Type == (int)DispatchCodeType.Department) ?? false)
				|| dispatchContext.DispatchCodes.Any(x => x.Type == DispatchCodeType.Department);

			string dispatchList;
			bool? isPending = null;
			if (isDepartmentDispatch)
			{
				// A department (ALLCALL) watcher pages everyone: "0". DispatchListBuilder leaves department codes out, so this
				// used to end in "no valid dispatch targets" and the ALLCALL was never created. The system API key is never
				// allowed to page a whole department by itself, so with it the call is saved pending for a dispatcher to send.
				dispatchList = "0";
				if (_apiClient?.IsSystemCaller == true)
					isPending = true;
			}
			else
			{
				await ResolveGroupCodesAsync(dispatchContext.DispatchCodes).ConfigureAwait(false);
				dispatchList = DispatchListBuilder.Build(dispatchContext.DispatchCodes, _config?.DispatchMapping?.DepartmentDispatchPrefix);
				if (String.IsNullOrWhiteSpace(dispatchList))
					throw new InvalidOperationException($"No valid Resgrid dispatch targets were found for watcher '{e.Watcher.Name}'.");
			}

			var callName = isDepartmentDispatch
				? $"ALLCALL Audio Import {DateTime.Now:g}"
				: $"Audio Import {DateTime.Now:g}";

			var callInput = new NewCallInput
			{
				Priority = 1,
				Name = callName,
				Nature = $"Relay import, listen to audio for call info. Toned: {watchersToned}",
				Note = $"Audio imported call from a radio dispatch using the Resgrid Relay app. Listen to the attached audio for call information. Call was created on {DateTime.Now:F}. Department dispatch: {isDepartmentDispatch}. Watchers toned: {watchersToned}",
				DispatchList = dispatchList,
				IsPending = isPending,
				ExternalId = e.Watcher.Id.ToString(),
				ReferenceId = e.Watcher.TriggerFiredTimestamp.ToString("O"),
				Type = "Relay Audio",
				DepartmentId = _departmentId
			};

			var idempotencyKey = $"relay-audio:{e.Watcher.Id:N}:{e.Watcher.TriggerFiredTimestamp.ToUniversalTime():yyyyMMddTHHmmssfff}";
			var savedCallId = await WithRetriesAsync(() => _callsApi.SaveCallAsync(callInput, idempotencyKey)).ConfigureAwait(false);

			// Resgrid requires SaveCallFile's UserId but uses it only for system callers; a token caller's file is attributed to
			// the token's own user. Its access tokens are encrypted, so with a token grant CurrentUserId is usually null, and
			// requiring it threw after the call was already paged, losing the audio. Token callers send a placeholder (as the
			// SMTP relay does); system callers fall back to the department id.
			var userId = _apiClient.CurrentUserId;
			if (String.IsNullOrWhiteSpace(userId))
			{
				if (_apiClient.IsSystemCaller)
				{
					userId = _departmentId;
					if (String.IsNullOrWhiteSpace(userId))
						_logger.Warning("Uploading audio for call {CallId} without a user id; set Resgrid:DepartmentId for system-key relays", savedCallId);
				}
				else
				{
					userId = "resgrid-relay";
				}
			}

			var file = new SaveCallFileInput
			{
				CallId = savedCallId,
				UserId = userId,
				Type = (int)CallFileType.Audio,
				Name = $"Relay_{e.Watcher.TriggerFiredTimestamp:s}".Replace(":", "_") + ".mp3",
				Data = Convert.ToBase64String(e.Mp3Audio),
				Note = $"Captured dispatch audio for watcher {e.Watcher.Name}",
				DepartmentId = _departmentId
			};

			try
			{
				await WithRetriesAsync(() => _callsApi.SaveCallFileAsync(file, idempotencyKey + "#audio")).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				// The call is already out; report the missing audio instead of losing the CallCreated notification.
				_logger.Error(ex, "Call {CallId} was created but its dispatch audio could not be uploaded", savedCallId);
			}

			string callNumber = null;
			try
			{
				var savedCall = await _callsApi.GetCallAsync(savedCallId, _departmentId).ConfigureAwait(false);
				callNumber = savedCall?.Data?.Number;
			}
			catch (Exception ex)
			{
				_logger.Warning(ex, "Could not read back call {CallId} for its number", savedCallId);
			}

			CallCreatedEvent?.Invoke(this, new CallCreatedEventArgs(e.Watcher.Name, savedCallId, callNumber, DateTime.Now));
		}

		/// <summary>
		/// Group watchers carry the group's dispatch email code (the local part of its groups-domain address), but the DispatchList
		/// needs the numeric group id. A code Resgrid cannot resolve is sent as-is, which Resgrid still matches to a group id or
		/// name, so configurations that used the id or name directly keep working.
		/// </summary>
		private async Task ResolveGroupCodesAsync(List<DispatchCode> dispatchCodes)
		{
			if (_lookupsApi == null)
				return;

			foreach (var code in dispatchCodes.Where(x => x.Type == DispatchCodeType.Group && !x.IsResolved && !Int32.TryParse(x.Code, out _)))
			{
				try
				{
					// Same bounded retry as the save: one 503 from the token endpoint threw an authentication error out of here
					// before the save's retries ever ran, and the page was dropped. A code Resgrid does not know (null) is sent
					// as-is; an authentication failure that outlasts the retries still aborts the dispatch.
					var group = await WithRetriesAsync(() => _lookupsApi.LookupGroupByDispatchCodeAsync(code.Code, _departmentId)).ConfigureAwait(false);
					if (group != null)
						code.ResolvedId = group.GroupId;
				}
				catch (Exception ex) when (!(ex is ResgridAuthenticationException))
				{
					_logger.Warning(ex, "Could not resolve group dispatch code {Code}; sending it as a group name", code.Code);
				}
			}
		}

		private async Task<T> WithRetriesAsync<T>(Func<Task<T>> action)
		{
			for (var attempt = 1; ; attempt++)
			{
				try
				{
					return await action().ConfigureAwait(false);
				}
				catch (Exception ex) when (attempt < 3 && IsTransient(ex))
				{
					_logger.Warning(ex, "Resgrid request failed (attempt {Attempt} of 3); retrying", attempt);
					await Task.Delay(TimeSpan.FromSeconds(2 * attempt)).ConfigureAwait(false);
				}
			}
		}

		private static bool IsTransient(Exception exception)
		{
			if (exception is HttpRequestException http)
			{
				if (http.StatusCode == null)
					return true;

				var status = (int)http.StatusCode.Value;
				return status == 408 || status == 409 || status == 429 || status >= 500;
			}

			return exception is TaskCanceledException || exception is ResgridAuthenticationException;
		}

		private (List<DispatchCode> DispatchCodes, List<string> WatcherNames) BuildDispatchContext(Watcher watcher)
		{
			var dispatchCodes = new List<DispatchCode>();
			var watcherNames = new List<string>();

			AddDispatchCode(dispatchCodes, watcherNames, watcher.Name, watcher.Code, watcher.Type);

			var additionalWatchers = watcher.GetAdditionalWatchers();
			if (additionalWatchers != null && additionalWatchers.Count > 0)
			{
				foreach (var additionalWatcher in additionalWatchers)
				{
					AddDispatchCode(dispatchCodes, watcherNames, additionalWatcher.Name, additionalWatcher.Code, additionalWatcher.Type);
				}
			}

			if (!String.IsNullOrWhiteSpace(watcher.AdditionalCodes))
			{
				foreach (var additionalCode in watcher.AdditionalCodes.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
				{
					AddDispatchCode(dispatchCodes, watcherNames, watcher.Name, additionalCode, (int)DispatchCodeType.Group);
				}
			}

			return (dispatchCodes, watcherNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
		}

		private static void AddDispatchCode(List<DispatchCode> dispatchCodes, List<string> watcherNames, string watcherName, string code, int watcherType)
		{
			if (!String.IsNullOrWhiteSpace(watcherName))
				watcherNames.Add(watcherName.Trim());

			if (String.IsNullOrWhiteSpace(code))
				return;

			if (dispatchCodes.Any(x => String.Equals(x.Code, code.Trim(), StringComparison.OrdinalIgnoreCase)))
				return;

			dispatchCodes.Add(new DispatchCode
			{
				Code = code.Trim(),
				Type = watcherType == (int)DispatchCodeType.Department ? DispatchCodeType.Department : DispatchCodeType.Group
			});
		}
	}
}
