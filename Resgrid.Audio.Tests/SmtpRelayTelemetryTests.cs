using FluentAssertions;
using MimeKit;
using NUnit.Framework;
using Resgrid.Relay.Engine.Configuration;
using Resgrid.Relay.Engine.Smtp;
using Resgrid.Providers.ApiClient.V4;
using Resgrid.Providers.ApiClient.V4.Models;
using SmtpServer;
using SmtpServer.IO;
using SmtpServer.Mail;
using SmtpServer.Protocol;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Resgrid.Audio.Tests
{
	[TestFixture]
	public partial class SmtpRelayTelemetryTests
	{
		[Test]
		public async Task SaveAsync_Should_Track_Unroutable_Message_And_Return_Ok()
		{
			var dataDirectory = CreateTempDirectory();
			try
			{
				var telemetry = new FakeSmtpTelemetry();
				var callsClient = new FakeResgridCallsClient();
				var store = new RelayMessageStore(CreateOptions(dataDirectory), telemetry, callsClient: callsClient);

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
				var failingStore = new RelayMessageStore(CreateOptions(dataDirectory), failingTelemetry, callsClient: failingClient);

				// Answered with 451, not thrown: SmtpServer turns an exception into a permanent 554 and the sender would never retry.
				var failed = await failingStore.SaveAsync(new FakeSessionContext(), null, buffer, CancellationToken.None);

				failed.ReplyCode.Should().Be(SmtpReplyCode.Aborted);
				failingTelemetry.FailedMessages.Should().ContainSingle();
				failingTelemetry.FailedMessages[0].Exception.Message.Should().Be("Resgrid is unavailable");
				failingTelemetry.FailedMessages[0].Message.SmtpReplyCode.Should().Be(451);

				var successTelemetry = new FakeSmtpTelemetry();
				var successClient = new FakeResgridCallsClient
				{
					NextCallId = "call-42"
				};
				var successStore = new RelayMessageStore(CreateOptions(dataDirectory), successTelemetry, callsClient: successClient);

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
		public async Task SaveAsync_Should_Key_The_Call_And_Each_Attachment_By_Message_Id_So_A_Retry_Repeats_The_Same_Keys()
		{
			var dataDirectory = CreateTempDirectory();
			try
			{
				var buffer = CreateMessageBuffer("msg-keyed@cad.example", "sender@example.com", new[] { "abc123@dispatch.resgrid.com" }, "Structure Fire", "First line",
					("map.pdf", new byte[] { 1, 2, 3 }), ("audio.mp3", new byte[] { 4, 5, 6 }));

				var client = new FakeResgridCallsClient
				{
					NextCallId = "call-7"
				};
				var store = new RelayMessageStore(CreateOptions(dataDirectory), new FakeSmtpTelemetry(), callsClient: client);
				await store.SaveAsync(new FakeSessionContext(), null, buffer, CancellationToken.None);

				client.SaveCallKeys.Should().Equal("msg-keyed@cad.example");
				client.SaveCallFileKeys.Should().Equal("msg-keyed@cad.example#attachment-0", "msg-keyed@cad.example#attachment-1");

				var retryDirectory = CreateTempDirectory();
				try
				{
					var failing = new FakeResgridCallsClient { NextCallId = "call-7" };
					var failingStore = new RelayMessageStore(CreateOptions(retryDirectory), new FakeSmtpTelemetry(), callsClient: failing);

					// The first upload fails after the call was saved; the sender delivers the message again, and the retry
					// sends the same call key (Resgrid returns the existing call) and the same attachment keys.
					failing.NextSaveCallFileException = new InvalidOperationException("upload failed");

					(await failingStore.SaveAsync(new FakeSessionContext(), null, buffer, CancellationToken.None))
						.ReplyCode.Should().Be(SmtpReplyCode.Aborted);

					var response = await failingStore.SaveAsync(new FakeSessionContext(), null, buffer, CancellationToken.None);

					response.ReplyCode.Should().Be(SmtpResponse.Ok.ReplyCode);
					failing.SaveCallKeys.Should().Equal("msg-keyed@cad.example", "msg-keyed@cad.example");
					failing.SaveCallFileKeys.Should().Equal("msg-keyed@cad.example#attachment-0",
						"msg-keyed@cad.example#attachment-0", "msg-keyed@cad.example#attachment-1");
				}
				finally
				{
					DeleteDirectory(retryDirectory);
				}
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		// Anything that may succeed later asks the sender to retry; a retry is safe because the call and its files are keyed.
		[TestCase(HttpStatusCode.ServiceUnavailable)]
		[TestCase(HttpStatusCode.InternalServerError)]
		[TestCase(HttpStatusCode.Conflict)]
		[TestCase(HttpStatusCode.TooManyRequests)]
		[TestCase(HttpStatusCode.RequestTimeout)]
		[TestCase(HttpStatusCode.Unauthorized)]
		[TestCase(HttpStatusCode.Forbidden)]
		[TestCase(HttpStatusCode.NotFound)] // Resgrid reports a miss in a 200; a 404 means the request never reached the endpoint
		public async Task SaveAsync_Should_Ask_The_Sender_To_Retry_When_Resgrid_May_Succeed_Later(HttpStatusCode status)
		{
			var reply = await ProcessFailingCallAsync(new HttpRequestException("failed", null, status));

			reply.ReplyCode.Should().Be(SmtpReplyCode.Aborted);
			reply.Message.Should().StartWith("4.3.0");
		}

		[Test]
		public async Task SaveAsync_Should_Ask_The_Sender_To_Retry_On_Timeouts_And_Network_Errors()
		{
			(await ProcessFailingCallAsync(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout")))
				.ReplyCode.Should().Be(SmtpReplyCode.Aborted);
			(await ProcessFailingCallAsync(new HttpRequestException("No such host is known.")))
				.ReplyCode.Should().Be(SmtpReplyCode.Aborted, "a network error has no status code");
		}

		// Resgrid gives the same answer to the same request every time: bounce it so the sender learns now, not in days.
		[TestCase(HttpStatusCode.BadRequest)]
		[TestCase(HttpStatusCode.Gone)]
		[TestCase(HttpStatusCode.RequestEntityTooLarge)]
		[TestCase(HttpStatusCode.UnprocessableEntity)]
		public async Task SaveAsync_Should_Bounce_A_Call_Resgrid_Rejects_Outright(HttpStatusCode status)
		{
			var reply = await ProcessFailingCallAsync(new HttpRequestException("rejected", null, status));

			reply.ReplyCode.Should().Be(SmtpReplyCode.TransactionFailed);
			reply.Message.Should().Be($"5.6.0 Resgrid rejected the dispatch (HTTP {(int)status})");
		}

		[Test]
		public async Task SaveAsync_Should_Skip_An_Attachment_Resgrid_Rejects_Outright_And_Still_Accept_The_Message()
		{
			var dataDirectory = CreateTempDirectory();
			try
			{
				var telemetry = new FakeSmtpTelemetry();
				var client = new FakeResgridCallsClient
				{
					NextSaveCallFileException = new HttpRequestException("rejected", null, HttpStatusCode.BadRequest)
				};
				var store = new RelayMessageStore(CreateOptions(dataDirectory), telemetry, callsClient: client);
				var buffer = CreateMessageBuffer("msg-files@cad.example", "sender@example.com", new[] { "abc123@dispatch.resgrid.com" }, "Structure Fire", "First line",
					("map.pdf", new byte[] { 1, 2, 3 }), ("audio.mp3", new byte[] { 4, 5, 6 }));

				var reply = await store.SaveAsync(new FakeSessionContext(), null, buffer, CancellationToken.None);

				reply.ReplyCode.Should().Be(SmtpReplyCode.Ok, "the call was created and dispatched");
				client.SaveCallFileInputs.Should().ContainSingle().Which.Name.Should().Be("audio.mp3");
				telemetry.ProcessedMessages.Should().ContainSingle().Which.RejectedAttachmentNames.Should().Equal("map.pdf");
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		[Test]
		public async Task SaveAsync_Should_Ask_The_Sender_To_Retry_When_An_Attachment_Upload_Fails_For_Now()
		{
			var dataDirectory = CreateTempDirectory();
			try
			{
				var client = new FakeResgridCallsClient
				{
					NextSaveCallFileException = new HttpRequestException("unavailable", null, HttpStatusCode.ServiceUnavailable)
				};
				var store = new RelayMessageStore(CreateOptions(dataDirectory), new FakeSmtpTelemetry(), callsClient: client);
				var buffer = CreateMessageBuffer("msg-files-2@cad.example", "sender@example.com", new[] { "abc123@dispatch.resgrid.com" }, "Structure Fire", "First line",
					("map.pdf", new byte[] { 1, 2, 3 }));

				(await store.SaveAsync(new FakeSessionContext(), null, buffer, CancellationToken.None)).ReplyCode.Should().Be(SmtpReplyCode.Aborted);
				(await store.SaveAsync(new FakeSessionContext(), null, buffer, CancellationToken.None)).ReplyCode.Should().Be(SmtpReplyCode.Ok,
					"the registration was rolled back, so the redelivery is processed rather than dropped as a duplicate");
				client.SaveCallFileInputs.Should().ContainSingle();
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		[Test]
		public async Task SaveAsync_Should_Bounce_A_Message_That_Cannot_Be_Parsed()
		{
			var dataDirectory = CreateTempDirectory();
			try
			{
				var client = new FakeResgridCallsClient();
				var telemetry = new FakeSmtpTelemetry();
				var store = new RelayMessageStore(CreateOptions(dataDirectory), telemetry, callsClient: client);

				var reply = await store.SaveAsync(new FakeSessionContext(), null, new ReadOnlySequence<byte>(Array.Empty<byte>()), CancellationToken.None);

				reply.ReplyCode.Should().Be(SmtpReplyCode.TransactionFailed);
				reply.Message.Should().StartWith("5.6.0");
				client.SaveCallInputs.Should().BeEmpty();

				// Nothing else is logged for these (there is no message summary), so the bounce must not be silent.
				var logged = telemetry.UnparseableMessages.Should().ContainSingle().Subject;
				logged.SizeBytes.Should().Be(0);
				logged.SmtpReply.Should().Be("554 5.6.0 The message could not be parsed");
				logged.Exception.Should().NotBeNull();
				telemetry.ReceivedMessages.Should().BeEmpty();
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		private async Task<SmtpResponse> ProcessFailingCallAsync(Exception exception)
		{
			var dataDirectory = CreateTempDirectory();
			try
			{
				var telemetry = new FakeSmtpTelemetry();
				var store = new RelayMessageStore(CreateOptions(dataDirectory), telemetry, callsClient: new FakeResgridCallsClient { SaveCallException = exception });
				var reply = await store.SaveAsync(new FakeSessionContext(), null,
					CreateMessageBuffer($"msg-{Guid.NewGuid():N}@cad.example", "sender@example.com", new[] { "abc123@dispatch.resgrid.com" }, "Structure Fire", "First line"),
					CancellationToken.None);

				telemetry.FailedMessages.Should().ContainSingle();
				telemetry.FailedMessages[0].Message.SmtpReplyCode.Should().Be((int)reply.ReplyCode);
				return reply;
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		// The token endpoint answering 400 (invalid_grant) is a credential an operator can fix, not Resgrid rejecting the dispatch.
		[TestCase(HttpStatusCode.BadRequest)]
		[TestCase(HttpStatusCode.NotFound)]
		[TestCase(null)]
		public async Task SaveAsync_Should_Ask_The_Sender_To_Retry_When_Authentication_Fails(HttpStatusCode? status)
		{
			var reply = await ProcessFailingCallAsync(new ResgridAuthenticationException("The Resgrid token request failed", status));

			reply.ReplyCode.Should().Be(SmtpReplyCode.Aborted);
		}

		[Test]
		public async Task SaveAsync_Should_Route_A_Dispatch_Address_That_Is_Only_On_The_Envelope()
		{
			var dataDirectory = CreateTempDirectory();
			try
			{
				// A CAD system Bccs the dispatch address, or a rule forwards the message: the headers name someone else entirely.
				var client = new FakeResgridCallsClient();
				var telemetry = new FakeSmtpTelemetry();
				var store = new RelayMessageStore(CreateOptions(dataDirectory), telemetry, callsClient: client);
				var transaction = new FakeMessageTransaction("abc123@dispatch.resgrid.com");

				var reply = await store.SaveAsync(new FakeSessionContext(), transaction,
					CreateMessageBuffer("msg-bcc@cad.example", "cad@county.example", new[] { "dispatchers@county.example" }, "Structure Fire", "123 Main St"),
					CancellationToken.None);

				reply.ReplyCode.Should().Be(SmtpResponse.Ok.ReplyCode);
				client.SaveCallInputs.Should().ContainSingle();
				telemetry.UnroutableMessages.Should().BeEmpty();
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		[Test]
		public async Task SaveAsync_Should_Turn_An_Html_Only_Body_Into_Text()
		{
			var dataDirectory = CreateTempDirectory();
			try
			{
				var message = new MimeMessage { MessageId = "msg-html@cad.example", Subject = "Dispatch" };
				message.From.Add(MailboxAddress.Parse("cad@county.example"));
				message.To.Add(MailboxAddress.Parse("abc123@dispatch.resgrid.com"));
				message.Body = new TextPart("html")
				{
					Text = "<html><head><style>p{color:red}</style></head><body><p>STRUCTURE FIRE &amp; RESCUE</p><div>123 Main&nbsp;St</div><script>x()</script></body></html>"
				};

				using var stream = new MemoryStream();
				message.WriteTo(stream);

				var client = new FakeResgridCallsClient();
				var store = new RelayMessageStore(CreateOptions(dataDirectory), new FakeSmtpTelemetry(), callsClient: client);
				await store.SaveAsync(new FakeSessionContext(), null, new ReadOnlySequence<byte>(stream.ToArray()), CancellationToken.None);

				var call = client.SaveCallInputs.Should().ContainSingle().Subject;
				call.Nature.Should().Be("STRUCTURE FIRE & RESCUE");
				call.Note.Should().Contain("123 Main St").And.NotContain("<").And.NotContain("color:red").And.NotContain("x()");
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		[Test]
		public async Task SaveAsync_Should_Keep_A_Raw_Copy_Of_A_Message_With_A_Very_Long_Message_Id()
		{
			var dataDirectory = CreateTempDirectory();
			try
			{
				var options = CreateOptions(dataDirectory);
				options.SaveRawMessages = true;
				var client = new FakeResgridCallsClient();
				var store = new RelayMessageStore(options, new FakeSmtpTelemetry(), callsClient: client);

				var reply = await store.SaveAsync(new FakeSessionContext(), null,
					CreateMessageBuffer(new string('x', 400) + "@cad.example", "cad@county.example", new[] { "abc123@dispatch.resgrid.com" }, "Dispatch", "Body"),
					CancellationToken.None);

				reply.ReplyCode.Should().Be(SmtpResponse.Ok.ReplyCode, "a file name past the filesystem limit would fail every redelivery");
				client.SaveCallInputs.Should().ContainSingle();
				Directory.GetFiles(Path.Combine(dataDirectory, "messages"), "*.eml").Should().ContainSingle()
					.Which.Length.Should().BeLessThan(260);
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		[Test]
		public async Task SaveAsync_Should_Bounce_A_Department_Address_Whose_Code_Is_Not_A_Department_Dispatch_Code()
		{
			// Without this check anyone who can reach the relay pages a whole department by mailing any@dispatch-domain.
			var (reply, client, _, telemetry) = await ProcessWithLookupsAsync(new FakeLookupsApi(), usesSystemApiKey: false, "guess@dispatch.resgrid.com");

			reply.ReplyCode.Should().Be(SmtpReplyCode.MailboxUnavailable);
			client.SaveCallInputs.Should().BeEmpty();
			telemetry.UnroutableMessages.Should().ContainSingle()
				.Which.UnresolvedTargets.Should().ContainSingle().Which.Should().Contain("guess");
		}

		[Test]
		public async Task SaveAsync_Should_Page_The_Whole_Department_For_A_Valid_Department_Code()
		{
			var lookups = new FakeLookupsApi { Departments = { ["abc123"] = "42" } };
			var (reply, client, _, _) = await ProcessWithLookupsAsync(lookups, usesSystemApiKey: false, "abc123@dispatch.resgrid.com");

			reply.ReplyCode.Should().Be(SmtpResponse.Ok.ReplyCode);
			var call = client.SaveCallInputs.Should().ContainSingle().Subject;
			call.DispatchList.Should().Be("0");
			call.IsPending.Should().BeNull();
		}

		[Test]
		public async Task SaveAsync_Should_Save_A_Pending_Call_For_A_Department_Address_With_The_System_Key()
		{
			// Resgrid never lets the system key page a department by itself: "0" on an immediate call pages nobody.
			var lookups = new FakeLookupsApi { Departments = { ["abc123"] = "42" } };
			var (reply, client, _, telemetry) = await ProcessWithLookupsAsync(lookups, usesSystemApiKey: true, "abc123@dispatch.resgrid.com");

			reply.ReplyCode.Should().Be(SmtpResponse.Ok.ReplyCode);
			var call = client.SaveCallInputs.Should().ContainSingle().Subject;
			call.DispatchList.Should().Be("0");
			call.IsPending.Should().BeTrue();
			call.DepartmentId.Should().Be("42", "with the system key the department comes from the code");
			telemetry.ProcessedMessages.Should().ContainSingle().Which.CreatedAsPending.Should().BeTrue();
		}

		[Test]
		public async Task SaveAsync_Should_Dispatch_A_Hosted_Group_Address_To_The_Group_In_The_Subdomain_Department()
		{
			var lookups = new FakeLookupsApi { Groups = { [("station5", "42")] = "7" } };
			var (reply, client, _, _) = await ProcessWithLookupsAsync(lookups, usesSystemApiKey: true, "station5@42.groups.resgrid.com",
				options => options.HostedMode = true);

			reply.ReplyCode.Should().Be(SmtpResponse.Ok.ReplyCode);
			var call = client.SaveCallInputs.Should().ContainSingle().Subject;
			call.DispatchList.Should().Be("G:7");
			call.DepartmentId.Should().Be("42");
			call.IsPending.Should().BeNull();
			lookups.GroupLookupDepartments.Should().Equal("42");
		}

		[Test]
		public async Task SaveAsync_Should_Not_Dispatch_A_Group_With_The_System_Key_When_No_Department_Is_Known()
		{
			var lookups = new FakeLookupsApi { Groups = { [("station5", null)] = "7" } };
			var (reply, client, _, _) = await ProcessWithLookupsAsync(lookups, usesSystemApiKey: true, "station5@groups.resgrid.com");

			reply.ReplyCode.Should().Be(SmtpReplyCode.MailboxUnavailable);
			client.SaveCallInputs.Should().BeEmpty();
		}

		[Test]
		public async Task SaveAsync_Should_Drop_A_Department_Code_From_Another_Department_Than_The_Configured_One()
		{
			var lookups = new FakeLookupsApi { Departments = { ["abc123"] = "42", ["xyz789"] = "99" } };
			var (_, client, _, _) = await ProcessWithLookupsAsync(lookups, usesSystemApiKey: true, "xyz789@dispatch.resgrid.com",
				options => options.DefaultDepartmentId = "42");

			client.SaveCallInputs.Should().BeEmpty();
		}

		[Test]
		public async Task SaveAsync_Should_Send_A_Group_Message_For_A_Group_Message_Address()
		{
			var lookups = new FakeLookupsApi { MessageGroups = { ["crew"] = "11" } };
			var (reply, client, messages, telemetry) = await ProcessWithLookupsAsync(lookups, usesSystemApiKey: false, "crew@gm.resgrid.com");

			reply.ReplyCode.Should().Be(SmtpResponse.Ok.ReplyCode);
			client.SaveCallInputs.Should().BeEmpty("a group-message address is a message, not a call");
			var sent = messages.Sent.Should().ContainSingle().Subject;
			sent.Title.Should().Be("Dispatch");
			sent.Recipients.Should().ContainSingle().Which.Id.Should().Be("G:11");
			sent.Recipients[0].Name.Should().BeNullOrEmpty("a recipient named \"Everyone\" would message the whole department");
			telemetry.ProcessedMessages.Should().ContainSingle().Which.GroupMessageId.Should().Be("message-1");
		}

		[TestCase("list@lists.resgrid.com", false, false)]
		[TestCase("crew@gm.resgrid.com", false, true)]
		[TestCase("crew@gm.resgrid.com", true, false)]
		[TestCase("abc123@dispatch.resgrid.com", true, true)]
		[TestCase("station5@groups.resgrid.com", true, true)]
		public async Task MailboxFilter_Should_Only_Accept_Recipients_The_Relay_Can_Deliver(string recipient, bool usesSystemApiKey, bool expected)
		{
			var options = CreateOptions(Path.GetTempPath());
			options.GroupMessageAddressDomains = new[] { "gm.resgrid.com" };
			options.ListAddressDomains = new[] { "lists.resgrid.com" };
			var filter = new RelayMailboxFilter(options, new FakeSmtpTelemetry(), usesSystemApiKey);
			var parts = recipient.Split('@');

			var accepted = await filter.CanDeliverToAsync(new FakeSessionContext(), new Mailbox(parts[0], parts[1]), new Mailbox("cad", "county.example"), CancellationToken.None);

			accepted.Should().Be(expected);
		}

		private static async Task<(SmtpResponse Reply, FakeResgridCallsClient Calls, FakeMessagesClient Messages, FakeSmtpTelemetry Telemetry)> ProcessWithLookupsAsync(
			FakeLookupsApi lookups, bool usesSystemApiKey, string recipient, Action<SmtpRelayOptions> configure = null)
		{
			var dataDirectory = CreateTempDirectory();
			try
			{
				var options = CreateOptions(dataDirectory);
				options.GroupMessageAddressDomains = new[] { "gm.resgrid.com" };
				configure?.Invoke(options);

				var client = new FakeResgridCallsClient();
				var messages = new FakeMessagesClient();
				var telemetry = new FakeSmtpTelemetry();
				var store = new RelayMessageStore(options, telemetry, callsClient: client, lookupsApi: lookups, messagesClient: messages, usesSystemApiKey: usesSystemApiKey);

				var reply = await store.SaveAsync(new FakeSessionContext(), new FakeMessageTransaction(recipient),
					CreateMessageBuffer($"msg-{Guid.NewGuid():N}@cad.example", "cad@county.example", new[] { recipient }, "Dispatch", "Structure fire"),
					CancellationToken.None);

				return (reply, client, messages, telemetry);
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		[TestCase("4", "4")]
		[TestCase("v4", "4")]
		[TestCase(" V4 ", "4")]
		public void NormalizeApiVersion_Should_Accept_A_Leading_V(string configured, string expected)
		{
			ResgridV4ApiClient.NormalizeApiVersion(configured).Should().Be(expected);
		}

		[TestCase("msg-1@cad.example", "msg-1@cad.example")]
		[TestCase("  msg-2@cad.example  ", "msg-2@cad.example")]
		public void IdempotencyHeaders_Should_Send_A_Plain_Key_As_Is(string key, string expected)
		{
			CallsApi.IdempotencyHeaders(key).Should().ContainKey(CallsApi.IdempotencyKeyHeader)
				.WhoseValue.Should().Be(expected);
		}

		[Test]
		public void IdempotencyHeaders_Should_Hash_A_Key_That_Is_Not_A_Valid_Header_Value()
		{
			var internationalized = CallsApi.IdempotencyHeaders("dispatch-é@cad.example")[CallsApi.IdempotencyKeyHeader];
			var withSpace = CallsApi.IdempotencyHeaders("two words")[CallsApi.IdempotencyKeyHeader];
			var tooLong = CallsApi.IdempotencyHeaders(new string('a', 300))[CallsApi.IdempotencyKeyHeader];

			internationalized.Should().StartWith("sha256:").And.HaveLength(7 + 64);
			withSpace.Should().StartWith("sha256:");
			tooLong.Should().StartWith("sha256:");
			CallsApi.IdempotencyHeaders("dispatch-é@cad.example")[CallsApi.IdempotencyKeyHeader].Should().Be(internationalized, "the same key hashes the same on every retry");
		}

		[TestCase(null)]
		[TestCase("   ")]
		public void IdempotencyHeaders_Should_Send_Nothing_Without_A_Key(string key)
		{
			CallsApi.IdempotencyHeaders(key).Should().BeNull();
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

		private static ReadOnlySequence<byte> CreateMessageBuffer(string messageId, string from, IEnumerable<string> to, string subject, string body,
			params (string Name, byte[] Data)[] attachments)
		{
			var builder = new BodyBuilder { TextBody = body };
			foreach (var attachment in attachments)
			{
				builder.Attachments.Add(attachment.Name, attachment.Data);
			}

			var message = new MimeMessage
			{
				MessageId = messageId,
				Subject = subject,
				Body = builder.ToMessageBody()
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
			public List<string> SaveCallKeys { get; } = new List<string>();
			public List<string> SaveCallFileKeys { get; } = new List<string>();
			public Exception SaveCallException { get; set; }
			public Exception NextSaveCallFileException { get; set; }
			public string NextCallId { get; set; } = "call-1";
			public string CurrentUserId { get; set; } = "fake-user-id";

			/// <summary>Awaited before each SaveCall, to hold a dispatch in progress.</summary>
			public Func<Task> BeforeSaveCall { get; set; }

			public async Task<string> SaveCallAsync(NewCallInput call, string idempotencyKey, CancellationToken cancellationToken)
			{
				if (BeforeSaveCall != null)
					await BeforeSaveCall().ConfigureAwait(false);

				if (SaveCallException != null)
					throw SaveCallException;

				lock (SaveCallInputs)
				{
					SaveCallInputs.Add(call);
					SaveCallKeys.Add(idempotencyKey);
				}

				return NextCallId;
			}

			public Task<string> SaveCallFileAsync(SaveCallFileInput file, string idempotencyKey, CancellationToken cancellationToken)
			{
				SaveCallFileKeys.Add(idempotencyKey);

				if (NextSaveCallFileException != null)
				{
					var exception = NextSaveCallFileException;
					NextSaveCallFileException = null;
					throw exception;
				}

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
			public List<(long SizeBytes, string SmtpReply, Exception Exception)> UnparseableMessages { get; } = new List<(long, string, Exception)>();
			public List<SmtpMessageSummary> ProcessedMessages { get; } = new List<SmtpMessageSummary>();
			public List<(SmtpMessageSummary Message, Exception Exception)> FailedMessages { get; } = new List<(SmtpMessageSummary, Exception)>();
			public List<SmtpMessageSummary> DuplicateMessages { get; } = new List<SmtpMessageSummary>();
			public List<SmtpMessageSummary> InProgressMessages { get; } = new List<SmtpMessageSummary>();
			public List<(string Warning, Exception Exception)> Warnings { get; } = new List<(string, Exception)>();

			public void MessageInProgress(ISessionContext context, SmtpMessageSummary message)
			{
				InProgressMessages.Add(message);
			}

			public void ProcessingWarning(ISessionContext context, SmtpMessageSummary message, string warning, Exception exception)
			{
				lock (Warnings)
					Warnings.Add((warning, exception));
			}

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
				DuplicateMessages.Add(message);
			}

			public void UnroutableMessage(ISessionContext context, SmtpMessageSummary message)
			{
				UnroutableMessages.Add(message);
			}

			public void UnparseableMessage(ISessionContext context, long sizeBytes, string smtpReply, Exception exception)
			{
				UnparseableMessages.Add((sizeBytes, smtpReply, exception));
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

		private sealed class FakeLookupsApi : IResgridLookupsApi
		{
			public Dictionary<string, string> Departments { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			public Dictionary<(string Code, string DepartmentId), string> Groups { get; } = new Dictionary<(string, string), string>();
			public Dictionary<string, string> MessageGroups { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			public List<string> GroupLookupDepartments { get; } = new List<string>();

			/// <summary>Thrown by every department-code lookup when set.</summary>
			public Exception DepartmentLookupException { get; set; }

			public Task<DepartmentLookupResult> LookupDepartmentByDispatchCodeAsync(string code, CancellationToken cancellationToken = default)
			{
				if (DepartmentLookupException != null)
					throw DepartmentLookupException;

				return Task.FromResult(Departments.TryGetValue(code, out var id) ? new DepartmentLookupResult { DepartmentId = id, Code = code } : null);
			}

			public Task<GroupLookupResult> LookupGroupByDispatchCodeAsync(string code, string departmentId, CancellationToken cancellationToken = default)
			{
				GroupLookupDepartments.Add(departmentId);
				return Task.FromResult(Groups.TryGetValue((code, departmentId), out var id) ? new GroupLookupResult { GroupId = id, Name = code } : null);
			}

			public Task<GroupLookupResult> LookupGroupByMessageCodeAsync(string code, string departmentId, CancellationToken cancellationToken = default) =>
				Task.FromResult(MessageGroups.TryGetValue(code, out var id) ? new GroupLookupResult { GroupId = id, Name = code } : null);

			public Task<UnitLookupResult> LookupUnitByNameAsync(string name, string departmentId, CancellationToken cancellationToken = default) =>
				Task.FromResult<UnitLookupResult>(null);

			public Task<RoleLookupResult> LookupRoleByNameAsync(string name, string departmentId, CancellationToken cancellationToken = default) =>
				Task.FromResult<RoleLookupResult>(null);
		}

		private sealed class FakeMessagesClient : IResgridMessagesClient
		{
			public List<NewMessageInput> Sent { get; } = new List<NewMessageInput>();

			public Task<string> SendMessageAsync(NewMessageInput message, CancellationToken cancellationToken)
			{
				Sent.Add(message);
				return Task.FromResult($"message-{Sent.Count}");
			}
		}

		private sealed class FakeMessageTransaction : IMessageTransaction
		{
			public FakeMessageTransaction(params string[] recipients)
			{
				foreach (var recipient in recipients)
				{
					var parts = recipient.Split('@');
					To.Add(new Mailbox(parts[0], parts[1]));
				}
			}

			public IMailbox From { get; set; } = new Mailbox("cad", "county.example");
			public IList<IMailbox> To { get; } = new List<IMailbox>();
			public IReadOnlyDictionary<string, string> Parameters { get; set; } = new Dictionary<string, string>();
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
