using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Resgrid.Audio.Core.Radio;
using Resgrid.Audio.Voice.Abstractions;
using Resgrid.Relay.Engine.Voice;

namespace Resgrid.Audio.Tests
{
	/// <summary><see cref="RadioMode.RunTransmitTestAsync(IRadioDevice, IPttController, RadioSettings, Serilog.ILogger, CancellationToken, int)"/>.</summary>
	[TestFixture]
	public class RadioTransmitTestTests
	{
		private const int SamplesPerMs = AudioFormat.SampleRate / 1000;

		private static RadioSettings Settings(int leadMs = 0) => new RadioSettings
		{
			Ptt = PttKeyingMethod.SerialRts,
			TxLeadMs = leadMs,
			TxTailMs = 0
		};

		[Test]
		public async Task Completes_KeysOnce_PlaysLeadAndTone_ThenReleases()
		{
			var device = new TxTestDevice();
			var ptt = new TxTestPtt();

			await RadioMode.RunTransmitTestAsync(device, ptt, Settings(leadMs: 300), null, CancellationToken.None, toneMs: 300);

			ptt.KeyCalls.Should().Be(1);
			ptt.IsKeyed.Should().BeFalse();
			device.Stopped.Should().BeTrue();
			var samples = device.Writes.SelectMany(w => w).ToArray();
			samples.Should().HaveCount(600 * SamplesPerMs, "the whole lead and tone reach the device");
			samples.Take(300 * SamplesPerMs).Should().OnlyContain(s => s == 0, "the key-up lead is silence");
			samples.Skip(300 * SamplesPerMs).Should().Contain(s => s != 0);
		}

		[Test]
		public async Task FeedsTheToneInBoundedChunks_NeverFarAheadOfPlayback()
		{
			// A device that can't report its backlog (Zero) must still not be handed seconds of audio at once: the NAudio
			// device buffers 5 s and drops the overflow.
			var device = new TxTestDevice();
			var ptt = new TxTestPtt();

			await RadioMode.RunTransmitTestAsync(device, ptt, Settings(leadMs: 500), null, CancellationToken.None, toneMs: 1500);

			device.Writes.Should().OnlyContain(w => w.Length <= 100 * SamplesPerMs);
			device.MaxAheadMs.Should().BeLessThan(1300, "at most about a second is queued ahead of playback");
			device.Writes.Sum(w => w.Length).Should().Be(2000 * SamplesPerMs);
		}

		[Test]
		public async Task CancelledBeforeStart_NeverOpensOrKeys()
		{
			var device = new TxTestDevice();
			var ptt = new TxTestPtt();
			using var cts = new CancellationTokenSource();
			cts.Cancel();

			Func<Task> run = () => RadioMode.RunTransmitTestAsync(device, ptt, Settings(), null, cts.Token, toneMs: 300);

			await run.Should().ThrowAsync<OperationCanceledException>();
			ptt.KeyCalls.Should().Be(0);
			device.Started.Should().BeFalse();
		}

		[Test]
		public async Task CancelledWhileTheDeviceOpens_NeverKeys()
		{
			using var cts = new CancellationTokenSource();
			var device = new TxTestDevice { OnStartTransmit = () => cts.Cancel() };
			var ptt = new TxTestPtt();

			Func<Task> run = () => RadioMode.RunTransmitTestAsync(device, ptt, Settings(), null, cts.Token, toneMs: 300);

			await run.Should().ThrowAsync<OperationCanceledException>();
			ptt.KeyCalls.Should().Be(0, "a cancel that arrived before keying must not put the radio on the air");
			device.Stopped.Should().BeTrue();
		}

		[Test]
		public async Task CancelledAfterKeying_ReleasesPttAndReportsCancellation()
		{
			using var cts = new CancellationTokenSource();
			var device = new TxTestDevice();
			var ptt = new TxTestPtt { OnKey = () => cts.CancelAfter(50) };

			Func<Task> run = () => RadioMode.RunTransmitTestAsync(device, ptt, Settings(), null, cts.Token, toneMs: 3000);

			await run.Should().ThrowAsync<OperationCanceledException>();
			ptt.IsKeyed.Should().BeFalse();
			device.Stopped.Should().BeTrue();
		}

		[Test]
		public async Task UnkeyThatNeverSucceeds_ThrowsPttReleaseException_AfterDeviceCleanup()
		{
			var device = new TxTestDevice();
			var ptt = new TxTestPtt { FailUnkey = true };

			Func<Task> run = () => RadioMode.RunTransmitTestAsync(device, ptt, Settings(), null, CancellationToken.None, toneMs: 200);

			await run.Should().ThrowAsync<PttReleaseException>().WithMessage("*could not be released*");
			ptt.UnkeyCalls.Should().BeGreaterThan(1, "a failed unkey is retried");
			device.Stopped.Should().BeTrue("device cleanup is unconditional");
		}

		[Test]
		public async Task CancelledWithAFailedRelease_IsReportedAsTheReleaseFailure_NotACancellation()
		{
			using var cts = new CancellationTokenSource();
			var device = new TxTestDevice();
			var ptt = new TxTestPtt { FailUnkey = true, OnKey = () => cts.CancelAfter(50) };

			Func<Task> run = () => RadioMode.RunTransmitTestAsync(device, ptt, Settings(), null, cts.Token, toneMs: 3000);

			var thrown = await run.Should().ThrowAsync<PttReleaseException>();
			thrown.Which.InnerException.Should().BeAssignableTo<OperationCanceledException>();
		}

