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
		void Clear();
	}

	public class WatcherAudioStorage : IWatcherAudioStorage
	{
		private static Object _lock = new Object();
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
				lock (_lock)
				{
					if (initalAudio != null && initalAudio.Length > 0)
						_watcherAudio.Add(watcherId, new List<byte>(initalAudio));
					else
						_watcherAudio.Add(watcherId, new List<byte>());

					_logger.Debug($"Adding watcher data collection with Id of {watcherId}, total of {_watcherAudio.Count} watchers awaiting data.");
				}
			}
			else
			{
				_logger.Debug($"Trying to add watcher audio with Id of {watcherId} but it already exists, total of {_watcherAudio.Count} watchers awaiting data.");
			}
		}

		public void FinishWatcher(Guid watcherId)
		{
			if (_watcherAudio.ContainsKey(watcherId))
			{
				lock (_lock)
				{
					_watcherAudio.Remove(watcherId);
					_logger.Debug($"Removed watcher with Id of {watcherId} leaving {_watcherAudio.Count} watchers awaiting data.");
				}
			}
			else
			{
				_logger.Debug($"Unable to finish watcher with Id of {watcherId}.");
			}
		}

		public void AddAudio(Guid watcherId, byte[] audio)
		{
			if (_watcherAudio.ContainsKey(watcherId))
			{
				lock (_lock)
				{
					_watcherAudio[watcherId].AddRange(audio);
				}

				_logger.Debug($"Added watch audio for watcher with Id of {watcherId} with size of {GetNonZeroArrayLength(audio)}.");
			}
			else
			{
				_logger.Debug($"Unable to add watch audio for watcher with Id of {watcherId} with size of {GetNonZeroArrayLength(audio)}.");
			}
		}

		public byte[] GetAudio(Guid watcherId)
		{
			if (_watcherAudio.ContainsKey(watcherId))
			{
				lock (_lock)
				{
					return _watcherAudio[watcherId].ToArray();
				}
			}
			else
			{
				_logger.Debug($"Unable to get watcher audio with a watcher Id of {watcherId}.");
			}

			return null;
		}

		public void Clear()
		{
			lock (_lock)
			{
				_watcherAudio = new Dictionary<Guid, List<byte>>();
			}

			_logger.Debug($"Clearing all watcher audio.");
		}

		private int GetNonZeroArrayLength(byte[] array)
		{
			int count = 0;
			for (int i = 0; i < array.Length; i++)
			{
				if (array[i] > 0)
					count++;
			}
			return count;

		}
	}
}
