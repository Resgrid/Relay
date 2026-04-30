using FluentAssertions;
using MimeKit;
using NUnit.Framework;
using Resgrid.Audio.Relay.Console.Configuration;
using Resgrid.Audio.Relay.Console.Smtp;
using Resgrid.Providers.ApiClient.V4.Models;
using SmtpServer;
using SmtpServer.IO;
using SmtpServer.Mail;
using SmtpServer.Protocol;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Resgrid.Audio.Tests
{
	[TestFixture]
	public class SmtpRelayTelemetryTests
	{
		[Test]
		public async Task SaveAsync_Should_Track_Unroutable_Message_And_Return_Ok()
		{
			var dataDirectory = CreateTempDirectory();
			try
			{
				var telemetry = new FakeSmtpTelemetry();
				var callsClient = new FakeResgridCallsClient();
				var store = new RelayMessageStore(CreateOptions(dataDirectory), telemetry, callsClient);

				var response = await store.SaveAsync(
					new FakeSessionContext(),
					null,
					CreateMessageBuffer("msg-unroutable", "sender@example.com", new[] { "invalid@example.com" }, "Dispatch Test", "Alarm body"),
					CancellationToken.None);

				response.ReplyCode.Should().Be(SmtpResponse.Ok.ReplyCode);
				callsClient.SaveCallInputs.Should().BeEmpty();
				telemetry.ReceivedMessages.Should().ContainSingle();
				telemetry.UnroutableMessages.Should().ContainSingle();
				telemetry.UnroutableMessages[0].Subject.Should().Be("Dispatch Test");
				telemetry.UnroutableMessages[0].ToAddresses.Should().Contain("invalid@example.com");
				telemetry.UnroutableMessages[0].DispatchTargetCount.Should().Be(0);
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		[Test]
		public async Task SaveAsync_Should_Remove_Duplicate_Registration_When_Processing_Fails()
		{
			var dataDirectory = CreateTempDirectory();
			try
			{
				var buffer = CreateMessageBuffer("msg-retry", "sender@example.com", new[] { "abc123@dispatch.resgrid.com" }, "Structure Fire", "First line");

				var failingTelemetry = new FakeSmtpTelemetry();
				var failingClient = new FakeResgridCallsClient
				{
					SaveCallException = new InvalidOperationException("Resgrid is unavailable")
				};
				var failingStore = new RelayMessageStore(CreateOptions(dataDirectory), failingTelemetry, failingClient);

				await FluentActions
					.Awaiting(() => failingStore.SaveAsync(new FakeSessionContext(), null, buffer, CancellationToken.None))
					.Should()
					.ThrowAsync<InvalidOperationException>()
					.WithMessage("Resgrid is unavailable");

				failingTelemetry.FailedMessages.Should().ContainSingle();

				var successTelemetry = new FakeSmtpTelemetry();
				var successClient = new FakeResgridCallsClient
				{
					NextCallId = "call-42"
				};
				var successStore = new RelayMessageStore(CreateOptions(dataDirectory), successTelemetry, successClient);

				var response = await successStore.SaveAsync(new FakeSessionContext(), null, buffer, CancellationToken.None);

				response.ReplyCode.Should().Be(SmtpResponse.Ok.ReplyCode);
				successClient.SaveCallInputs.Should().ContainSingle();
				successTelemetry.ProcessedMessages.Should().ContainSingle();
				successTelemetry.ProcessedMessages[0].CallId.Should().Be("call-42");
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		[Test]
		public async Task MailboxFilter_Should_Track_Sender_And_Recipient_Outcomes()
		{
			var dataDirectory = CreateTempDirectory();
			try
			{
				var telemetry = new FakeSmtpTelemetry();
				var filter = new RelayMailboxFilter(CreateOptions(dataDirectory), telemetry);
				var context = new FakeSessionContext();
				var sender = new Mailbox("sender", "example.com");

				var senderAccepted = await filter.CanAcceptFromAsync(context, sender, 2048, CancellationToken.None);
				var validRecipientAccepted = await filter.CanDeliverToAsync(context, new Mailbox("abc123", "dispatch.resgrid.com"), sender, CancellationToken.None);
				var invalidRecipientAccepted = await filter.CanDeliverToAsync(context, new Mailbox("bad", "example.com"), sender, CancellationToken.None);

				senderAccepted.Should().BeTrue();
				validRecipientAccepted.Should().BeTrue();
				invalidRecipientAccepted.Should().BeFalse();
				telemetry.SenderAcceptCount.Should().Be(1);
				telemetry.RecipientOutcomes.Should().HaveCount(2);
				telemetry.RecipientOutcomes[0].Recipient.Should().Be("abc123@dispatch.resgrid.com");
				telemetry.RecipientOutcomes[0].Accepted.Should().BeTrue();
				telemetry.RecipientOutcomes[1].Recipient.Should().Be("bad@example.com");
				telemetry.RecipientOutcomes[1].Accepted.Should().BeFalse();
				telemetry.RecipientOutcomes[1].Reason.Should().Contain("recipient domain");
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		private static SmtpRelayOptions CreateOptions(string dataDirectory)
		{
			return new SmtpRelayOptions
			{
				ServerName = "relay-test",
				DataDirectory = dataDirectory,
				SaveRawMessages = false,
				MaxAttachmentBytes = 1024,
				DepartmentDispatchPrefix = "G",
				DepartmentAddressDomains = new[] { "dispatch.resgrid.com" },
				GroupAddressDomains = new[] { "groups.resgrid.com" }
			};
		}

		private static ReadOnlySequence<byte> CreateMessageBuffer(string messageId, string from, IEnumerable<string> to, string subject, string body)
		{
			var message = new MimeMessage
			{
				MessageId = messageId,
				Subject = subject,
				Body = new TextPart("plain")
				{
					Text = body
				}
			};

			message.From.Add(MailboxAddress.Parse(from));
			foreach (var recipient in to)
			{
				message.To.Add(MailboxAddress.Parse(recipient));
			}

			using var stream = new MemoryStream();
			message.WriteTo(stream);
			return new ReadOnlySequence<byte>(stream.ToArray());
		}

		private static string CreateTempDirectory()
		{
			var path = Path.Combine(Path.GetTempPath(), $"resgrid-relay-tests-{Guid.NewGuid():N}");
			Directory.CreateDirectory(path);
			return path;
		}

		private static void DeleteDirectory(string path)
		{
			if (!String.IsNullOrWhiteSpace(path) && Directory.Exists(path))
				Directory.Delete(path, recursive: true);
		}

		private sealed class FakeResgridCallsClient : IResgridCallsClient
		{
			public List<NewCallInput> SaveCallInputs { get; } = new List<NewCallInput>();
			public List<SaveCallFileInput> SaveCallFileInputs { get; } = new List<SaveCallFileInput>();
			public Exception SaveCallException { get; set; }
			public string NextCallId { get; set; } = "call-1";

			public Task<string> SaveCallAsync(NewCallInput call, CancellationToken cancellationToken)
			{
				if (SaveCallException != null)
					throw SaveCallException;

				SaveCallInputs.Add(call);
				return Task.FromResult(NextCallId);
			}

			public Task<string> SaveCallFileAsync(SaveCallFileInput file, CancellationToken cancellationToken)
			{
				SaveCallFileInputs.Add(file);
				return Task.FromResult($"file-{SaveCallFileInputs.Count}");
			}
		}

		private sealed class FakeSmtpTelemetry : ISmtpTelemetry
		{
			public int SenderAcceptCount { get; private set; }
			public List<RecipientOutcome> RecipientOutcomes { get; } = new List<RecipientOutcome>();
			public List<SmtpMessageSummary> ReceivedMessages { get; } = new List<SmtpMessageSummary>();
			public List<SmtpMessageSummary> UnroutableMessages { get; } = new List<SmtpMessageSummary>();
			public List<SmtpMessageSummary> ProcessedMessages { get; } = new List<SmtpMessageSummary>();
			public List<(SmtpMessageSummary Message, Exception Exception)> FailedMessages { get; } = new List<(SmtpMessageSummary, Exception)>();

			public void RelayStarting(SmtpRelayOptions options)
			{
			}

			public void RelayStopped(SmtpRelayOptions options)
			{
			}

			public void RelayFaulted(SmtpRelayOptions options, Exception exception)
			{
			}

			public void SessionCreated(ISessionContext context)
			{
			}

			public void SessionCompleted(ISessionContext context)
			{
			}

			public void SessionCancelled(ISessionContext context)
			{
			}

			public void SessionFaulted(ISessionContext context, Exception exception)
			{
			}

			public void SenderAccepted(ISessionContext context, IMailbox from, int size)
			{
				SenderAcceptCount++;
			}

			public void RecipientEvaluated(ISessionContext context, IMailbox to, IMailbox from, bool accepted, string reason)
			{
				RecipientOutcomes.Add(new RecipientOutcome
				{
					Recipient = to == null ? null : $"{to.User}@{to.Host}",
					Accepted = accepted,
					Reason = reason
				});
			}

			public void MessageReceived(ISessionContext context, SmtpMessageSummary message)
			{
				ReceivedMessages.Add(message);
			}

			public void DuplicateMessage(ISessionContext context, SmtpMessageSummary message)
			{
			}

			public void UnroutableMessage(ISessionContext context, SmtpMessageSummary message)
			{
				UnroutableMessages.Add(message);
			}

			public void UnsupportedTarget(ISessionContext context, SmtpMessageSummary message)
			{
			}

			public void MessageProcessingStarted(ISessionContext context, SmtpMessageSummary message)
			{
			}

			public void MessageProcessed(ISessionContext context, SmtpMessageSummary message, TimeSpan duration)
			{
				ProcessedMessages.Add(message);
			}

			public void MessageFailed(ISessionContext context, SmtpMessageSummary message, Exception exception, TimeSpan duration)
			{
				FailedMessages.Add((message, exception));
			}

			public ValueTask DisposeAsync()
			{
				return ValueTask.CompletedTask;
			}
		}

		private sealed class RecipientOutcome
		{
			public string Recipient { get; set; }
			public bool Accepted { get; set; }
			public string Reason { get; set; }
		}

		private sealed class FakeSessionContext : ISessionContext
		{
#pragma warning disable CS0067
			public FakeSessionContext()
			{
				Properties = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
				{
					["EndpointListener:RemoteEndPoint"] = "127.0.0.1:45000",
					["EndpointListener:LocalEndPoint"] = "127.0.0.1:2525"
				};
			}

			public event EventHandler<SmtpCommandEventArgs> CommandExecuted;
			public event EventHandler<SmtpCommandEventArgs> CommandExecuting;
			public event EventHandler<SmtpResponseExceptionEventArgs> ResponseException;
			public event EventHandler<EventArgs> SessionAuthenticated;

			public AuthenticationContext Authentication => null;
			public IEndpointDefinition EndpointDefinition => null;
			public ISecurableDuplexPipe Pipe => null;
			public IDictionary<string, object> Properties { get; }
			public ISmtpServerOptions ServerOptions => null;
			public IServiceProvider ServiceProvider => null;
			public Guid SessionId { get; } = Guid.NewGuid();
#pragma warning restore CS0067
		}
	}
}
