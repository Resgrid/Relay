using System;
using System.Collections.Generic;
using Serilog.Core;

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
		private readonly Logger _logger;
		private static Dictionary<Guid, List<byte>> _watcherAudio;

		public WatcherAudioStorage(Logger logger)
		{
			_logger = logger;
			_watcherAudio = new Dictionary<Guid, List<byte>>();
		}

		public void StartNewAudio(Guid watcherId, byte[] initalAudio)
		{
			if (!_watcherAudio.ContainsKey(watcherId))
			{
				if (initalAudio != null && initalAudio.Length > 0)
					_watcherAudio.Add(watcherId, new List<byte>(initalAudio));
				else
					_watcherAudio.Add(watcherId, new List<byte>());

				_logger.Information($"Adding watcher data collection with Id of {watcherId}, total of {_watcherAudio.Count} watchers awaiting data.");
			}
		}

		public void FinishWatcher(Guid watcherId)
		{
			if (_watcherAudio.ContainsKey(watcherId))
			{
				_watcherAudio.Remove(watcherId);
				_logger.Information($"Removed watcher with Id of {watcherId} leaving {_watcherAudio.Count} watchers awaiting data.");
			}
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
				return _watcherAudio[watcherId].ToArray();
			}

			return null;
		}
	}
}
