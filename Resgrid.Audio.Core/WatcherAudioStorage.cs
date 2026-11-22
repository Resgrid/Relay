using System;
using System.Collections.Generic;
using Serilog;

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

	/// <summary>
	/// Per-watcher audio buffers for the tone-detect pipeline. Written from the capture thread and read/cleared from the
	/// processing timer, so every access (including the existence checks) is under one lock. Per instance: the storage was
	/// static, so a pipeline restarted in the same process (the desktop app) shared and wiped the previous one's buffers.
	/// </summary>
	public class WatcherAudioStorage : IWatcherAudioStorage
	{
		private readonly object _lock = new object();
		private readonly ILogger _logger;
		private Dictionary<Guid, List<byte>> _watcherAudio = new Dictionary<Guid, List<byte>>();

		public WatcherAudioStorage(ILogger logger)
		{
			_logger = logger;
		}

		public void StartNewAudio(Guid watcherId, byte[] initalAudio)
		{
			lock (_lock)
			{
				if (_watcherAudio.ContainsKey(watcherId))
				{
					_logger?.Debug("Watcher audio for {WatcherId} already exists; {Count} watchers awaiting data", watcherId, _watcherAudio.Count);
					return;
				}

				_watcherAudio.Add(watcherId, initalAudio != null && initalAudio.Length > 0 ? new List<byte>(initalAudio) : new List<byte>());
				_logger?.Debug("Collecting audio for watcher {WatcherId}; {Count} watchers awaiting data", watcherId, _watcherAudio.Count);
			}
		}

		public void FinishWatcher(Guid watcherId)
		{
			lock (_lock)
			{
				if (_watcherAudio.Remove(watcherId))
					_logger?.Debug("Finished watcher {WatcherId}; {Count} watchers awaiting data", watcherId, _watcherAudio.Count);
				else
					_logger?.Debug("Unable to finish watcher {WatcherId}: no audio collected", watcherId);
			}
		}

		public void AddAudio(Guid watcherId, byte[] audio)
		{
			if (audio == null || audio.Length == 0)
				return;

			// Called for every captured sample while a watcher records, so no per-call logging.
			lock (_lock)
			{
				if (_watcherAudio.TryGetValue(watcherId, out var buffer))
					buffer.AddRange(audio);
			}
		}

		public byte[] GetAudio(Guid watcherId)
		{
			lock (_lock)
			{
				if (_watcherAudio.TryGetValue(watcherId, out var buffer))
					return buffer.ToArray();
			}

			_logger?.Debug("No watcher audio for {WatcherId}", watcherId);
			return null;
		}

		public void Clear()
		{
			lock (_lock)
			{
				_watcherAudio = new Dictionary<Guid, List<byte>>();
			}

			_logger?.Debug("Cleared all watcher audio");
		}
	}
}
