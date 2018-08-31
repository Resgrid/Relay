using System;
using System.Collections.Generic;

namespace Resgrid.Audio.Core
{
	public interface IWatcherAudioStorage
	{
		void StartNewAudio(Guid watcherId, byte[] initalAudio);
		void FinishWatcher(Guid watcherId);
		void AddAudio(Guid watcherId, byte[] audio);
		byte[] GetAudio(Guid watcherId);
	}

	public class WatcherAudioStorage : IWatcherAudioStorage
	{
		private static Dictionary<Guid, List<byte>> _watcherAudio;

		public WatcherAudioStorage()
		{
			_watcherAudio = new Dictionary<Guid, List<byte>>();
		}

		public void StartNewAudio(Guid watcherId, byte[] initalAudio)
		{
			if (!_watcherAudio.ContainsKey(watcherId))
				_watcherAudio.Add(watcherId, new List<byte>(initalAudio));
		}

		public void FinishWatcher(Guid watcherId)
		{
			if (_watcherAudio.ContainsKey(watcherId))
				_watcherAudio.Remove(watcherId);
		}

		public void AddAudio(Guid watcherId, byte[] audio)
		{
			if (_watcherAudio.ContainsKey(watcherId))
			{
				_watcherAudio[watcherId].AddRange(audio);
			}
		}

		public byte[] GetAudio(Guid watcherId)
		{
			if (_watcherAudio.ContainsKey(watcherId))
			{
				_watcherAudio[watcherId].ToArray();
			}

			return null;
		}
	}
}
