using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Resgrid.Audio.Relay.Services;
using Resgrid.Relay.Engine;
using Resgrid.Relay.Engine.Configuration;
using Serilog;

namespace Resgrid.Audio.Relay.Tests
{
	/// <summary>
	/// The desktop shutdown barrier of <see cref="RelayController"/>: stops already in progress are waited for, modes stop in
	/// parallel, and no mode starts once shutdown has begun — plus the <see cref="RelayModeLauncher"/> rules built on it (a
	/// stop-all cancels starts issued before it; a bridge still tearing down keeps the radio). Runs against fake services whose
	/// teardown is held open by a gate (standing in for a recorder still saving a transmission).
	/// </summary>
	[TestFixture]
	public class RelayControllerTests
	{
		private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);
		private static readonly TimeSpan Long = TimeSpan.FromSeconds(10);

		private readonly List<FakeService> _created = new List<FakeService>();
		private RelayController _controller;

		[SetUp]
		public void SetUp()
		{
			_created.Clear();
			_controller = new RelayController(new LoggerConfiguration().CreateLogger(), (options, _) =>
			{
				var service = new FakeService(options.Mode);
				_created.Add(service);
				return service;
			});
		}

		[TearDown]
		public void TearDown()
		{
			foreach (var service in _created)
				service.ReleaseTeardown();
			_controller.StopAllAsync().Wait(Long);
		}

		private RelayModeLauncher Launcher()
		{
			var logger = new LoggerConfiguration().CreateLogger();
			return new RelayModeLauncher(_controller, new ConfigurationService(), new DesktopSettingsService(logger), logger);
		}

		private FakeService Start(string mode)
		{
			_controller.Start(new RelayHostOptions { Mode = mode });
			return _created[_created.Count - 1];
		}

		[Test]
		public async Task StopAll_WaitsForAStopAlreadyInProgress()
		{
			var service = Start("record");
			await service.Running.WaitAsync(Long);

			var firstStop = _controller.StopAsync(service);
			await service.Cancelled.WaitAsync(Long);

			// The stop has taken the service out of the running set, but its teardown is still saving.
			_controller.IsRunning.Should().BeFalse();
			_controller.HasPendingStops.Should().BeTrue();

			var stopAll = _controller.StopAllAsync();
			(await Task.WhenAny(stopAll, Task.Delay(Short))).Should().NotBeSameAs(stopAll, "the teardown has not finished");

			service.ReleaseTeardown();
			await stopAll.WaitAsync(Long);
			await firstStop.WaitAsync(Long);

			service.Disposed.Should().BeTrue();
			_controller.HasPendingStops.Should().BeFalse();
		}

		[Test]
		public async Task Stop_OfAServiceAlreadyStopping_WaitsForThatTeardown()
		{
			var service = Start("radio");
			await service.Running.WaitAsync(Long);

			var firstStop = _controller.StopAsync(service);
			await service.Cancelled.WaitAsync(Long);

			var secondStop = _controller.StopAsync(service);
			(await Task.WhenAny(secondStop, Task.Delay(Short))).Should().NotBeSameAs(secondStop);

			service.ReleaseTeardown();
			await Task.WhenAll(firstStop, secondStop).WaitAsync(Long);
			service.DisposeCount.Should().Be(1, "the service is torn down once");
		}

		[Test]
		public async Task StopAll_StopsModesInParallel()
		{
			var radio = Start("radio");
			var record = Start("record");
			await Task.WhenAll(radio.Running, record.Running).WaitAsync(Long);

			var stopAll = _controller.StopAllAsync();

			// Both teardowns are under way before either finishes: one slow recorder flush no longer delays the next mode.
			await Task.WhenAll(radio.Cancelled, record.Cancelled).WaitAsync(Long);
			stopAll.IsCompleted.Should().BeFalse();

			radio.ReleaseTeardown();
			record.ReleaseTeardown();
			await stopAll.WaitAsync(Long);
			radio.Disposed.Should().BeTrue();
			record.Disposed.Should().BeTrue();
		}

		[Test]
		public void Start_AfterBeginShutdown_IsRefused()
		{
			_controller.BeginShutdown();

			Action start = () => _controller.Start(new RelayHostOptions { Mode = "smtp" });

			start.Should().Throw<InvalidOperationException>();
			_created.Should().BeEmpty();
			_controller.IsRunning.Should().BeFalse();
			_controller.IsShuttingDown.Should().BeTrue();
		}

		[Test]
		public async Task BeginShutdown_LeavesRunningModesForTheStopPass()
		{
			var service = Start("dispatch");
			await service.Running.WaitAsync(Long);

			_controller.BeginShutdown();
			_controller.IsModeRunning("dispatch").Should().BeTrue();

			service.ReleaseTeardown();
			await _controller.StopAllAsync().WaitAsync(Long);
			service.Disposed.Should().BeTrue();
			_controller.IsRunning.Should().BeFalse();
		}

		[Test]
		public void Container_ResolvesTheControllerWithTheEngineFactory()
		{
			var services = new ServiceCollection();
			services.AddRelayEngine();
			services.AddSingleton<ILogger>(new LoggerConfiguration().CreateLogger());
			services.AddSingleton<RelayController>();

			using var provider = services.BuildServiceProvider();

			provider.GetRequiredService<RelayController>().Should().NotBeNull();
		}

		[Test]
		public async Task StopAllRequests_CountsEachStopAll()
		{
			var before = _controller.StopAllRequests;

			await _controller.StopAllAsync();
			await _controller.StopAllAsync();

			_controller.StopAllRequests.Should().Be(before + 2);
		}

