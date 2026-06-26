using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Resgrid.Relay.Engine;
using Resgrid.Relay.Engine.Configuration;
using Resgrid.Relay.Engine.Services;

namespace Resgrid.Audio.Voice.Tests
{
	/// <summary>
	/// Behavioral coverage for the LiveKit-mode resilience loop in
	/// <see cref="RelayServiceBase"/>: exponential back-off retries, the consecutive-failure
	/// circuit breaker, and graceful stop while retrying. Uses a tiny test subclass with a
	/// scripted <c>ExecuteAsync</c> and near-zero back-off so the tests run fast.
	/// </summary>
	[TestFixture]
	public class RelayResilienceTests
	{
		private static RelayHostOptions OptionsWith(ResilienceOptions resilience)
		{
			return new RelayHostOptions
			{
				Resilience = resilience,
				// No Sentry DSN ⇒ telemetry is the no-op singleton (no SDK init).
				Telemetry = new RelayTelemetryOptions()
			};
		}

		private static ResilienceOptions FastResilience(int maxFailures, double healthySeconds = 1000) => new ResilienceOptions
		{
			Enabled = true,
			MaxConsecutiveFailures = maxFailures,
			InitialBackoffSeconds = 0.01,
			MaxBackoffSeconds = 0.05,
			HealthyRunSeconds = healthySeconds
		};

		[Test]
		public void ComputeBackoff_GrowsExponentially_AndIsCappedWithJitter()
		{
			var r = new ResilienceOptions
			{
				InitialBackoffSeconds = 2,
				MaxBackoffSeconds = 60,
				MaxConsecutiveFailures = 10,
				HealthyRunSeconds = 30
			};

			// failure 1 ≈ 2s ±20% ⇒ [1.6, 2.4]; failure 3 ≈ 8s ±20% ⇒ [6.4, 9.6];
			// failure 10 caps at 60s ±20% ⇒ [48, 72]; floor is always >= 0.5s.
			RelayServiceBase.ComputeBackoff(r, 1).TotalSeconds.Should().BeInRange(1.6, 2.4);
			RelayServiceBase.ComputeBackoff(r, 3).TotalSeconds.Should().BeInRange(6.4, 9.6);
			RelayServiceBase.ComputeBackoff(r, 10).TotalSeconds.Should().BeInRange(48, 72);
			RelayServiceBase.ComputeBackoff(r, 1).TotalSeconds.Should().BeGreaterThanOrEqualTo(0.5);
		}

		[Test]
		public async Task AlwaysFailing_LiveKitMode_TripsBreaker_AndFaults()
		{
			var svc = new ScriptedRelayService(OptionsWith(FastResilience(maxFailures: 3)));

			// ExecuteAsync throws immediately every time; the breaker opens on the 3rd failure.
			svc.ExecuteBehavior = (_, __) => throw new InvalidOperationException("boom");

			await svc.StartAsync(CancellationToken.None);

			svc.State.Should().Be(RelayServiceState.Faulted);
			svc.Attempts.Should().Be(3, "the breaker opens once MaxConsecutiveFailures quick failures pile up");
			svc.Status.LiveKit.Should().Be(ConnectionState.Degraded);
		}

		[Test]
		public async Task TransientFailures_ThenGracefulStop_EndsStopped_NotFaulted()
		{
			var svc = new ScriptedRelayService(OptionsWith(FastResilience(maxFailures: 5)));

			// First two attempts fail (transient); the third blocks until cancelled, then
			// returns by honoring the token ⇒ graceful Stopped, never reaching the breaker.
			svc.ExecuteBehavior = async (s, token) =>
			{
				if (s.Attempts < 3)
					throw new InvalidOperationException("transient");
				await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
			};

			var run = svc.StartAsync(CancellationToken.None);

			// Wait until the run is parked in the long-lived (3rd) attempt, then stop it.
			var spun = SpinWait.SpinUntil(() => svc.Attempts >= 3 && svc.State == RelayServiceState.Running, TimeSpan.FromSeconds(5));
			spun.Should().BeTrue("the service should reach its healthy long-lived run after the transient failures");

			await svc.StopAsync();
			await run;

			svc.State.Should().Be(RelayServiceState.Stopped);
			svc.Attempts.Should().Be(3);
		}

		[Test]
		public async Task ResilienceDisabled_RunsExecuteOnce_AndPropagatesFault()
		{
			var resilience = FastResilience(maxFailures: 3);
			resilience.Enabled = false;
			var svc = new ScriptedRelayService(OptionsWith(resilience));

			svc.ExecuteBehavior = (_, __) => throw new InvalidOperationException("boom");

			await svc.StartAsync(CancellationToken.None);

			svc.State.Should().Be(RelayServiceState.Faulted);
			svc.Attempts.Should().Be(1, "with resilience disabled ExecuteAsync runs exactly once");
		}

		/// <summary>Minimal LiveKit-mode service whose run is scripted by the test.</summary>
		private sealed class ScriptedRelayService : RelayServiceBase
		{
			private int _attempts;

			public ScriptedRelayService(RelayHostOptions options)
				: base("test", options, null)
			{
			}

			protected override bool IsLiveKitMode => true;

			public int Attempts => Volatile.Read(ref _attempts);

			public Func<ScriptedRelayService, CancellationToken, Task> ExecuteBehavior { get; set; }

			protected override async Task ExecuteAsync(CancellationToken token)
			{
				Interlocked.Increment(ref _attempts);
				await ExecuteBehavior(this, token).ConfigureAwait(false);
			}
		}
	}
}
