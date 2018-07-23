using System;
using System.Collections.Generic;
using System.Text;
using Resgrid.Audio.Core.Model;
using Resgrid.Providers.ApiClient.V3;
using Resgrid.Providers.ApiClient.V3.Models;

namespace Resgrid.Audio.Core
{
	public class ComService
	{
		private AudioProcessor _audioProcessor;
		private Config _config;

		public ComService(AudioProcessor audioProcessor)
		{
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
			Call newCall = new Call();
			newCall.Name = $"Audio Import {DateTime.Now.ToString("g")}";
			newCall.Priority = (int)CallPriority.Medium;
			newCall.NatureOfCall = $"Audio import from a radio dispatch. Listen to attached audio for call information.";
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
					watchersToned.Append($"{code.Name} ");
					newCall.GroupCodesToDispatch.Add(code.Code);

					if (code.Type == 1)
						newCall.AllCall = true;
				}
			}

			newCall.Notes = $"Audio import from a radio dispatch. Listen to attached audio for call information. Call was created on {DateTime.Now.ToString("F")}. Was an AllCall: {newCall.AllCall}. Watchers Toned: {watchersToned}";

			if (e.Watcher.Type == 1)
				newCall.AllCall = true;

			newCall.Attachments = new List<CallAttachment>();
			newCall.Attachments.Add(new CallAttachment()
			{
				CallAttachmentType = (int)CallAttachmentTypes.DispatchAudio,
				FileName = $"Relay_{DateTime.Now.ToString("s")}",
				Timestamp = DateTime.UtcNow,
				Data = e.Watcher.GetBuffer()
			});

			var savedCall = CallsApi.AddNewCall(newCall).Result;
		}
	}
}
