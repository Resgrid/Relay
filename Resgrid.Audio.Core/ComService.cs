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
using Serilog.Core;

namespace Resgrid.Audio.Core
{
	public class ComService
	{
		private readonly Logger _logger;
		private readonly AudioProcessor _audioProcessor;

		private Config _config;

		public event EventHandler<CallCreatedEventArgs> CallCreatedEvent;

		public ComService(Logger logger, AudioProcessor audioProcessor)
		{
			_logger = logger;
			_audioProcessor = audioProcessor;
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
				return HealthApi.IsHealthyAsync().GetAwaiter().GetResult();
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
			if (e.Mp3Audio != null && e.Mp3Audio.Length > 0)
			{
				try
				{
					var dispatchContext = BuildDispatchContext(e.Watcher);
					var watchersToned = String.Join(", ", dispatchContext.WatcherNames);
					var isDepartmentDispatch = dispatchContext.DispatchCodes.Any(x => x.Type == DispatchCodeType.Department);
					var dispatchList = DispatchListBuilder.Build(dispatchContext.DispatchCodes, _config?.DispatchMapping?.DepartmentDispatchPrefix);
					if (String.IsNullOrWhiteSpace(dispatchList))
						throw new InvalidOperationException($"No valid Resgrid dispatch targets were found for watcher '{e.Watcher.Name}'.");

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
						ExternalId = e.Watcher.Id.ToString(),
						ReferenceId = e.Watcher.TriggerFiredTimestamp.ToString("O"),
						Type = "Relay Audio"
					};

					var savedCallId = CallsApi.SaveCallAsync(callInput).GetAwaiter().GetResult();
					var userId = ResgridV4ApiClient.CurrentUserId;
					if (String.IsNullOrWhiteSpace(userId))
						throw new InvalidOperationException("The Resgrid access token did not contain a user id required to upload the dispatch audio.");

					CallsApi.SaveCallFileAsync(new SaveCallFileInput
					{
						CallId = savedCallId,
						UserId = userId,
						Type = (int)CallFileType.Audio,
						Name = $"Relay_{e.Watcher.TriggerFiredTimestamp:s}".Replace(":", "_") + ".mp3",
						Data = Convert.ToBase64String(e.Mp3Audio),
						Note = $"Captured dispatch audio for watcher {e.Watcher.Name}"
					}).GetAwaiter().GetResult();

					var savedCall = CallsApi.GetCallAsync(savedCallId).GetAwaiter().GetResult();
					CallCreatedEvent?.Invoke(
						this,
						new CallCreatedEventArgs(e.Watcher.Name, savedCallId, savedCall?.Data?.Number, DateTime.Now));
				}
				catch (HttpRequestException ex)
				{
					_logger.Error(ex.ToString());
				}
				catch (InvalidOperationException ex)
				{
					_logger.Error(ex.ToString());
				}
				catch (TaskCanceledException ex)
				{
					_logger.Error(ex.ToString());
				}
			}
			else
			{
				_logger.Warning($"No Dispatch Audio detected, unable to save call for {e.Watcher.Name}");
			}
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
