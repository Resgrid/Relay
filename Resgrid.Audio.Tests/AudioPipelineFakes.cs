using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DtmfDetection;
using NAudio.Wave;
using Resgrid.Audio.Core;
using Resgrid.Audio.Core.Events;
using Resgrid.Audio.Core.Model;
using Resgrid.Providers.ApiClient.V4;
using Resgrid.Providers.ApiClient.V4.Models;

namespace Resgrid.Audio.Tests
{
	/// <summary>Test doubles for the tone-detect pipeline (AudioProcessor / ComService) that need no audio device.</summary>
	internal static class AudioPipelineFakes
	{
		/// <summary>Raises triggers on demand and records which watchers are marked active.</summary>
		internal sealed class Evaluator : IAudioEvaluator
		{
			private readonly object _sync = new object();
			private readonly List<Guid> _active = new List<Guid>();

			public event EventHandler<EvaluatorEventArgs> EvaluatorStarted { add { } remove { } }
			public event EventHandler<EvaluatorEventArgs> EvaluatorFinished { add { } remove { } }
			public event EventHandler<WatcherEventArgs> WatcherTriggered;

			/// <summary>Runs after a watcher is released, on the releasing thread.</summary>
			public Action<Guid> OnRemoveActiveWatcher { get; set; }

			public List<Guid> Active
			{
				get
				{
					lock (_sync)
						return new List<Guid>(_active);
				}
			}

			public void Trigger(Watcher watcher, Trigger trigger, DateTime timestamp) =>
				WatcherTriggered?.Invoke(this, new WatcherEventArgs(watcher, new List<Trigger> { trigger }, new List<DtmfTone>(), timestamp));

			public void Init(Config config) { }
			public void Start(IWaveIn waveIn) { }
			public void ClearTones() { }
			public int CleanUpTones() => 0;
			public void Stop() { }

			public void AddActiveWatcher(Guid watcherId)
			{
				lock (_sync)
				{
					if (!_active.Contains(watcherId))
						_active.Add(watcherId);
				}
			}

			public void RemoveActiveWatcher(Guid watcherId)
			{
				lock (_sync)
					_active.Remove(watcherId);

				OnRemoveActiveWatcher?.Invoke(watcherId);
			}
		}

		/// <summary>Hands the processor's sample aggregator to the test and "encodes" without MediaFoundation.</summary>
		internal sealed class Recorder : IAudioRecorder
		{
			private readonly IWatcherAudioStorage _storage;

			public Recorder(IWatcherAudioStorage storage)
			{
				_storage = storage;
			}

			public Exception SaveError { get; set; }
			public byte[] Mp3 { get; set; } = { 0x49, 0x44, 0x33 };

			public SampleAggregator SampleAggregator { get; private set; }
			public RecordingState RecordingState => RecordingState.Monitoring;
			public event EventHandler Stopped { add { } remove { } }
			public WaveFormat RecordingFormat { get; set; }
			public TimeSpan RecordedTime => TimeSpan.Zero;

			public void BeginMonitoring(int recordingDevice) { }
			public void BeginRecording(string path) { }
			public void Stop() { }
			public void SetSampleAggregator(SampleAggregator sampleAggregator) => SampleAggregator = sampleAggregator;

			public byte[] SaveWatcherAudio(Watcher watcher)
			{
				_storage.GetAudio(watcher.Id);
				if (SaveError != null)
					throw SaveError;

				return Mp3;
			}
		}

		internal sealed class ApiClient : IResgridApiClient
		{
			public string CurrentUserId => "relay-user";
			public bool UsesSystemApiKey => false;
			public bool IsSystemCaller => false;
			public string CredentialScope => "test";

			public Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
			public Task<T> GetAsync<T>(string url, CancellationToken cancellationToken = default) where T : class => throw new NotSupportedException();
			public Task<T> PostAsync<T>(string url, object data, CancellationToken cancellationToken = default) where T : class => throw new NotSupportedException();
			public Task<T> PostAsync<T>(string url, object data, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken = default) where T : class =>
				throw new NotSupportedException();
		}

		internal sealed class CallsApi : IResgridCallsApi
		{
			public List<NewCallInput> SavedCalls { get; } = new List<NewCallInput>();

			public Task<string> SaveCallAsync(NewCallInput call, CancellationToken cancellationToken = default) => SaveCallAsync(call, null, cancellationToken);

			public Task<string> SaveCallAsync(NewCallInput call, string idempotencyKey, CancellationToken cancellationToken = default)
			{
				lock (SavedCalls)
					SavedCalls.Add(call);
				return Task.FromResult("call-1");
			}

			public Task<GetCallResult> GetCallAsync(string callId, string departmentId = null, CancellationToken cancellationToken = default) =>
				Task.FromResult(new GetCallResult { Data = new CallResultData { CallId = callId, Number = "26-1" } });

			public Task<string> SaveCallFileAsync(SaveCallFileInput file, CancellationToken cancellationToken = default) => Task.FromResult("file-1");
			public Task<string> SaveCallFileAsync(SaveCallFileInput file, string idempotencyKey, CancellationToken cancellationToken = default) => Task.FromResult("file-1");
			public Task<List<CallResultData>> GetActiveCallsAsync(string departmentId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		}

		/// <summary>Answers group lookups from <see cref="GroupLookup"/>, failing the first <see cref="FailuresBeforeAnswer"/> attempts.</summary>
		internal sealed class LookupsApi : IResgridLookupsApi
		{
			private int _attempts;

			public int FailuresBeforeAnswer { get; set; }
			public Func<Exception> Failure { get; set; } = () => new HttpRequestException("lookup failed");
			public Func<string, GroupLookupResult> GroupLookup { get; set; } = _ => null;
			public int Attempts => Volatile.Read(ref _attempts);

			public Task<GroupLookupResult> LookupGroupByDispatchCodeAsync(string code, string departmentId, CancellationToken cancellationToken = default)
			{
				if (Interlocked.Increment(ref _attempts) <= FailuresBeforeAnswer)
					throw Failure();

				return Task.FromResult(GroupLookup(code));
			}

			public Task<DepartmentLookupResult> LookupDepartmentByDispatchCodeAsync(string code, CancellationToken cancellationToken = default) => throw new NotSupportedException();
			public Task<GroupLookupResult> LookupGroupByMessageCodeAsync(string code, string departmentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
			public Task<UnitLookupResult> LookupUnitByNameAsync(string name, string departmentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
			public Task<RoleLookupResult> LookupRoleByNameAsync(string name, string departmentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		}
	}
}
