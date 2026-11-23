using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Resgrid.Audio.Core;
using Resgrid.Audio.Core.Model;

namespace Resgrid.Audio.Tests
{
	[TestFixture]
	public class AudioProcessorTests
	{
		private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

		private WatcherAudioStorage _storage;
		private AudioPipelineFakes.Recorder _recorder;
		private AudioPipelineFakes.Evaluator _evaluator;
		private AudioProcessor _processor;

		[SetUp]
		public void SetUp()
		{
			_storage = new WatcherAudioStorage(null);
			_recorder = new AudioPipelineFakes.Recorder(_storage);
			_evaluator = new AudioPipelineFakes.Evaluator();
			_processor = new AudioProcessor(_recorder, _evaluator, _storage);
		}

		[TearDown]
		public void TearDown()
		{
			_processor.Stop();
		}

		private static Watcher NewWatcher(string name) => new Watcher { Name = name, Active = true, Code = name, Type = 2 };

		private static Trigger NewTrigger() => new Trigger { Count = 1, Frequency1 = 500, Time1 = 500 };

		/// <summary>A trigger fired long enough ago that the next timer tick finishes it.</summary>
		private static DateTime AlreadyDue => DateTime.UtcNow.AddMinutes(-5);

		[Test]
		public void A_Failed_Encode_Should_Still_Release_The_Recording_Buffer()
		{
			var watcher = NewWatcher("station5");
			_recorder.SaveError = new InvalidOperationException("encoder failed");
			_processor.Init(new Config { AudioLength = 60, Watchers = new List<Watcher> { watcher } });

			_evaluator.Trigger(watcher, NewTrigger(), DateTime.UtcNow);
			_recorder.SampleAggregator.OnDataAvailable(new byte[] { 1, 2, 3, 4 }, 4);
			_storage.GetAudio(watcher.Id).Should().Equal(new byte[] { 1, 2, 3, 4 });

			_processor.Stop();

			// Left behind, StartNewAudio kept this buffer and its audio went out again with the watcher's next dispatch.
			_storage.GetAudio(watcher.Id).Should().BeNull();
			_evaluator.Active.Should().BeEmpty();
		}

		[Test]
		public void Finishing_A_Dispatch_Should_Not_Clear_A_New_Page_On_The_Same_Watcher()
		{
			var primary = NewWatcher("station5");
			var rider = NewWatcher("station6");
			var newTrigger = NewTrigger();
			var released = new ManualResetEventSlim();
			var pagedAgain = 0;

			// The detector pages the primary again the moment it is released, and a second watcher rides along on that new
			// recording (Multiple=false), before the finished dispatch's cleanup has returned.
			_evaluator.OnRemoveActiveWatcher = id =>
			{
				if (id != primary.Id || Interlocked.Exchange(ref pagedAgain, 1) == 1)
					return;

				_evaluator.Trigger(primary, newTrigger, DateTime.UtcNow);
				_evaluator.Trigger(rider, NewTrigger(), DateTime.UtcNow);
				released.Set();
			};

			_processor.Init(new Config { AudioLength = 60, Multiple = false, Watchers = new List<Watcher> { primary, rider } });
			_evaluator.Trigger(primary, NewTrigger(), AlreadyDue);

			released.Wait(WaitLimit).Should().BeTrue("the timer finishes the due dispatch");
			_processor.WaitForInFlightDispatchAsync().Wait(WaitLimit).Should().BeTrue();

			primary.GetTrigger().Should().BeSameAs(newTrigger);
			primary.GetAdditionalWatchers().Should().Contain(rider);
			_evaluator.Active.Should().Contain(new[] { primary.Id, rider.Id }, "the rider is released only when the new dispatch finishes");
		}

		[Test]
		public void Stop_Should_Wait_For_The_Dispatch_In_Flight()
		{
			var watcher = NewWatcher("station5");
			var sending = new ManualResetEventSlim();
			var gate = new ManualResetEventSlim();
			_processor.TriggerProcessingFinished += (_, _) =>
			{
				sending.Set();
				gate.Wait(WaitLimit);
			};

			_processor.Init(new Config { AudioLength = 60, Watchers = new List<Watcher> { watcher } });
			_evaluator.Trigger(watcher, NewTrigger(), AlreadyDue);
			sending.Wait(WaitLimit).Should().BeTrue("the timer starts the due dispatch");

			var inFlight = _processor.WaitForInFlightDispatchAsync();
			var stop = Task.Run(() => _processor.Stop());

			stop.Wait(TimeSpan.FromMilliseconds(300)).Should().BeFalse("Stop waits for the dispatch being sent");
			inFlight.IsCompleted.Should().BeFalse();

			gate.Set();

			stop.Wait(WaitLimit).Should().BeTrue();
			inFlight.Wait(WaitLimit).Should().BeTrue();
			_processor.WaitForInFlightDispatchAsync().IsCompleted.Should().BeTrue();
		}

		[Test]
		public void A_Page_After_Stop_Should_Be_Ignored()
		{
			var watcher = NewWatcher("station5");
			_processor.Init(new Config { AudioLength = 60, Watchers = new List<Watcher> { watcher } });
			_processor.Stop();

			_evaluator.Trigger(watcher, NewTrigger(), DateTime.UtcNow);

			// Nothing would ever finish it, and it would stay marked active in the evaluator.
			_evaluator.Active.Should().BeEmpty();
			_storage.GetAudio(watcher.Id).Should().BeNull();
		}
	}
}
