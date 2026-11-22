using FluentAssertions;
using MimeKit;
using NUnit.Framework;
using Resgrid.Relay.Engine.Configuration;
using Resgrid.Relay.Engine.Smtp;
using Resgrid.Providers.ApiClient.V4.Models;
using SmtpServer.Mail;
using SmtpServer.Protocol;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Resgrid.Audio.Tests
{
	/// <summary>
	/// Routing, duplicate handling, attachments and raw copies of the SMTP relay's message store. Shares the fakes of
	/// <see cref="SmtpRelayTelemetryTests"/>.
	/// </summary>
	public partial class SmtpRelayTelemetryTests
	{
		// ─── Attachments with a token whose user id cannot be read (finding 1) ───

		[TestCase(null)]
		[TestCase("")]
		public async Task SaveAsync_Should_Upload_Attachments_For_A_Token_Caller_Without_A_User_Id(string currentUserId)
		{
			// Resgrid encrypts access tokens, so the relay cannot read the user id; Resgrid ignores UserId for a token caller anyway.
			var dataDirectory = CreateTempDirectory();
			try
			{
				var client = new FakeResgridCallsClient { CurrentUserId = currentUserId };
				var telemetry = new FakeSmtpTelemetry();
				await using var store = new RelayMessageStore(CreateOptions(dataDirectory), telemetry, callsClient: client, usesSystemApiKey: false);

				var reply = await store.SaveAsync(new FakeSessionContext(), new FakeMessageTransaction("abc123@dispatch.resgrid.com"),
					CreateMessageBuffer("msg-token-files@cad.example", "cad@county.example", new[] { "abc123@dispatch.resgrid.com" }, "Fire", "Body",
						("map.pdf", new byte[] { 1, 2, 3 })),
					CancellationToken.None);

				reply.ReplyCode.Should().Be(SmtpReplyCode.Ok);
				var file = client.SaveCallFileInputs.Should().ContainSingle().Subject;
				file.UserId.Should().NotBeNullOrWhiteSpace("Resgrid requires the field even though it ignores it for a token caller");
				telemetry.FailedMessages.Should().BeEmpty();
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		[Test]
		public async Task SaveAsync_Should_Name_The_Department_As_The_Attachment_Author_For_A_System_Caller()
		{
			var dataDirectory = CreateTempDirectory();
			try
			{
				var client = new FakeResgridCallsClient { CurrentUserId = null };
				var lookups = new FakeLookupsApi { Departments = { ["abc123"] = "42" } };
				await using var store = new RelayMessageStore(CreateOptions(dataDirectory), new FakeSmtpTelemetry(), callsClient: client, lookupsApi: lookups, usesSystemApiKey: true);

				var reply = await store.SaveAsync(new FakeSessionContext(), new FakeMessageTransaction("abc123@dispatch.resgrid.com"),
					CreateMessageBuffer("msg-system-files@cad.example", "cad@county.example", new[] { "abc123@dispatch.resgrid.com" }, "Fire", "Body",
						("map.pdf", new byte[] { 1, 2, 3 })),
					CancellationToken.None);

				reply.ReplyCode.Should().Be(SmtpReplyCode.Ok);
				client.SaveCallFileInputs.Should().ContainSingle().Which.UserId.Should().Be("42");
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		// ─── One message, several departments (finding 2) ───

		[Test]
		public async Task SaveAsync_Should_Dispatch_The_Same_Message_To_A_Second_Department_Delivered_In_Its_Own_Transaction()
		{
			// Hosted mode: each department's subdomain is its own domain, so the sender delivers each in a separate transaction
			// with the same Message-Id. The second is a different dispatch, not a duplicate.
			var dataDirectory = CreateTempDirectory();
			try
			{
				var options = CreateHostedOptions(dataDirectory);
				var lookups = new FakeLookupsApi { Groups = { [("station5", "42")] = "7", [("engine2", "99")] = "8" } };
				var client = new FakeResgridCallsClient();
				var telemetry = new FakeSmtpTelemetry();
				await using var store = new RelayMessageStore(options, telemetry, callsClient: client, lookupsApi: lookups, usesSystemApiKey: true);
				var buffer = CreateMessageBuffer("msg-two-departments@cad.example", "cad@county.example",
					new[] { "station5@42.groups.resgrid.com", "engine2@99.groups.resgrid.com" }, "Mutual aid", "Body");

				var first = await store.SaveAsync(new FakeSessionContext(), new FakeMessageTransaction("station5@42.groups.resgrid.com"), buffer, CancellationToken.None);
				var second = await store.SaveAsync(new FakeSessionContext(), new FakeMessageTransaction("engine2@99.groups.resgrid.com"), buffer, CancellationToken.None);
				var again = await store.SaveAsync(new FakeSessionContext(), new FakeMessageTransaction("engine2@99.groups.resgrid.com"), buffer, CancellationToken.None);

				first.ReplyCode.Should().Be(SmtpReplyCode.Ok);
				second.ReplyCode.Should().Be(SmtpReplyCode.Ok);
				again.ReplyCode.Should().Be(SmtpReplyCode.Ok);
				client.SaveCallInputs.Select(x => (x.DepartmentId, x.DispatchList)).Should().Equal(("42", "G:7"), ("99", "G:8"));
				telemetry.DuplicateMessages.Should().ContainSingle("only the repeat of the second department's delivery is a duplicate");
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		[Test]
		public async Task SaveAsync_Should_Dispatch_Each_Department_Accepted_In_One_Transaction_Separately()
		{
			// The RCPT filter normally keeps departments apart; when it could not (here: no filter at all), nothing is dropped.
			var dataDirectory = CreateTempDirectory();
			try
			{
				var options = CreateHostedOptions(dataDirectory);
				var lookups = new FakeLookupsApi { Groups = { [("station5", "42")] = "7", [("engine2", "99")] = "8" } };
				var client = new FakeResgridCallsClient();
				await using var store = new RelayMessageStore(options, new FakeSmtpTelemetry(), callsClient: client, lookupsApi: lookups, usesSystemApiKey: true);
				var transaction = new FakeMessageTransaction("station5@42.groups.resgrid.com", "engine2@99.groups.resgrid.com");
				var buffer = CreateMessageBuffer("msg-one-transaction@cad.example", "cad@county.example",
					new[] { "station5@42.groups.resgrid.com", "engine2@99.groups.resgrid.com" }, "Mutual aid", "Body");

				var reply = await store.SaveAsync(new FakeSessionContext(), transaction, buffer, CancellationToken.None);
				var redelivery = await store.SaveAsync(new FakeSessionContext(), transaction, buffer, CancellationToken.None);

				reply.ReplyCode.Should().Be(SmtpReplyCode.Ok);
				redelivery.ReplyCode.Should().Be(SmtpReplyCode.Ok);
				client.SaveCallInputs.Select(x => (x.DepartmentId, x.DispatchList)).Should().Equal(new[] { ("42", "G:7"), ("99", "G:8") },
					"each department gets its own call, and the redelivery is a duplicate of both");
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		[Test]
		public async Task SaveAsync_Should_Dispatch_Department_Codes_Of_Two_Departments_In_One_Transaction_Separately()
		{
			var dataDirectory = CreateTempDirectory();
			try
			{
				var lookups = new FakeLookupsApi { Departments = { ["abc123"] = "42", ["xyz789"] = "99" } };
				var client = new FakeResgridCallsClient();
				await using var store = new RelayMessageStore(CreateOptions(dataDirectory), new FakeSmtpTelemetry(), callsClient: client, lookupsApi: lookups, usesSystemApiKey: true);

				var reply = await store.SaveAsync(new FakeSessionContext(), new FakeMessageTransaction("abc123@dispatch.resgrid.com", "xyz789@dispatch.resgrid.com"),
					CreateMessageBuffer("msg-two-codes@cad.example", "cad@county.example", new[] { "abc123@dispatch.resgrid.com", "xyz789@dispatch.resgrid.com" }, "Fire", "Body"),
					CancellationToken.None);

				reply.ReplyCode.Should().Be(SmtpReplyCode.Ok);
				client.SaveCallInputs.Select(x => x.DepartmentId).Should().Equal("42", "99");
				client.SaveCallInputs.Should().OnlyContain(x => x.DispatchList == "0" && x.IsPending == true);
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		[Test]
		public async Task SaveAsync_Should_Retry_Only_What_Failed_When_A_Second_Department_Fails_For_Now()
		{
			var dataDirectory = CreateTempDirectory();
			try
			{
				var options = CreateHostedOptions(dataDirectory);
				var lookups = new FakeLookupsApi { Groups = { [("station5", "42")] = "7", [("engine2", "99")] = "8" } };
				var client = new FakeResgridCallsClient();
				await using var store = new RelayMessageStore(options, new FakeSmtpTelemetry(), callsClient: client, lookupsApi: lookups, usesSystemApiKey: true);
				var transaction = new FakeMessageTransaction("station5@42.groups.resgrid.com", "engine2@99.groups.resgrid.com");
				var buffer = CreateMessageBuffer("msg-partial@cad.example", "cad@county.example",
					new[] { "station5@42.groups.resgrid.com", "engine2@99.groups.resgrid.com" }, "Mutual aid", "Body");

				var calls = 0;
				client.BeforeSaveCall = () =>
				{
					if (++calls == 2)
						throw new HttpRequestException("unavailable", null, HttpStatusCode.ServiceUnavailable);
					return Task.CompletedTask;
				};

				(await store.SaveAsync(new FakeSessionContext(), transaction, buffer, CancellationToken.None)).ReplyCode.Should().Be(SmtpReplyCode.Aborted);
				(await store.SaveAsync(new FakeSessionContext(), transaction, buffer, CancellationToken.None)).ReplyCode.Should().Be(SmtpReplyCode.Ok);

				// The first department is sent again on the retry (Resgrid's idempotency key returns its call), so the second
				// department, which is only reached through it, is not lost behind a "duplicate".
				client.SaveCallInputs.Select(x => x.DepartmentId).Should().Equal("42", "42", "99");
				client.SaveCallKeys.Should().OnlyContain(x => x == "msg-partial@cad.example");
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		[Test]
		public async Task SaveAsync_Should_Send_A_Group_Message_Once_When_The_Sender_Splits_Recipients_By_Domain()
		{
			// The department address and the group-message address are on different domains, so the sender delivers them in two
			// transactions; both carry the same To header. The call is deduplicated by Resgrid, the message (no idempotency) here.
			var dataDirectory = CreateTempDirectory();
			try
			{
				var options = CreateOptions(dataDirectory);
				options.GroupMessageAddressDomains = new[] { "gm.resgrid.com" };
				var lookups = new FakeLookupsApi { Departments = { ["abc123"] = "42" }, MessageGroups = { ["crew"] = "11" } };
				var client = new FakeResgridCallsClient();
				var messages = new FakeMessagesClient();
				await using var store = new RelayMessageStore(options, new FakeSmtpTelemetry(), callsClient: client, lookupsApi: lookups, messagesClient: messages, usesSystemApiKey: false);
				var buffer = CreateMessageBuffer("msg-split@cad.example", "cad@county.example", new[] { "abc123@dispatch.resgrid.com", "crew@gm.resgrid.com" }, "Fire", "Body");

				(await store.SaveAsync(new FakeSessionContext(), new FakeMessageTransaction("abc123@dispatch.resgrid.com"), buffer, CancellationToken.None))
					.ReplyCode.Should().Be(SmtpReplyCode.Ok);
				(await store.SaveAsync(new FakeSessionContext(), new FakeMessageTransaction("crew@gm.resgrid.com"), buffer, CancellationToken.None))
					.ReplyCode.Should().Be(SmtpReplyCode.Ok);

				client.SaveCallKeys.Should().OnlyContain(x => x == "msg-split@cad.example");
				messages.Sent.Should().ContainSingle();
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		[Test]
		public async Task SaveAsync_Should_Message_Only_The_Groups_An_Earlier_Transaction_Did_Not_Reach()
		{
			// The sender delivers each Bcc recipient in its own transaction: the first names only crew22 (the To header), the
			// second adds the Bcc'd crew11. crew22 has its message already; only crew11 gets one now.
			var dataDirectory = CreateTempDirectory();
			try
			{
				var options = CreateOptions(dataDirectory);
				options.GroupMessageAddressDomains = new[] { "gm.resgrid.com" };
				var lookups = new FakeLookupsApi { MessageGroups = { ["crew11"] = "11", ["crew22"] = "22" } };
				var messages = new FakeMessagesClient();
				await using var store = new RelayMessageStore(options, new FakeSmtpTelemetry(), callsClient: new FakeResgridCallsClient(), lookupsApi: lookups,
					messagesClient: messages, usesSystemApiKey: false);
				var buffer = CreateMessageBuffer("msg-bcc-groups@cad.example", "cad@county.example", new[] { "crew22@gm.resgrid.com" }, "Fire", "Body");

				(await store.SaveAsync(new FakeSessionContext(), new FakeMessageTransaction("crew22@gm.resgrid.com"), buffer, CancellationToken.None))
					.ReplyCode.Should().Be(SmtpReplyCode.Ok);
				(await store.SaveAsync(new FakeSessionContext(), new FakeMessageTransaction("crew11@gm.resgrid.com"), buffer, CancellationToken.None))
					.ReplyCode.Should().Be(SmtpReplyCode.Ok);
				(await store.SaveAsync(new FakeSessionContext(), new FakeMessageTransaction("crew11@gm.resgrid.com"), buffer, CancellationToken.None))
					.ReplyCode.Should().Be(SmtpReplyCode.Ok);

				messages.Sent.Select(x => String.Join(",", x.Recipients.Select(r => r.Id))).Should().Equal(new[] { "G:22", "G:11" },
					"every group gets the message once, however the transactions overlap");
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		[Test]
		public async Task SaveAsync_Should_Bounce_An_Accepted_Group_Message_Address_Whose_Message_Resgrid_Rejects_Even_When_A_Call_Went_Out()
		{
			// The envelope holds only the group-message address; the department address is in the To header (its own transaction
			// dispatches it), which puts this transaction on the call path too. The call works, the message is rejected: the
			// recipient this transaction was accepted for got nothing, so a 250 would hide it.
			var dataDirectory = CreateTempDirectory();
			try
			{
				var options = CreateOptions(dataDirectory);
				options.GroupMessageAddressDomains = new[] { "gm.resgrid.com" };
				var lookups = new FakeLookupsApi { Departments = { ["abc123"] = "42" }, MessageGroups = { ["crew"] = "11" } };
				var client = new FakeResgridCallsClient();
				var messages = new FakeMessagesClient { SendException = new HttpRequestException("rejected", null, HttpStatusCode.UnprocessableEntity) };
				var telemetry = new FakeSmtpTelemetry();
				await using var store = new RelayMessageStore(options, telemetry, callsClient: client, lookupsApi: lookups, messagesClient: messages, usesSystemApiKey: false);
				var transaction = new FakeMessageTransaction("crew@gm.resgrid.com");
				var buffer = CreateMessageBuffer("msg-gm-rejected@cad.example", "cad@county.example", new[] { "abc123@dispatch.resgrid.com", "crew@gm.resgrid.com" }, "Fire", "Body");

				var reply = await store.SaveAsync(new FakeSessionContext(), transaction, buffer, CancellationToken.None);

				reply.ReplyCode.Should().Be(SmtpReplyCode.TransactionFailed);
				reply.Message.Should().StartWith("5.6.0").And.Contain("group message").And.Contain("422");
				client.SaveCallInputs.Should().ContainSingle();
				telemetry.FailedMessages.Should().ContainSingle().Which.Message.UnresolvedTargets.Should().ContainSingle().Which.Should().Contain("crew");

				// A redelivery gets the same answer; the call comes back from Resgrid's idempotency key.
				(await store.SaveAsync(new FakeSessionContext(), transaction, buffer, CancellationToken.None)).ReplyCode.Should().Be(SmtpReplyCode.TransactionFailed);
				client.SaveCallKeys.Should().OnlyContain(x => x == "msg-gm-rejected@cad.example");
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		[Test]
		public async Task SaveAsync_Should_Still_Accept_A_Call_Whose_Header_Only_Group_Message_Address_Resgrid_Rejects()
		{
			// The group-message address is only in the headers: this transaction was accepted for the department address alone,
			// which got its call. The rejected message is reported, not bounced.
			var dataDirectory = CreateTempDirectory();
			try
			{
				var options = CreateOptions(dataDirectory);
				options.GroupMessageAddressDomains = new[] { "gm.resgrid.com" };
				var lookups = new FakeLookupsApi { Departments = { ["abc123"] = "42" }, MessageGroups = { ["crew"] = "11" } };
				var client = new FakeResgridCallsClient();
				var messages = new FakeMessagesClient { SendException = new HttpRequestException("rejected", null, HttpStatusCode.UnprocessableEntity) };
				var telemetry = new FakeSmtpTelemetry();
				await using var store = new RelayMessageStore(options, telemetry, callsClient: client, lookupsApi: lookups, messagesClient: messages, usesSystemApiKey: false);

				var reply = await store.SaveAsync(new FakeSessionContext(), new FakeMessageTransaction("abc123@dispatch.resgrid.com"),
					CreateMessageBuffer("msg-gm-header@cad.example", "cad@county.example", new[] { "abc123@dispatch.resgrid.com", "crew@gm.resgrid.com" }, "Fire", "Body"),
					CancellationToken.None);

				reply.ReplyCode.Should().Be(SmtpReplyCode.Ok);
				client.SaveCallInputs.Should().ContainSingle();
				telemetry.ProcessedMessages.Should().ContainSingle().Which.UnresolvedTargets.Should().ContainSingle().Which.Should().Contain("message rejected");
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		[Test]
		public async Task SaveAsync_Should_Dispatch_A_Header_Address_Of_The_Default_Department_Next_To_A_Bare_Envelope_Code()
		{
			// The bare group code names no department of its own; DefaultDepartmentId is the dispatch's department, so a hosted
			// header address of that department belongs to it, and one of another department does not.
			var dataDirectory = CreateTempDirectory();
			try
			{
				var options = CreateHostedOptions(dataDirectory);
				options.DefaultDepartmentId = "42";
				var lookups = new FakeLookupsApi { Groups = { [("station5", "42")] = "7", [("ladder1", "42")] = "8", [("engine2", "99")] = "9" } };
				var client = new FakeResgridCallsClient();
				await using var store = new RelayMessageStore(options, new FakeSmtpTelemetry(), callsClient: client, lookupsApi: lookups, usesSystemApiKey: true);

				var reply = await store.SaveAsync(new FakeSessionContext(), new FakeMessageTransaction("station5@groups.resgrid.com"),
					CreateMessageBuffer("msg-default-header@cad.example", "cad@county.example",
						new[] { "station5@groups.resgrid.com", "ladder1@42.groups.resgrid.com", "engine2@99.groups.resgrid.com" }, "Fire", "Body"),
					CancellationToken.None);

				reply.ReplyCode.Should().Be(SmtpReplyCode.Ok);
				var call = client.SaveCallInputs.Should().ContainSingle().Subject;
				call.DepartmentId.Should().Be("42");
				call.DispatchList.Should().Be("G:7|G:8");
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		[Test]
		public async Task SaveAsync_Should_Bounce_A_Hosted_Envelope_Address_Of_Another_Department_Than_The_Default_One()
		{
			// Refused at RCPT TO already; should one still be accepted, it is bounced as not served rather than dispatched as the
			// default department (whose group of the same code would be paged) or dropped with a 250.
			var dataDirectory = CreateTempDirectory();
			try
			{
				var options = CreateHostedOptions(dataDirectory);
				options.DefaultDepartmentId = "42";
				var lookups = new FakeLookupsApi { Groups = { [("engine2", "42")] = "7", [("engine2", "99")] = "9" } };
				var client = new FakeResgridCallsClient();
				await using var store = new RelayMessageStore(options, new FakeSmtpTelemetry(), callsClient: client, lookupsApi: lookups, usesSystemApiKey: true);

				var reply = await store.SaveAsync(new FakeSessionContext(), new FakeMessageTransaction("engine2@99.groups.resgrid.com"),
					CreateMessageBuffer("msg-other-subdomain@cad.example", "cad@county.example", new[] { "engine2@99.groups.resgrid.com" }, "Fire", "Body"),
					CancellationToken.None);

				reply.ReplyCode.Should().Be(SmtpReplyCode.MailboxUnavailable);
				reply.Message.Should().Contain("does not dispatch for");
				client.SaveCallInputs.Should().BeEmpty();
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		[TestCase("<html><head><title>CAD page</title><meta charset=\"utf-8\"><body><p>STRUCTURE FIRE</p><div>123 Main St</div></body></html>")]
		[TestCase("<html><head><title>CAD page</title><style>p{color:red}</style><p>STRUCTURE FIRE</p><div>123 Main St</div></html>")]
		[TestCase("<head><title>CAD page</title>STRUCTURE FIRE<br>123 Main St")]
		public async Task SaveAsync_Should_Keep_The_Body_Of_Html_That_Leaves_Out_The_End_Of_Its_Head(string html)
		{
			// HTML lets a page leave out </head> (and <body>); the head then ends at the first element or text that cannot be in it.
			var dataDirectory = CreateTempDirectory();
			try
			{
				var message = new MimeMessage { MessageId = $"msg-{Guid.NewGuid():N}@cad.example", Subject = "Dispatch" };
				message.From.Add(MailboxAddress.Parse("cad@county.example"));
				message.To.Add(MailboxAddress.Parse("abc123@dispatch.resgrid.com"));
				message.Body = new TextPart("html") { Text = html };

				using var stream = new MemoryStream();
				message.WriteTo(stream);

				var client = new FakeResgridCallsClient();
				await using var store = new RelayMessageStore(CreateOptions(dataDirectory), new FakeSmtpTelemetry(), callsClient: client);
				await store.SaveAsync(new FakeSessionContext(), null, new ReadOnlySequence<byte>(stream.ToArray()), CancellationToken.None);

				var call = client.SaveCallInputs.Should().ContainSingle().Subject;
				call.Nature.Should().Be("STRUCTURE FIRE");
				call.Note.Should().Contain("123 Main St").And.NotContain("CAD page").And.NotContain("color:red").And.NotContain("(No message body)");
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		[Test]
		public async Task MailboxFilter_Should_Defer_A_Recipient_Of_Another_Department_Until_The_Next_Transaction()
		{
			var options = CreateHostedOptions(Path.GetTempPath());
			var telemetry = new FakeSmtpTelemetry();
			var filter = new RelayMailboxFilter(options, telemetry, usesSystemApiKey: true);
			var context = new FakeSessionContext();
			var sender = new Mailbox("cad", "county.example");

			(await filter.CanAcceptFromAsync(context, sender, 0, CancellationToken.None)).Should().BeTrue();
			(await filter.CanDeliverToAsync(context, new Mailbox("station5", "42.groups.resgrid.com"), sender, CancellationToken.None)).Should().BeTrue();
			(await filter.CanDeliverToAsync(context, new Mailbox("ladder1", "42.groups.resgrid.com"), sender, CancellationToken.None)).Should().BeTrue();

			Func<Task> otherDepartment = () => filter.CanDeliverToAsync(context, new Mailbox("engine2", "99.groups.resgrid.com"), sender, CancellationToken.None);
			var refusal = (await otherDepartment.Should().ThrowAsync<SmtpResponseException>()).Which.Response;
			((int)refusal.ReplyCode).Should().Be(452);
			refusal.Message.Should().StartWith("4.5.3");
			telemetry.RecipientOutcomes.Last().Accepted.Should().BeFalse();

			// The sender's next transaction carries the deferred recipient on its own.
			(await filter.CanAcceptFromAsync(context, sender, 0, CancellationToken.None)).Should().BeTrue();
			(await filter.CanDeliverToAsync(context, new Mailbox("engine2", "99.groups.resgrid.com"), sender, CancellationToken.None)).Should().BeTrue();
		}

		[Test]
		public async Task MailboxFilter_Should_Defer_A_Department_Code_That_Resolves_To_Another_Department()
		{
			var options = CreateOptions(Path.GetTempPath());
			var departments = new Dictionary<string, string> { ["abc123"] = "42", ["xyz789"] = "99", ["def456"] = "42" };
			var filter = new RelayMailboxFilter(options, new FakeSmtpTelemetry(), usesSystemApiKey: true,
				(code, _) => Task.FromResult(departments.TryGetValue(code, out var id) ? id : null));
			var context = new FakeSessionContext();
			var sender = new Mailbox("cad", "county.example");

			await filter.CanAcceptFromAsync(context, sender, 0, CancellationToken.None);
			(await filter.CanDeliverToAsync(context, new Mailbox("abc123", "dispatch.resgrid.com"), sender, CancellationToken.None)).Should().BeTrue();
			(await filter.CanDeliverToAsync(context, new Mailbox("station5", "groups.resgrid.com"), sender, CancellationToken.None)).Should().BeTrue("a group code names no department of its own");
			(await filter.CanDeliverToAsync(context, new Mailbox("def456", "dispatch.resgrid.com"), sender, CancellationToken.None)).Should().BeTrue();
			(await filter.CanDeliverToAsync(context, new Mailbox("unknown", "dispatch.resgrid.com"), sender, CancellationToken.None)).Should().BeTrue("an unknown code is bounced at DATA");

			Func<Task> otherDepartment = () => filter.CanDeliverToAsync(context, new Mailbox("xyz789", "dispatch.resgrid.com"), sender, CancellationToken.None);
			((int)(await otherDepartment.Should().ThrowAsync<SmtpResponseException>()).Which.Response.ReplyCode).Should().Be(452);
		}

		[Test]
		public async Task MailboxFilter_Should_Refuse_A_Recipient_Of_Another_Department_Than_The_Default_One()
		{
			// DefaultDepartmentId is the only department the relay dispatches, so a transaction of its own would not serve the
			// recipient either: 452 would have the sender retry it for days, never delivered and never bounced.
			var options = CreateOptions(Path.GetTempPath());
			options.DefaultDepartmentId = "42";
			var departments = new Dictionary<string, string> { ["abc123"] = "42", ["xyz789"] = "99" };
			var telemetry = new FakeSmtpTelemetry();
			var filter = new RelayMailboxFilter(options, telemetry, usesSystemApiKey: true,
				(code, _) => Task.FromResult(departments.TryGetValue(code, out var id) ? id : null));
			var context = new FakeSessionContext();
			var sender = new Mailbox("cad", "county.example");
			Func<Task> otherDepartment = () => filter.CanDeliverToAsync(context, new Mailbox("xyz789", "dispatch.resgrid.com"), sender, CancellationToken.None);

			await filter.CanAcceptFromAsync(context, sender, 0, CancellationToken.None);
			var alone = (await otherDepartment.Should().ThrowAsync<SmtpResponseException>("it is the transaction's only recipient")).Which.Response;
			((int)alone.ReplyCode).Should().Be(550);
			alone.Message.Should().StartWith("5.1.1");
			telemetry.RecipientOutcomes.Last().Accepted.Should().BeFalse();
			telemetry.RecipientOutcomes.Last().Reason.Should().Contain("DefaultDepartmentId").And.Contain("550");

			(await filter.CanDeliverToAsync(context, new Mailbox("abc123", "dispatch.resgrid.com"), sender, CancellationToken.None)).Should().BeTrue();
			((int)(await otherDepartment.Should().ThrowAsync<SmtpResponseException>()).Which.Response.ReplyCode).Should().Be(550,
				"next to the default department's recipient it is no more servable than alone");

			// The sender's next transaction gets the same answer: refused, not deferred again.
			await filter.CanAcceptFromAsync(context, sender, 0, CancellationToken.None);
			((int)(await otherDepartment.Should().ThrowAsync<SmtpResponseException>()).Which.Response.ReplyCode).Should().Be(550);
		}

		[Test]
		public async Task MailboxFilter_Should_Refuse_A_Hosted_Address_Of_Another_Department_Than_The_Default_One()
		{
			var options = CreateHostedOptions(Path.GetTempPath());
			options.DefaultDepartmentId = "42";
			var filter = new RelayMailboxFilter(options, new FakeSmtpTelemetry(), usesSystemApiKey: true);
			var context = new FakeSessionContext();
			var sender = new Mailbox("cad", "county.example");

			await filter.CanAcceptFromAsync(context, sender, 0, CancellationToken.None);
			Func<Task> otherDepartment = () => filter.CanDeliverToAsync(context, new Mailbox("engine2", "99.groups.resgrid.com"), sender, CancellationToken.None);
			((int)(await otherDepartment.Should().ThrowAsync<SmtpResponseException>()).Which.Response.ReplyCode).Should().Be(550);
			(await filter.CanDeliverToAsync(context, new Mailbox("station5", "42.groups.resgrid.com"), sender, CancellationToken.None)).Should().BeTrue();
		}

		[Test]
		public async Task MailboxFilter_Should_Still_Defer_The_Second_Department_Without_A_Default_Department()
		{
			// Without DefaultDepartmentId either department can be served, just not both in one transaction.
			var options = CreateOptions(Path.GetTempPath());
			var departments = new Dictionary<string, string> { ["abc123"] = "42", ["xyz789"] = "99" };
			var filter = new RelayMailboxFilter(options, new FakeSmtpTelemetry(), usesSystemApiKey: true,
				(code, _) => Task.FromResult(departments.TryGetValue(code, out var id) ? id : null));
			var context = new FakeSessionContext();
			var sender = new Mailbox("cad", "county.example");

			await filter.CanAcceptFromAsync(context, sender, 0, CancellationToken.None);
			(await filter.CanDeliverToAsync(context, new Mailbox("xyz789", "dispatch.resgrid.com"), sender, CancellationToken.None)).Should().BeTrue("alone, any department is accepted");

			Func<Task> second = () => filter.CanDeliverToAsync(context, new Mailbox("abc123", "dispatch.resgrid.com"), sender, CancellationToken.None);
			var refusal = (await second.Should().ThrowAsync<SmtpResponseException>()).Which.Response;
			((int)refusal.ReplyCode).Should().Be(452);
			refusal.Message.Should().StartWith("4.5.3");

			await filter.CanAcceptFromAsync(context, sender, 0, CancellationToken.None);
			(await filter.CanDeliverToAsync(context, new Mailbox("abc123", "dispatch.resgrid.com"), sender, CancellationToken.None)).Should().BeTrue();
		}

		[Test]
		public async Task SaveAsync_Should_Bounce_A_Recipient_Of_Another_Department_Than_The_Default_One_Instead_Of_Hiding_It()
		{
			// The RCPT lookup failed, so the transaction accepted both codes. The default department's call goes out, but the other
			// department's recipient can never be dispatched by this relay; a 250 would tell the sender it had been.
			var dataDirectory = CreateTempDirectory();
			try
			{
				var options = CreateOptions(dataDirectory);
				options.DefaultDepartmentId = "42";
				var lookups = new FakeLookupsApi { Departments = { ["abc123"] = "42", ["xyz789"] = "99" } };
				var client = new FakeResgridCallsClient();
				var telemetry = new FakeSmtpTelemetry();
				await using var store = new RelayMessageStore(options, telemetry, callsClient: client, lookupsApi: lookups, usesSystemApiKey: true);
				var transaction = new FakeMessageTransaction("abc123@dispatch.resgrid.com", "xyz789@dispatch.resgrid.com");
				var buffer = CreateMessageBuffer("msg-not-served@cad.example", "cad@county.example",
					new[] { "abc123@dispatch.resgrid.com", "xyz789@dispatch.resgrid.com" }, "Mutual aid", "Body");

				var reply = await store.SaveAsync(new FakeSessionContext(), transaction, buffer, CancellationToken.None);

				reply.ReplyCode.Should().Be(SmtpReplyCode.MailboxUnavailable);
				reply.Message.Should().StartWith("5.1.1").And.Contain("does not dispatch for");
				client.SaveCallInputs.Should().ContainSingle().Which.DepartmentId.Should().Be("42");
				var unroutable = telemetry.UnroutableMessages.Should().ContainSingle().Subject;
				unroutable.SmtpReplyCode.Should().Be(550);
				unroutable.UnresolvedTargets.Should().ContainSingle().Which.Should().Contain("xyz789").And.Contain("different department");

				// The 550 never reached the sender (the connection dropped), so it sends the same envelope again: it must get the
				// same bounce, not a 250 for the default department's "duplicate" that hides the unserved recipient.
				var redelivery = await store.SaveAsync(new FakeSessionContext(), transaction, buffer, CancellationToken.None);

				redelivery.ReplyCode.Should().Be(SmtpReplyCode.MailboxUnavailable);
				redelivery.Message.Should().Contain("does not dispatch for");
				client.SaveCallKeys.Should().Equal(new[] { "msg-not-served@cad.example", "msg-not-served@cad.example" },
					"the default department's pass runs again, and Resgrid's idempotency key returns the call it already saved");
				telemetry.DuplicateMessages.Should().BeEmpty();
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		[Test]
		public async Task SaveAsync_Should_Bounce_A_Lone_Recipient_Of_Another_Department_Than_The_Default_One_Even_When_The_Headers_Name_Another()
		{
			// The default department's code is only in the To header (its own transaction dispatches it): it must not turn the
			// unservable envelope recipient into a 250.
			var dataDirectory = CreateTempDirectory();
			try
			{
				var options = CreateOptions(dataDirectory);
				options.DefaultDepartmentId = "42";
				var lookups = new FakeLookupsApi { Departments = { ["abc123"] = "42", ["xyz789"] = "99" } };
				var client = new FakeResgridCallsClient();
				await using var store = new RelayMessageStore(options, new FakeSmtpTelemetry(), callsClient: client, lookupsApi: lookups, usesSystemApiKey: true);

				var reply = await store.SaveAsync(new FakeSessionContext(), new FakeMessageTransaction("xyz789@dispatch.resgrid.com"),
					CreateMessageBuffer("msg-not-served-alone@cad.example", "cad@county.example",
						new[] { "abc123@dispatch.resgrid.com", "xyz789@dispatch.resgrid.com" }, "Mutual aid", "Body"),
					CancellationToken.None);

				reply.ReplyCode.Should().Be(SmtpReplyCode.MailboxUnavailable);
				reply.Message.Should().Contain("does not dispatch for");
				client.SaveCallInputs.Should().BeEmpty();
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		[Test]
		public async Task SaveAsync_Should_Still_Accept_A_Message_Whose_Other_Recipient_Is_Only_An_Unknown_Code()
		{
			// Unchanged: an address that names no department or group does not bounce what was dispatched alongside it.
			var dataDirectory = CreateTempDirectory();
			try
			{
				var options = CreateOptions(dataDirectory);
				options.DefaultDepartmentId = "42";
				var lookups = new FakeLookupsApi { Departments = { ["abc123"] = "42" } };
				var client = new FakeResgridCallsClient();
				await using var store = new RelayMessageStore(options, new FakeSmtpTelemetry(), callsClient: client, lookupsApi: lookups, usesSystemApiKey: true);

				var reply = await store.SaveAsync(new FakeSessionContext(), new FakeMessageTransaction("abc123@dispatch.resgrid.com", "typo@dispatch.resgrid.com"),
					CreateMessageBuffer("msg-typo@cad.example", "cad@county.example",
						new[] { "abc123@dispatch.resgrid.com", "typo@dispatch.resgrid.com" }, "Fire", "Body"),
					CancellationToken.None);

				reply.ReplyCode.Should().Be(SmtpReplyCode.Ok);
				client.SaveCallInputs.Should().ContainSingle();
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		[Test]
		public async Task SaveAsync_Should_Ask_For_A_Retry_Rather_Than_Bounce_While_Another_Department_Pass_May_Still_Succeed()
		{
			// A pass that fails for now still wins over the bounce: the redelivery reaches the unservable recipient again.
			var dataDirectory = CreateTempDirectory();
			try
			{
				var options = CreateOptions(dataDirectory);
				options.DefaultDepartmentId = "42";
				var lookups = new FakeLookupsApi { Departments = { ["abc123"] = "42", ["xyz789"] = "99" } };
				var client = new FakeResgridCallsClient { SaveCallException = new HttpRequestException("unavailable", null, HttpStatusCode.ServiceUnavailable) };
				await using var store = new RelayMessageStore(options, new FakeSmtpTelemetry(), callsClient: client, lookupsApi: lookups, usesSystemApiKey: true);
				var transaction = new FakeMessageTransaction("abc123@dispatch.resgrid.com", "xyz789@dispatch.resgrid.com");
				var buffer = CreateMessageBuffer("msg-not-served-retry@cad.example", "cad@county.example",
					new[] { "abc123@dispatch.resgrid.com", "xyz789@dispatch.resgrid.com" }, "Mutual aid", "Body");

				(await store.SaveAsync(new FakeSessionContext(), transaction, buffer, CancellationToken.None)).ReplyCode.Should().Be(SmtpReplyCode.Aborted);

				client.SaveCallException = null;
				(await store.SaveAsync(new FakeSessionContext(), transaction, buffer, CancellationToken.None)).ReplyCode.Should().Be(SmtpReplyCode.MailboxUnavailable);
				client.SaveCallInputs.Should().ContainSingle().Which.DepartmentId.Should().Be("42");
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		[Test]
		public async Task MailboxFilter_Should_Accept_A_Recipient_When_Its_Department_Lookup_Fails()
		{
			var filter = new RelayMailboxFilter(CreateOptions(Path.GetTempPath()), new FakeSmtpTelemetry(), usesSystemApiKey: true,
				(_, _) => throw new HttpRequestException("unavailable", null, HttpStatusCode.ServiceUnavailable));
			var context = new FakeSessionContext();
			var sender = new Mailbox("cad", "county.example");

			await filter.CanAcceptFromAsync(context, sender, 0, CancellationToken.None);
			(await filter.CanDeliverToAsync(context, new Mailbox("abc123", "dispatch.resgrid.com"), sender, CancellationToken.None)).Should().BeTrue();
		}

		// ─── Duplicates recorded only after the work (finding 4) ───

		[Test]
		public async Task SaveAsync_Should_Ask_A_Concurrent_Redelivery_To_Retry_While_The_First_Is_Still_Processing()
		{
			var dataDirectory = CreateTempDirectory();
			var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			Task<SmtpResponse> first = null;
			try
			{
				var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
				var client = new FakeResgridCallsClient();
				client.BeforeSaveCall = async () =>
				{
					entered.TrySetResult(true);
					await release.Task.ConfigureAwait(false);
				};

				var telemetry = new FakeSmtpTelemetry();
				await using var store = new RelayMessageStore(CreateOptions(dataDirectory), telemetry, callsClient: client);
				try
				{
					var transaction = new FakeMessageTransaction("abc123@dispatch.resgrid.com");
					var buffer = CreateMessageBuffer("msg-concurrent@cad.example", "cad@county.example", new[] { "abc123@dispatch.resgrid.com" }, "Fire", "Body");

					first = store.SaveAsync(new FakeSessionContext(), transaction, buffer, CancellationToken.None);
					(await Task.WhenAny(entered.Task, first, Task.Delay(TimeSpan.FromSeconds(30)))).Should().BeSameAs(entered.Task,
						"the first delivery is held inside SaveCall (not finished early, not stuck before it)");

					var concurrent = await store.SaveAsync(new FakeSessionContext(), transaction, buffer, CancellationToken.None);
					concurrent.ReplyCode.Should().Be(SmtpReplyCode.Aborted, "250 now would be a promise the first attempt may not keep");
					telemetry.InProgressMessages.Should().ContainSingle();

					// Nothing is on disk while the first attempt runs: a crash now leaves the redelivery free to dispatch.
					await using (var afterRestart = new RelayMessageStore(CreateOptions(dataDirectory), new FakeSmtpTelemetry(), callsClient: new FakeResgridCallsClient()))
						(await afterRestart.SaveAsync(new FakeSessionContext(), transaction, buffer, CancellationToken.None)).ReplyCode.Should().Be(SmtpReplyCode.Ok);

					release.SetResult(true);
					(await first).ReplyCode.Should().Be(SmtpReplyCode.Ok);

					client.BeforeSaveCall = null;
					(await store.SaveAsync(new FakeSessionContext(), transaction, buffer, CancellationToken.None)).ReplyCode.Should().Be(SmtpReplyCode.Ok);
					client.SaveCallInputs.Should().ContainSingle("the last delivery came after the first had worked: a duplicate");
					telemetry.DuplicateMessages.Should().ContainSingle();
				}
				finally
				{
					// Never leave the first delivery parked (an assertion above may have failed): let it finish before the store
					// is disposed and its directory deleted.
					release.TrySetResult(true);
					if (first != null)
						await Task.WhenAny(first, Task.Delay(TimeSpan.FromSeconds(30)));
				}
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		[Test]
		public async Task SaveAsync_Should_Not_Record_A_Message_That_Was_Bounced_For_Unknown_Recipients()
		{
			var dataDirectory = CreateTempDirectory();
			try
			{
				var lookups = new FakeLookupsApi();
				var client = new FakeResgridCallsClient();
				await using var store = new RelayMessageStore(CreateOptions(dataDirectory), new FakeSmtpTelemetry(), callsClient: client, lookupsApi: lookups);
				var transaction = new FakeMessageTransaction("abc123@dispatch.resgrid.com");
				var buffer = CreateMessageBuffer("msg-new-code@cad.example", "cad@county.example", new[] { "abc123@dispatch.resgrid.com" }, "Fire", "Body");

				(await store.SaveAsync(new FakeSessionContext(), transaction, buffer, CancellationToken.None)).ReplyCode.Should().Be(SmtpReplyCode.MailboxUnavailable);

				// The department sets the code up and the CAD sends the message again.
				lookups.Departments["abc123"] = "42";
				(await store.SaveAsync(new FakeSessionContext(), transaction, buffer, CancellationToken.None)).ReplyCode.Should().Be(SmtpReplyCode.Ok);
				client.SaveCallInputs.Should().ContainSingle();
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		// ─── Raw copies (finding 6) ───

		[Test]
		public async Task SaveAsync_Should_Keep_A_Raw_Copy_Only_Of_A_Message_That_Routes()
		{
			var dataDirectory = CreateTempDirectory();
			try
			{
				var options = CreateOptions(dataDirectory);
				options.SaveRawMessages = true;
				await using var store = new RelayMessageStore(options, new FakeSmtpTelemetry(), callsClient: new FakeResgridCallsClient(), lookupsApi: new FakeLookupsApi());

				var reply = await store.SaveAsync(new FakeSessionContext(), new FakeMessageTransaction("guess@dispatch.resgrid.com"),
					CreateMessageBuffer("msg-guess@cad.example", "cad@county.example", new[] { "guess@dispatch.resgrid.com" }, "Fire", "Body"),
					CancellationToken.None);

				reply.ReplyCode.Should().Be(SmtpReplyCode.MailboxUnavailable);
				Directory.GetFiles(Path.Combine(dataDirectory, "messages"), "*.eml").Should().BeEmpty();
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		[Test]
		public async Task SaveAsync_Should_Dispatch_Even_When_The_Raw_Copy_Cannot_Be_Written()
		{
			var dataDirectory = CreateTempDirectory();
			try
			{
				var options = CreateOptions(dataDirectory);
				options.SaveRawMessages = true;
				var telemetry = new FakeSmtpTelemetry();
				var client = new FakeResgridCallsClient();
				await using var store = new RelayMessageStore(options, telemetry, callsClient: client);

				// A file where the copies' directory should be makes the write fail.
				var messages = Path.Combine(dataDirectory, "messages");
				Directory.Delete(messages, recursive: true);
				File.WriteAllText(messages, "not a directory");

				var reply = await store.SaveAsync(new FakeSessionContext(), new FakeMessageTransaction("abc123@dispatch.resgrid.com"),
					CreateMessageBuffer("msg-unwritable@cad.example", "cad@county.example", new[] { "abc123@dispatch.resgrid.com" }, "Fire", "Body"),
					CancellationToken.None);

				reply.ReplyCode.Should().Be(SmtpReplyCode.Ok);
				client.SaveCallInputs.Should().ContainSingle();
				telemetry.Warnings.Should().ContainSingle().Which.Warning.Should().Contain("raw copy");
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		[Test]
		public async Task RelayMessageStore_Should_Prune_Old_Raw_Copies_At_Startup()
		{
			var dataDirectory = CreateTempDirectory();
			try
			{
				var messages = Directory.CreateDirectory(Path.Combine(dataDirectory, "messages")).FullName;
				var old = Path.Combine(messages, "old.eml");
				var recent = Path.Combine(messages, "recent.eml");
				var notACopy = Path.Combine(messages, "notes.txt");
				foreach (var file in new[] { old, recent, notACopy })
					File.WriteAllText(file, "x");

				File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-8));
				File.SetLastWriteTimeUtc(recent, DateTime.UtcNow.AddDays(-6));
				File.SetLastWriteTimeUtc(notACopy, DateTime.UtcNow.AddDays(-30));

				var options = CreateOptions(dataDirectory);
				options.DuplicateWindowHours = 72;
				await using var store = new RelayMessageStore(options, new FakeSmtpTelemetry(), callsClient: new FakeResgridCallsClient());

				File.Exists(old).Should().BeFalse("copies are kept for the larger of the duplicate window and 7 days");
				File.Exists(recent).Should().BeTrue();
				File.Exists(notACopy).Should().BeTrue();
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		// ─── Messages without a Message-Id (finding 8) ───

		[Test]
		public async Task SaveAsync_Should_Not_Treat_The_Same_Page_Sent_On_Another_Day_As_A_Duplicate()
		{
			var dataDirectory = CreateTempDirectory();
			try
			{
				var client = new FakeResgridCallsClient();
				var telemetry = new FakeSmtpTelemetry();
				await using var store = new RelayMessageStore(CreateOptions(dataDirectory), telemetry, callsClient: client);
				var transaction = new FakeMessageTransaction("abc123@dispatch.resgrid.com");

				var monday = CreateMessageWithoutId(new DateTimeOffset(2026, 11, 16, 7, 0, 0, TimeSpan.Zero));
				var tuesday = CreateMessageWithoutId(new DateTimeOffset(2026, 11, 17, 7, 0, 0, TimeSpan.Zero));

				await store.SaveAsync(new FakeSessionContext(), transaction, monday, CancellationToken.None);
				await store.SaveAsync(new FakeSessionContext(), transaction, tuesday, CancellationToken.None);
				await store.SaveAsync(new FakeSessionContext(), transaction, tuesday, CancellationToken.None);

				client.SaveCallKeys.Should().HaveCount(2);
				client.SaveCallKeys.Distinct().Should().HaveCount(2, "each day's page is its own call");
				telemetry.DuplicateMessages.Should().ContainSingle("the same email delivered twice still is a duplicate");
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		[Test]
		public void GetStableMessageId_Should_Tell_Apart_Messages_Without_An_Id_That_Differ_Only_In_Their_Attachments()
		{
			var date = new DateTimeOffset(2026, 11, 16, 7, 0, 0, TimeSpan.Zero);
			var plain = LoadMessage(CreateMessageWithoutId(date));
			var withMap = LoadMessage(CreateMessageWithoutId(date, ("map.pdf", new byte[] { 1, 2, 3 })));
			var withBiggerMap = LoadMessage(CreateMessageWithoutId(date, ("map.pdf", new byte[] { 1, 2, 3, 4 })));
			var withAnotherMapOfTheSameSize = LoadMessage(CreateMessageWithoutId(date, ("map.pdf", new byte[] { 9, 8, 7 })));

			var ids = new[] { plain, withMap, withBiggerMap, withAnotherMapOfTheSameSize }.Select(RelayMessageStore.GetStableMessageId).ToList();

			ids.Should().OnlyHaveUniqueItems("a new map of the same name and size is a different page");
			RelayMessageStore.GetStableMessageId(LoadMessage(CreateMessageWithoutId(date))).Should().Be(ids[0]);
			RelayMessageStore.GetStableMessageId(LoadMessage(CreateMessageWithoutId(date, ("map.pdf", new byte[] { 1, 2, 3 })))).Should().Be(ids[1],
				"the same email delivered again keeps its id");
		}

		// ─── Raw copies of messages whose ids sanitize alike ───

		[Test]
		public async Task SaveAsync_Should_Keep_A_Separate_Raw_Copy_For_Message_Ids_That_Sanitize_Alike()
		{
			var dataDirectory = CreateTempDirectory();
			try
			{
				var options = CreateOptions(dataDirectory);
				options.SaveRawMessages = true;
				var telemetry = new FakeSmtpTelemetry();
				await using var store = new RelayMessageStore(options, telemetry, callsClient: new FakeResgridCallsClient());
				var transaction = new FakeMessageTransaction("abc123@dispatch.resgrid.com");

				// "/" cannot be in a file name, so both ids sanitize to "a_b@cad.example".
				foreach (var messageId in new[] { "a/b@cad.example", "a_b@cad.example", "a/b@cad.example" })
				{
					(await store.SaveAsync(new FakeSessionContext(), transaction,
						CreateMessageBuffer(messageId, "cad@county.example", new[] { "abc123@dispatch.resgrid.com" }, "Fire", $"Body of {messageId}"),
						CancellationToken.None)).ReplyCode.Should().Be(SmtpReplyCode.Ok);
				}

				var copies = Directory.GetFiles(Path.Combine(dataDirectory, "messages"), "*.eml");
				copies.Should().HaveCount(2, "the redelivery of the first message is a duplicate");
				var paths = telemetry.ProcessedMessages.Select(x => x.RawMessagePath).ToList();
				paths.Should().OnlyHaveUniqueItems();
				File.ReadAllText(paths[0]).Should().Contain("a/b@cad.example");
				File.ReadAllText(paths[1]).Should().Contain("a_b@cad.example");
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		// ─── 404 from the lookup endpoints (finding 9) ───

		[Test]
		public async Task SaveAsync_Should_Ask_The_Sender_To_Retry_When_A_Lookup_Answers_404()
		{
			var dataDirectory = CreateTempDirectory();
			try
			{
				var lookups = new FakeLookupsApi
				{
					Departments = { ["abc123"] = "42" },
					DepartmentLookupException = new HttpRequestException("not found", null, HttpStatusCode.NotFound)
				};
				var client = new FakeResgridCallsClient();
				await using var store = new RelayMessageStore(CreateOptions(dataDirectory), new FakeSmtpTelemetry(), callsClient: client, lookupsApi: lookups);

				var reply = await store.SaveAsync(new FakeSessionContext(), new FakeMessageTransaction("abc123@dispatch.resgrid.com"),
					CreateMessageBuffer("msg-404@cad.example", "cad@county.example", new[] { "abc123@dispatch.resgrid.com" }, "Fire", "Body"),
					CancellationToken.None);

				reply.ReplyCode.Should().Be(SmtpReplyCode.Aborted, "a 404 is a routing or deploy problem, not an unknown code");
				client.SaveCallInputs.Should().BeEmpty();
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		// ─── Redis lookup cache ───

		[Test]
		public async Task SaveAsync_Should_Not_Wait_For_An_Unreachable_Redis_On_The_First_Lookup()
		{
			// StackExchange.Redis waits out its whole connect timeout before ConnectAsync returns, whatever the backlog policy; the
			// dispatch must not wait with it.
			var dataDirectory = CreateTempDirectory();
			try
			{
				var options = CreateOptions(dataDirectory);
				// TEST-NET-1 (RFC 5737) is never routed, so the connect attempt runs into its timeout.
				options.RedisCache = new RedisCacheOptions { Enabled = true, ConnectionString = "192.0.2.1:6379,connectTimeout=10000" };
				var lookups = new FakeLookupsApi { Departments = { ["abc123"] = "42" } };
				var client = new FakeResgridCallsClient();
				await using var store = new RelayMessageStore(options, new FakeSmtpTelemetry(), callsClient: client, lookupsApi: lookups);

				var stopwatch = System.Diagnostics.Stopwatch.StartNew();
				var reply = await store.SaveAsync(new FakeSessionContext(), new FakeMessageTransaction("abc123@dispatch.resgrid.com"),
					CreateMessageBuffer("msg-redis-down@cad.example", "cad@county.example", new[] { "abc123@dispatch.resgrid.com" }, "Fire", "Body"),
					CancellationToken.None);
				stopwatch.Stop();

				reply.ReplyCode.Should().Be(SmtpReplyCode.Ok);
				client.SaveCallInputs.Should().ContainSingle();
				stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "the cache is skipped until Redis is up");
			}
			finally
			{
				DeleteDirectory(dataDirectory);
			}
		}

		// ─── Helpers ───

		private static SmtpRelayOptions CreateHostedOptions(string dataDirectory)
		{
			var options = CreateOptions(dataDirectory);
			options.HostedMode = true;
			options.DepartmentDomainSeparator = ".";
			return options;
		}

		private static MimeMessage LoadMessage(ReadOnlySequence<byte> buffer)
		{
			using var stream = new MemoryStream(buffer.ToArray());
			return MimeMessage.Load(stream);
		}

		private static ReadOnlySequence<byte> CreateMessageWithoutId(DateTimeOffset date, params (string Name, byte[] Data)[] attachments)
		{
			var builder = new BodyBuilder { TextBody = "Daily test page" };
			foreach (var attachment in attachments)
				builder.Attachments.Add(attachment.Name, attachment.Data);

			var message = new MimeMessage
			{
				Subject = "TEST PAGE",
				Body = builder.ToMessageBody()
			};

			message.From.Add(MailboxAddress.Parse("cad@county.example"));
			message.To.Add(MailboxAddress.Parse("abc123@dispatch.resgrid.com"));
			message.Headers.Remove(HeaderId.MessageId);
			message.Date = date;

			using var stream = new MemoryStream();
			message.WriteTo(stream);
			return new ReadOnlySequence<byte>(stream.ToArray());
		}
	}
}
