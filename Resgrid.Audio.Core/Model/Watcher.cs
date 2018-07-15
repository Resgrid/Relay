using System;
using System.Collections.Generic;
using System.Linq;
using DtmfDetection;

namespace Resgrid.Audio.Core.Model
{
	public class Watcher
	{
		private const int BUFFER_SIZE = 10560000;

		private Guid _id;
		private Queue<byte> _buffer;
		private Trigger _trigger;
		private int _audioCount;
		private List<string> _additionalCodes;

		public string Name { get; set; }
		public bool Active { get; set; }
		public string Code { get; set; }
		public int Type { get; set; } // 1 Department, 2 Group
		public int Eval { get; set; }
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

		public bool AddAudio(byte[] audio)
		{
			if (_buffer == null)
				_buffer = new Queue<byte>(BUFFER_SIZE);

			foreach (var b in audio)
			{
				if (IsQueueFull())
					return false;

				_buffer.Enqueue(b);
				_audioCount++;
			}

			return true;
		}

		public bool IsQueueFull()
		{
			return _audioCount >= BUFFER_SIZE;
		}

		public Trigger GetTrigger()
		{
			return _trigger;
		}

		public byte[] GetBuffer()
		{
			return _buffer.ToArray();
		}

		public void AddAdditionalCode(string code)
		{
			if (_additionalCodes == null)
				_additionalCodes = new List<string>();

			if (!_additionalCodes.Contains(code))
				_additionalCodes.Add(code);
		}

		public List<string> GetAdditionalCodes()
		{
			return _additionalCodes;
		}

		public List<Tuple<Trigger, List<DtmfTone>>> DidTriggerProcess(List<DtmfTone> tones)
		{
			List<Tuple<Trigger, List<DtmfTone>>> triggers = new List<Tuple<Trigger, List<DtmfTone>>>();

			if (tones != null && tones.Any())
			{
				if (Triggers != null && Triggers.Any())
				{
					foreach (var trigger in Triggers)
					{
						List<DtmfTone> matchedTones = trigger.GetMatchingTones(tones);
						if (matchedTones != null && matchedTones.Any())
							triggers.Add(new Tuple<Trigger, List<DtmfTone>>(trigger, matchedTones));
					}
				}
			}

			return triggers;
		}
	}
}
