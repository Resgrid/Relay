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
				var store = new RelayMessageStore(CreateOptions(dataDirectory), telemetry, callsClient: client, usesSystemApiKey: false);

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
				var store = new RelayMessageStore(CreateOptions(dataDirectory), new FakeSmtpTelemetry(), callsClient: client, lookupsApi: lookups, usesSystemApiKey: true);

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
				var store = new RelayMessageStore(options, telemetry, callsClient: client, lookupsApi: lookups, usesSystemApiKey: true);
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
				var store = new RelayMessageStore(options, new FakeSmtpTelemetry(), callsClient: client, lookupsApi: lookups, usesSystemApiKey: true);
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
				var store = new RelayMessageStore(CreateOptions(dataDirectory), new FakeSmtpTelemetry(), callsClient: client, lookupsApi: lookups, usesSystemApiKey: true);

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
				var store = new RelayMessageStore(options, new FakeSmtpTelemetry(), callsClient: client, lookupsApi: lookups, usesSystemApiKey: true);
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
				var store = new RelayMessageStore(options, new FakeSmtpTelemetry(), callsClient: client, lookupsApi: lookups, messagesClient: messages, usesSystemApiKey: false);
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
				var store = new RelayMessageStore(options, telemetry, callsClient: client, lookupsApi: lookups, usesSystemApiKey: true);
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
				var store = new RelayMessageStore(options, new FakeSmtpTelemetry(), callsClient: client, lookupsApi: lookups, usesSystemApiKey: true);

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
				var store = new RelayMessageStore(options, new FakeSmtpTelemetry(), callsClient: client, lookupsApi: lookups, usesSystemApiKey: true);

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
				var store = new RelayMessageStore(options, new FakeSmtpTelemetry(), callsClient: client, lookupsApi: lookups, usesSystemApiKey: true);
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
			try
			{
				var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
				var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
				var client = new FakeResgridCallsClient();
				client.BeforeSaveCall = async () =>
				{
					entered.TrySetResult(true);
					await release.Task.ConfigureAwait(false);
				};

				var telemetry = new FakeSmtpTelemetry();
				var store = new RelayMessageStore(CreateOptions(dataDirectory), telemetry, callsClient: client);
				var transaction = new FakeMessageTransaction("abc123@dispatch.resgrid.com");
				var buffer = CreateMessageBuffer("msg-concurrent@cad.example", "cad@county.example", new[] { "abc123@dispatch.resgrid.com" }, "Fire", "Body");

				var first = store.SaveAsync(new FakeSessionContext(), transaction, buffer, CancellationToken.None);
				await entered.Task;

				var concurrent = await store.SaveAsync(new FakeSessionContext(), transaction, buffer, CancellationToken.None);
				concurrent.ReplyCode.Should().Be(SmtpReplyCode.Aborted, "250 now would be a promise the first attempt may not keep");
				telemetry.InProgressMessages.Should().ContainSingle();

				// Nothing is on disk while the first attempt runs: a crash now leaves the redelivery free to dispatch.
				var afterRestart = new RelayMessageStore(CreateOptions(dataDirectory), new FakeSmtpTelemetry(), callsClient: new FakeResgridCallsClient());
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
				var store = new RelayMessageStore(CreateOptions(dataDirectory), new FakeSmtpTelemetry(), callsClient: client, lookupsApi: lookups);
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
				var store = new RelayMessageStore(options, new FakeSmtpTelemetry(), callsClient: new FakeResgridCallsClient(), lookupsApi: new FakeLookupsApi());

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
				var store = new RelayMessageStore(options, telemetry, callsClient: client);

				// A directory where the copy's file would go makes the write fail.
				Directory.CreateDirectory(Path.Combine(dataDirectory, "messages", "msg-unwritable@cad.example.eml"));

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
		public void RelayMessageStore_Should_Prune_Old_Raw_Copies_At_Startup()
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
				_ = new RelayMessageStore(options, new FakeSmtpTelemetry(), callsClient: new FakeResgridCallsClient());

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
				var store = new RelayMessageStore(CreateOptions(dataDirectory), telemetry, callsClient: client);
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
			var plain = MimeMessage.Load(new MemoryStream(CreateMessageWithoutId(date).ToArray()));
			var withMap = MimeMessage.Load(new MemoryStream(CreateMessageWithoutId(date, ("map.pdf", new byte[] { 1, 2, 3 })).ToArray()));
			var withBiggerMap = MimeMessage.Load(new MemoryStream(CreateMessageWithoutId(date, ("map.pdf", new byte[] { 1, 2, 3, 4 })).ToArray()));

			var ids = new[] { plain, withMap, withBiggerMap }.Select(RelayMessageStore.GetStableMessageId).ToList();

			ids.Should().OnlyHaveUniqueItems();
			RelayMessageStore.GetStableMessageId(MimeMessage.Load(new MemoryStream(CreateMessageWithoutId(date).ToArray()))).Should().Be(ids[0]);
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
				var store = new RelayMessageStore(CreateOptions(dataDirectory), new FakeSmtpTelemetry(), callsClient: client, lookupsApi: lookups);

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

		// ─── Helpers ───

		private static SmtpRelayOptions CreateHostedOptions(string dataDirectory)
		{
			var options = CreateOptions(dataDirectory);
			options.HostedMode = true;
			options.DepartmentDomainSeparator = ".";
			return options;
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