		[Test]
		public async Task Start_RequestedBeforeAStopAll_IsRefused()
		{
			var requested = _controller.StopAllRequests;
			await _controller.StopAllAsync();

			Action start = () => _controller.Start(new RelayHostOptions { Mode = "radio" }, requested);

			start.Should().Throw<InvalidOperationException>();
			_created.Should().BeEmpty();
			_controller.IsRunning.Should().BeFalse();
		}

		[Test]
		public async Task WhenModeStopped_WaitsForThatModesTeardownOnly()
		{
			var radio = Start("radio");
			await radio.Running.WaitAsync(Long);

			var stopAll = _controller.StopAllAsync();
			await radio.Cancelled.WaitAsync(Long);

			_controller.Find("radio").Should().BeNull("the stop took it out of the tracked set");
			_controller.IsModeStopping("radio").Should().BeTrue();
			_controller.IsModeStopping("record").Should().BeFalse();
			_controller.WhenModeStopped("record").IsCompleted.Should().BeTrue();
			var radioStopped = _controller.WhenModeStopped("radio");
			(await Task.WhenAny(radioStopped, Task.Delay(Short))).Should().NotBeSameAs(radioStopped);

			radio.ReleaseTeardown();
			await Task.WhenAll(stopAll, radioStopped).WaitAsync(Long);
			_controller.IsModeStopping("radio").Should().BeFalse();
		}

		// ─────────── RelayModeLauncher ───────────

		[Test]
		public async Task TryClaimRadio_WhileTheBridgeIsStillStopping_IsRefused()
		{
			var launcher = Launcher();
			var radio = Start("radio");
			await radio.Running.WaitAsync(Long);

			// Stop all doesn't take the launcher's gate, and the bridge is still releasing the radio's audio and PTT.
			var stopAll = _controller.StopAllAsync();
			await radio.Cancelled.WaitAsync(Long);

			launcher.TryClaimRadio(out var reason).Should().BeNull();
			reason.Should().Contain("still stopping");

			radio.ReleaseTeardown();
			await stopAll.WaitAsync(Long);

			using var claim = launcher.TryClaimRadio(out reason);
			claim.Should().NotBeNull(reason);
		}

		[Test]
		public async Task StartAsync_QueuedBehindARadioClaim_IsCancelledByAStopAll()
		{
			var launcher = Launcher();
			var claim = launcher.TryClaimRadio(out var reason);
			claim.Should().NotBeNull(reason);

			var start = launcher.StartAsync("radio");
			(await Task.WhenAny(start, Task.Delay(Short))).Should().NotBeSameAs(start, "the transmit test holds the radio");

			await _controller.StopAllAsync();
			claim.Dispose();

			(await start.WaitAsync(Long)).Should().Contain("Stop all");
			_created.Should().BeEmpty("the start was issued before the stop-all");
		}

		[Test]
		public async Task StartAsync_ForARequestThatPrecedesAStopAll_IsCancelled()
		{
			var launcher = Launcher();
			var requested = _controller.StopAllRequests;
			await _controller.StopAllAsync();

			(await launcher.StartAsync("smtp", requested).WaitAsync(Long)).Should().Contain("Stop all");
			_created.Should().BeEmpty();
		}

		[Test]
		public async Task StartAsync_WaitsForThePreviousInstancesTeardown()
		{
			var launcher = Launcher();
			var radio = Start("radio");
			await radio.Running.WaitAsync(Long);

			var stopAll = _controller.StopAllAsync();
			await radio.Cancelled.WaitAsync(Long);

			// Requested after the stop-all, so it should run — but only once the old bridge has let go of the radio.
			var start = launcher.StartAsync("radio");
			(await Task.WhenAny(start, Task.Delay(Short))).Should().NotBeSameAs(start);
			_created.Should().HaveCount(1, "no replacement starts while the old bridge is still tearing down");

			radio.ReleaseTeardown();
			await stopAll.WaitAsync(Long);
			await start.WaitAsync(Long);
		}

		/// <summary>Runs until cancelled, then holds its teardown open until <see cref="ReleaseTeardown"/>.</summary>
		private sealed class FakeService : IRelayService
		{
			private readonly TaskCompletionSource<bool> _running = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			private readonly TaskCompletionSource<bool> _cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			private readonly TaskCompletionSource<bool> _teardown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			private int _disposeCount;

			public FakeService(string mode)
			{
				Mode = mode;
			}

			public string Mode { get; }
			public RelayServiceState State { get; private set; } = RelayServiceState.Stopped;
			public IRelayStatus Status { get; } = new RelayStatus();

			public event EventHandler<RelayStateChangedEventArgs> StateChanged
			{
				add { }
				remove { }
			}

			public Task Running => _running.Task;
			public Task Cancelled => _cancelled.Task;
			public int DisposeCount => Volatile.Read(ref _disposeCount);
			public bool Disposed => DisposeCount > 0;

			public void ReleaseTeardown() => _teardown.TrySetResult(true);

			public async Task StartAsync(CancellationToken token)
			{
				State = RelayServiceState.Running;
				_running.TrySetResult(true);
				try
				{
					await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					_cancelled.TrySetResult(true);
					await _teardown.Task.ConfigureAwait(false);
				}
				State = RelayServiceState.Stopped;
			}

			public Task StopAsync() => Task.CompletedTask;

			public ValueTask DisposeAsync()
			{
				Interlocked.Increment(ref _disposeCount);
				return default;
			}
		}
	}
}
