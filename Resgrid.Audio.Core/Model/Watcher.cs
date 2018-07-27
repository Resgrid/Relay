using System;
using System.Collections.Generic;
using System.Linq;
using DtmfDetection;
using DtmfDetection.NAudio;

namespace Resgrid.Audio.Core.Model
{
	public class Watcher
	{
		private Guid _id;
		private byte[] _buffer;
		private Trigger _trigger;
		private int _audioCount;
		private List<Watcher> _additionalWatchers;

		public string Name { get; set; }
		public bool Active { get; set; }
		public string Code { get; set; }
		public int Type { get; set; } // 1 Department, 2 Group
		public List<Trigger> Triggers { get; set; }

		[Newtonsoft.Json.JsonIgnore]
		public DateTime TriggerFiredTimestamp { get; set; }

		[Newtonsoft.Json.JsonIgnore]
		public DateTime LastCheckedTimestamp { get; set; }

		public Guid Id
		{
			get
			{
				if (_id == null)
					_id = Guid.NewGuid();

				return _id;
			}
			set { _id = value; }
		}

		public void SetFiredTrigger(Trigger trigger)
		{
			if (_trigger == null && trigger != null)
				_trigger = trigger;
		}

		public void AddAudio(byte[] audio)
		{
			_buffer = audio;
		}

		public Trigger GetTrigger()
		{
			return _trigger;
		}

		public byte[] GetBuffer()
		{
			return _buffer;
		}

		public void AddAdditionalWatcher(Watcher watcher)
		{
			if (_additionalWatchers == null)
				_additionalWatchers = new List<Watcher>();

			if (!_additionalWatchers.Contains(watcher))
				_additionalWatchers.Add(watcher);
		}

		public List<Watcher> GetAdditionalWatchers()
		{
			return _additionalWatchers;
		}

		public List<Tuple<Trigger, List<DtmfToneEnd>>> DidTriggerProcess(List<DtmfToneEnd> tones)
		{
			List<Tuple<Trigger, List<DtmfToneEnd>>> triggers = new List<Tuple<Trigger, List<DtmfToneEnd>>>();

			if (tones != null && tones.Any())
			{
				if (Triggers != null && Triggers.Any())
				{
					foreach (var trigger in Triggers)
					{
						List<DtmfToneEnd> matchedTones = trigger.GetMatchingTones(tones);
						if (matchedTones != null && matchedTones.Any())
							triggers.Add(new Tuple<Trigger, List<DtmfToneEnd>>(trigger, matchedTones));
					}
				}
			}

			return triggers;
		}
	}
}
