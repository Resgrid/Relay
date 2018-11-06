using System;
using System.Collections.Generic;
using System.Text;
using Resgrid.Audio.Core.Events;
using Resgrid.Audio.Core.Model;
using Resgrid.Providers.ApiClient;
using Resgrid.Providers.ApiClient.V3;
using Resgrid.Providers.ApiClient.V3.Models;
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
				var result = HealthApi.GetApiHealth().Result;

				if (result != null && result.DatabaseOnline)
					return true;
			}
			catch (Exception e)
			{
				return false;
			}

			return false;
		}

		private void _audioProcessor_TriggerProcessingFinished(object sender, Events.TriggerProcessedEventArgs e)
		{
			if (e.Mp3Audio != null && e.Mp3Audio.Length > 0)
			{
				Call newCall = new Call();
				newCall.Priority = (int)CallPriority.Medium;
				StringBuilder watchersToned = new StringBuilder();

				watchersToned.Append($"{e.Watcher.Name} ");

				newCall.GroupCodesToDispatch = new List<string>();
				newCall.GroupCodesToDispatch.Add(e.Watcher.Code);
				newCall.CallSource = 3;
				newCall.SourceIdentifier = e.Watcher.Id.ToString();

				var additionalCodes = e.Watcher.GetAdditionalWatchers();
				if (additionalCodes != null && additionalCodes.Count > 0)
				{
					foreach (var code in additionalCodes)
					{
						if (!newCall.GroupCodesToDispatch.Contains(code.Code))
						{
							watchersToned.Append($"{code.Name} ");
							newCall.GroupCodesToDispatch.Add(code.Code);

							if (code.Type == 1)
								newCall.AllCall = true;
						}
					}
				}

				// Run through the additional comma seperated group codes and add them.
				if (!string.IsNullOrWhiteSpace(e.Watcher.AdditionalCodes))
				{
					var newCodes = e.Watcher.AdditionalCodes.Split(char.Parse(","));

					if (newCodes != null && newCodes.Length > 0)
					{
						foreach (var code in newCodes)
						{
							if (!newCall.GroupCodesToDispatch.Contains(code))
							{
								newCall.GroupCodesToDispatch.Add(code);
							}
						}
					}
				}

				newCall.NatureOfCall =
					$"Audio import from a radio dispatch. Listen to attached audio for call information. Watchers Toned: {watchersToned}";
				newCall.Notes =
					$"Audio import from a radio dispatch. Listen to attached audio for call information. Call was created on {DateTime.Now.ToString("F")}. Was an AllCall: {newCall.AllCall}. Watchers Toned: {watchersToned}";

				if (e.Watcher.Type == 1)
					newCall.AllCall = true;

				if (newCall.AllCall)
					newCall.Name = $"ALLCALL Audio Import {DateTime.Now.ToString("g")}";
				else
					newCall.Name = $"Audio Import {DateTime.Now.ToString("g")}";

				newCall.Attachments = new List<CallAttachment>();
				newCall.Attachments.Add(new CallAttachment()
				{
					CallAttachmentType = (int)CallAttachmentTypes.DispatchAudio,
					FileName = $"Relay_{e.Watcher.TriggerFiredTimestamp.ToString("s").Replace(":", "_")}.mp3",
					Timestamp = DateTime.UtcNow,
					Data = e.Mp3Audio
				});

				try
				{
					var savedCall = AsyncHelpers.RunSync<Call>(() => CallsApi.AddNewCall(newCall));

					if (savedCall != null)
						CallCreatedEvent?.Invoke(this,
							new CallCreatedEventArgs(e.Watcher.Name, savedCall.CallId, savedCall.Number, DateTime.Now));
				}
				catch (Exception ex)
				{
					_logger.Error(ex.ToString());
				}
				finally
				{
					newCall = null;
				}
			}
			else
			{
				_logger.Warning($"No Dispatch Audio detected, unable to save call for {e.Watcher.Name}");
			}
		}
	}
}