		[Test]
		public async Task KeyFailure_IsReported_WithoutAReleaseFailure()
		{
			var device = new TxTestDevice();
			var ptt = new TxTestPtt { FailKey = true, FailUnkey = true };

			Func<Task> run = () => RadioMode.RunTransmitTestAsync(device, ptt, Settings(), null, CancellationToken.None, toneMs: 200);

			(await run.Should().ThrowAsync<InvalidOperationException>()).Which.Should().NotBeOfType<PttReleaseException>(
				"a line that was never keyed is not left transmitting");
			device.Stopped.Should().BeTrue();
		}

		[Test]
		public async Task DeviceFaultMidTone_FailsTheTest_AfterReleasingPtt()
		{
			var device = new TxTestDevice();
			var ptt = new TxTestPtt();
			device.OnWrite = count => { if (count == 3) device.Fail("Radio transmit audio stopped unexpectedly"); };

			Func<Task> run = () => RadioMode.RunTransmitTestAsync(device, ptt, Settings(), null, CancellationToken.None, toneMs: 3000);

			await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("*stopped unexpectedly*interrupted*");
			ptt.IsKeyed.Should().BeFalse();
			device.Stopped.Should().BeTrue();
			device.FaultSubscribers.Should().Be(0);
		}

		[Test]
		public async Task OutputThatStopsConsuming_FailsInsteadOfReportingSuccess()
		{
			var device = new TxTestDevice { BufferedTransmitOverride = TimeSpan.FromSeconds(2) };
			var ptt = new TxTestPtt();

			Func<Task> run = () => RadioMode.RunTransmitTestAsync(device, ptt, Settings(), null, CancellationToken.None, toneMs: 200);

			await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("*stopped playing*");
			ptt.IsKeyed.Should().BeFalse();
		}

		private sealed class TxTestDevice : IRadioDevice
		{
			private readonly object _lock = new object();
			private readonly List<short[]> _writes = new List<short[]>();
			private DateTime _firstWrite;
			private long _sentSamples;
			private EventHandler<RadioFaultEventArgs> _faulted;

			public event EventHandler<short[]> SamplesReceived { add { } remove { } }

			public event EventHandler<RadioFaultEventArgs> Faulted
			{
				add { lock (_lock) { _faulted += value; FaultSubscribers++; } }
				remove { lock (_lock) { _faulted -= value; FaultSubscribers--; } }
			}

			public int FaultSubscribers { get; private set; }
			public bool Started { get; private set; }
			public bool Stopped { get; private set; }
			public Action OnStartTransmit { get; set; }
			public Action<int> OnWrite { get; set; }
			public TimeSpan? BufferedTransmitOverride { get; set; }

			/// <summary>The most audio ever handed over beyond what real-time playback since the first write had consumed.</summary>
			public double MaxAheadMs { get; private set; }

			public List<short[]> Writes
			{
				get { lock (_lock) return _writes.ToList(); }
			}

			public TimeSpan BufferedTransmit => BufferedTransmitOverride ?? TimeSpan.Zero;
			public TimeSpan PlayedTransmit => TimeSpan.Zero;
			public TimeSpan OutputLatency => TimeSpan.Zero;

			public void ClearTransmit() { }

			public void StartReceive() { }
			public void StopReceive() { }

			public void StartTransmit()
			{
				Started = true;
				OnStartTransmit?.Invoke();
			}

			public void StopTransmit() => Stopped = true;

			public void Transmit(short[] pcm48kMono)
			{
				int count;
				lock (_lock)
				{
					var now = DateTime.UtcNow;
					if (_writes.Count == 0)
						_firstWrite = now;
					var playedMs = (now - _firstWrite).TotalMilliseconds;
					_sentSamples += pcm48kMono.Length;
					MaxAheadMs = Math.Max(MaxAheadMs, (double)_sentSamples / SamplesPerMs - playedMs);
					_writes.Add((short[])pcm48kMono.Clone());
					count = _writes.Count;
				}
				OnWrite?.Invoke(count);
			}

			public void Fail(string reason)
			{
				EventHandler<RadioFaultEventArgs> handler;
				lock (_lock) handler = _faulted;
				handler?.Invoke(this, new RadioFaultEventArgs(reason));
			}

			public void Dispose() { }
		}

		private sealed class TxTestPtt : IPttController
		{
			private int _keyCalls;
			private int _unkeyCalls;

			public bool IsKeyed { get; private set; }
			public bool FailKey { get; set; }
			public bool FailUnkey { get; set; }
			public Action OnKey { get; set; }
			public int KeyCalls => Volatile.Read(ref _keyCalls);
			public int UnkeyCalls => Volatile.Read(ref _unkeyCalls);

			public bool Key()
			{
				Interlocked.Increment(ref _keyCalls);
				if (FailKey)
					return false;
				IsKeyed = true;
				OnKey?.Invoke();
				return true;
			}

			public bool Unkey()
			{
				Interlocked.Increment(ref _unkeyCalls);
				if (FailUnkey)
					return false;
				IsKeyed = false;
				return true;
			}

			public void Dispose() { }
		}
	}
}
