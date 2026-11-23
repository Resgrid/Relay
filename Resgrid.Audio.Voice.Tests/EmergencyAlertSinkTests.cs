using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Resgrid.Audio.Voice.Dsp;
using Resgrid.Providers.ApiClient.V4;
using Resgrid.Providers.ApiClient.V4.Models;

namespace Resgrid.Audio.Voice.Tests
{
	[TestFixture]
	public class EmergencyAlertSinkTests
	{
		[Test]
		public async Task RaiseAsync_Creates_One_Call_For_A_Repeated_Emergency_Packet()
		{
			var now = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
			var calls = new RecordingCallsApi();
			var sink = new ResgridEmergencyAlertSink(null, createCall: true, priority: 3, callsApi: calls, departmentId: "42",
				cooldown: TimeSpan.FromMinutes(2), utcNow: () => now);

			// A radio in emergency resends the same MDC-1200 packet until it is acknowledged.
			await sink.RaiseAsync("MDC-1200", "Emergency from unit 1234 (op 0x00)");
			now = now.AddSeconds(5);
			await sink.RaiseAsync("MDC-1200", "Emergency from unit 1234 (op 0x00)");
			await sink.RaiseAsync("MDC-1200", "Emergency from unit 5678 (op 0x00)");

			calls.Saved.Should().HaveCount(2, "a second unit is a separate emergency, a repeat of the first is not");
			calls.Saved.Should().OnlyContain(x => x.DepartmentId == "42" && x.Priority == 3);

			now = now.AddMinutes(3);
			await sink.RaiseAsync("MDC-1200", "Emergency from unit 1234 (op 0x00)");

			calls.Saved.Should().HaveCount(3, "once the cooldown passes the same unit can raise a new emergency");
		}

		[Test]
		public async Task RaiseAsync_Only_Logs_When_Call_Creation_Is_Off()
		{
			var calls = new RecordingCallsApi();
			var sink = new ResgridEmergencyAlertSink(null, createCall: false, callsApi: calls);

			await sink.RaiseAsync("Tone", "Emergency tone 1500 Hz held 2000 ms");

			calls.Saved.Should().BeEmpty();
		}

		private sealed class RecordingCallsApi : IResgridCallsApi
		{
			public List<NewCallInput> Saved { get; } = new List<NewCallInput>();

			public Task<string> SaveCallAsync(NewCallInput call, CancellationToken cancellationToken = default) =>
				SaveCallAsync(call, null, cancellationToken);

			public Task<string> SaveCallAsync(NewCallInput call, string idempotencyKey, CancellationToken cancellationToken = default)
			{
				Saved.Add(call);
				return Task.FromResult($"call-{Saved.Count}");
			}

			public Task<GetCallResult> GetCallAsync(string callId, string departmentId = null, CancellationToken cancellationToken = default) =>
				Task.FromResult<GetCallResult>(null);

			public Task<string> SaveCallFileAsync(SaveCallFileInput file, CancellationToken cancellationToken = default) =>
				Task.FromResult<string>(null);

			public Task<string> SaveCallFileAsync(SaveCallFileInput file, string idempotencyKey, CancellationToken cancellationToken = default) =>
				Task.FromResult<string>(null);

			public Task<List<CallResultData>> GetActiveCallsAsync(string departmentId = null, CancellationToken cancellationToken = default) =>
				Task.FromResult(new List<CallResultData>());
		}
	}
}
