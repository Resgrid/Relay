using MimeKit;
using Resgrid.Relay.Engine.Configuration;
using Resgrid.Providers.ApiClient.V4;
using Resgrid.Providers.ApiClient.V4.Models;
using SmtpServer;
using SmtpServer.ComponentModel;
using SmtpServer.Mail;
using SmtpServer.Protocol;
using SmtpServer.Storage;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Resgrid.Relay.Engine.Smtp
{
	public static class SmtpRelayRunner
	{
		/// <summary>
		/// SmtpServer ends a session after this many commands in a row were refused with an error reply (its default is 5). Each
		/// recipient deferred with 452 because it belongs to another department counts, so a message to six or more departments
		/// would lose its session before DATA, on every retry. The count resets after any command that succeeds.
		/// </summary>
		internal const int MaxConsecutiveCommandErrors = 100;

		public static async Task RunAsync(SmtpRelayOptions options, ISmtpTelemetry telemetry, IResgridApiClient apiClient, CancellationToken cancellationToken)
		{
			if (options == null)
				throw new ArgumentNullException(nameof(options));
			if (telemetry == null)
				throw new ArgumentNullException(nameof(telemetry));

			var usesSystemApiKey = apiClient?.IsSystemCaller ?? false;
			await using var messageStore = new RelayMessageStore(options, telemetry, apiClient);

			var serviceProvider = new ServiceProvider();
			serviceProvider.Add((IMailboxFilterFactory)new RelayMailboxFilter(options, telemetry, usesSystemApiKey, messageStore.ResolveDepartmentOfCodeAsync));
			serviceProvider.Add(messageStore);

			var smtpServerOptions = new SmtpServerOptionsBuilder()
				.ServerName(options.ServerName)
				.Port(options.Port)
				.MaxMessageSize(options.MaxMessageBytes, MaxMessageSizeHandling.Strict)
				.MaxRetryCount(MaxConsecutiveCommandErrors)
				.Build();

			var smtpServer = new SmtpServer.SmtpServer(smtpServerOptions, serviceProvider);
			smtpServer.SessionCreated += (_, eventArgs) => telemetry.SessionCreated(eventArgs.Context);
			smtpServer.SessionCompleted += (_, eventArgs) => telemetry.SessionCompleted(eventArgs.Context);
			smtpServer.SessionCancelled += (_, eventArgs) => telemetry.SessionCancelled(eventArgs.Context);
			smtpServer.SessionFaulted += (_, eventArgs) => telemetry.SessionFaulted(eventArgs.Context, eventArgs.Exception);

			telemetry.RelayStarting(options);

			try
			{
				await smtpServer.StartAsync(cancellationToken).ConfigureAwait(false);
				telemetry.RelayStopped(options);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				telemetry.RelayStopped(options);
			}
			catch (Exception ex)
			{
				telemetry.RelayFaulted(options, ex);
				throw;
			}
		}
	}

	/// <summary>
	/// Both calls carry an idempotency key derived from the message's stable id. Resgrid keeps the first result for a key, so a
	/// message redelivered after a failure (the sending server retries until it gets a 250, and several relay instances may
	/// receive the same message) never creates a second call or uploads an attachment twice. While another attempt for the same
	/// key is still running Resgrid answers 409, which fails this attempt so the sender retries later.
	/// </summary>
	public interface IResgridCallsClient
	{
		string CurrentUserId { get; }
		Task<string> SaveCallAsync(NewCallInput call, string idempotencyKey, CancellationToken cancellationToken);
		Task<string> SaveCallFileAsync(SaveCallFileInput file, string idempotencyKey, CancellationToken cancellationToken);
	}

	internal sealed class ResgridCallsClient : IResgridCallsClient
	{
		private readonly IResgridApiClient _apiClient;
		private readonly CallsApi _callsApi;

		public ResgridCallsClient(IResgridApiClient apiClient)
		{
			_apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
			_callsApi = new CallsApi(apiClient);
		}

		public string CurrentUserId => _apiClient.CurrentUserId;

		public Task<string> SaveCallAsync(NewCallInput call, string idempotencyKey, CancellationToken cancellationToken)
		{
			return _callsApi.SaveCallAsync(call, idempotencyKey, cancellationToken);
		}

		public Task<string> SaveCallFileAsync(SaveCallFileInput file, string idempotencyKey, CancellationToken cancellationToken)
		{
			return _callsApi.SaveCallFileAsync(file, idempotencyKey, cancellationToken);
		}
	}

	/// <summary>Sends the broadcast message a group-message address (code@group-message-domain) asks for.</summary>
	public interface IResgridMessagesClient
	{
		Task<string> SendMessageAsync(NewMessageInput message, CancellationToken cancellationToken);
	}

	internal sealed class ResgridMessagesClient : IResgridMessagesClient
	{
		private readonly MessagesApi _messagesApi;

		public ResgridMessagesClient(IResgridApiClient apiClient)
		{
			_messagesApi = new MessagesApi(apiClient ?? throw new ArgumentNullException(nameof(apiClient)));
		}

		public Task<string> SendMessageAsync(NewMessageInput message, CancellationToken cancellationToken)
		{
			return _messagesApi.SendMessageAsync(message, cancellationToken);
		}
	}

	public sealed class AttachmentPayload
	{
		public string Name { get; set; }
		public byte[] Data { get; set; }
		public CallFileType Type { get; set; }
	}

	public sealed class RelayMessageStore : MessageStore, IAsyncDisposable
	{
		private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
		{
			WriteIndented = true
		};

		private readonly SmtpRelayOptions _options;
		private readonly SmtpDispatchAddressParser _dispatchAddressParser;
		private readonly ProcessedMessageStore _processedMessageStore;
		private readonly ISmtpTelemetry _telemetry;
		private readonly IResgridCallsClient _callsClient;
		private readonly IResgridMessagesClient _messagesClient;
		private readonly bool _usesSystemApiKey;
		private readonly CachedLookupsService _lookupService;
		private readonly IDispatchLookupCache _lookupCache;
		private readonly string _dataDirectory;
		private readonly string _messageDirectory;
		private readonly TimeSpan _rawMessageRetention;
		private readonly Timer _rawMessagePruneTimer;

		/// <summary>Raw copies are pruned at startup and then about this often.</summary>
		internal static readonly TimeSpan RawMessagePruneInterval = TimeSpan.FromDays(1);

		/// <summary>Raw copies are kept at least this long, and never for less than the duplicate window.</summary>
		internal static readonly TimeSpan MinimumRawMessageRetention = TimeSpan.FromDays(7);

		/// <summary>
		/// Resgrid requires SaveCallFile's UserId but ignores it for a token caller (the token's own user is the author), so a
		/// token caller whose user id cannot be read from its (encrypted) access token sends this instead.
		/// </summary>
		internal const string TokenCallerAttachmentAuthor = "resgrid-relay";

		public RelayMessageStore(SmtpRelayOptions options, ISmtpTelemetry telemetry, IResgridApiClient apiClient = null, IResgridCallsClient callsClient = null, IDispatchLookupCache lookupCache = null, IResgridLookupsApi lookupsApi = null,
			IResgridMessagesClient messagesClient = null, bool? usesSystemApiKey = null)
		{
			_options = options ?? throw new ArgumentNullException(nameof(options));
			_telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
			_callsClient = callsClient ?? new ResgridCallsClient(apiClient);
			_messagesClient = messagesClient ?? (apiClient != null ? new ResgridMessagesClient(apiClient) : null);
			_usesSystemApiKey = usesSystemApiKey ?? apiClient?.IsSystemCaller ?? false;
			_lookupCache = lookupCache ?? CreateLookupCache(options, apiClient?.CredentialScope);
			lookupsApi ??= apiClient != null ? new LookupsApi(apiClient) : null;
			_lookupService = new CachedLookupsService(_lookupCache, lookupsApi);
			_dispatchAddressParser = new SmtpDispatchAddressParser(options);
			_dataDirectory = ResolvePath(options.DataDirectory);
			_messageDirectory = Path.Combine(_dataDirectory, "messages");
			_processedMessageStore = new ProcessedMessageStore(Path.Combine(_dataDirectory, "processed-messages.json"), options.DuplicateWindowHours);

			var duplicateWindow = TimeSpan.FromHours(options.DuplicateWindowHours <= 0 ? 72 : options.DuplicateWindowHours);
			_rawMessageRetention = duplicateWindow > MinimumRawMessageRetention ? duplicateWindow : MinimumRawMessageRetention;

			Directory.CreateDirectory(_dataDirectory);
			Directory.CreateDirectory(_messageDirectory);

			PruneRawMessages();
			_rawMessagePruneTimer = new Timer(_ => PruneRawMessages(), null, RawMessagePruneInterval, RawMessagePruneInterval);
		}

		public async ValueTask DisposeAsync()
		{
			await _rawMessagePruneTimer.DisposeAsync().ConfigureAwait(false);

			// Closes the Redis connection the lookup cache opened, so a stop/start doesn't leak one per run.
			await _lookupCache.DisposeAsync().ConfigureAwait(false);
		}

		private static IDispatchLookupCache CreateLookupCache(SmtpRelayOptions options, string credentialScope)
		{
			if (options.RedisCache?.Enabled == true && !String.IsNullOrWhiteSpace(options.RedisCache.ConnectionString))
				return new RedisDispatchLookupCache(options.RedisCache, credentialScope);

			return new NullDispatchLookupCache();
		}

		/// <summary>
		/// The department a department dispatch code belongs to, for the RCPT filter to keep each department in its own SMTP
		/// transaction. Only a system caller can resolve another department's code (a token sees its own department only, and
		/// every recipient then belongs to it), so for anyone else, or with code validation off, this is null.
		/// </summary>
		internal async Task<string> ResolveDepartmentOfCodeAsync(string departmentCode, CancellationToken cancellationToken)
		{
			if (!_usesSystemApiKey || !_options.ValidateDepartmentCodes || !_lookupService.CanLookup)
				return null;

			var department = await _lookupService.LookupDepartmentByDispatchCodeAsync(departmentCode, cancellationToken).ConfigureAwait(false);
			return department?.DepartmentId;
		}

		/// <summary>
		/// Deletes raw message copies older than the retention (the larger of the duplicate window and 7 days). Runs at startup
		/// and about daily; never throws, since a copy that cannot be deleted now is simply tried again next time.
		/// </summary>
		private void PruneRawMessages()
		{
			try
			{
				if (!Directory.Exists(_messageDirectory))
					return;

				var cutoff = DateTime.UtcNow.Subtract(_rawMessageRetention);
				foreach (var file in Directory.EnumerateFiles(_messageDirectory, "*.eml"))
				{
					try
					{
						if (File.GetLastWriteTimeUtc(file) < cutoff)
							File.Delete(file);
					}
					catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
					{
						// In use or not ours to delete; the next run tries again.
					}
				}
			}
			catch (Exception ex)
			{
				try
				{
					_telemetry.ProcessingWarning(null, null, "old raw message copies could not be pruned", ex);
				}
				catch (Exception)
				{
					// Runs on a timer thread: nothing may escape.
				}
			}
		}

		public override async Task<SmtpResponse> SaveAsync(ISessionContext context, IMessageTransaction transaction, ReadOnlySequence<byte> buffer, CancellationToken cancellationToken)
		{
			if (buffer.Length > _options.MaxMessageBytes)
				return SmtpResponse.SizeLimitExceeded;

			await using var stream = new MemoryStream();
			var position = buffer.GetPosition(0);
			while (buffer.TryGet(ref position, out var memory))
			{
				await stream.WriteAsync(memory, cancellationToken).ConfigureAwait(false);
			}

			stream.Position = 0;
			MimeMessage message;
			try
			{
				message = await MimeMessage.LoadAsync(stream, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is ParseException || ex is FormatException)
			{
				// The same bytes would fail the same way on every redelivery.
				_telemetry.UnparseableMessage(context, buffer.Length, $"{(int)Unparseable.ReplyCode} {Unparseable.Message}", ex);
				return Unparseable;
			}

			var subject = String.IsNullOrWhiteSpace(message.Subject) ? "SMTP Email Import" : message.Subject.Trim();
			var messageBody = GetMessageBody(message);
			var inbound = new InboundMessage
			{
				Message = message,
				Raw = stream,
				Subject = subject,
				Body = messageBody,
				Nature = GetNature(messageBody, subject),
				StableMessageId = GetStableMessageId(message)
			};

			var headerRecipients = GetHeaderRecipients(message);
			var envelope = GetEnvelopeRecipients(transaction);

			// Each pass dispatches one department. Recipients the transaction accepted for another department (the RCPT filter
			// normally defers those with 452, but it cannot always tell, e.g. while a code lookup fails) are dispatched in a pass
			// of their own instead of being dropped. A pass in which every recipient belongs elsewhere (a code of another department
			// than DefaultDepartmentId) makes no progress: it ends the loop and bounces those recipients (NotServedRecipients).
			var passes = new List<DispatchPass>();
			try
			{
				IReadOnlyList<string> passRecipients = envelope;
				while (true)
				{
					var pass = await DispatchAsync(context, inbound, passRecipients, headerRecipients, reportReceived: passes.Count == 0, cancellationToken).ConfigureAwait(false);
					passes.Add(pass);

					if (pass.OtherDepartmentRecipients.Count == 0 || pass.OtherDepartmentRecipients.Count >= passRecipients.Count)
						break;

					passRecipients = pass.OtherDepartmentRecipients;
				}

				// A pass is recorded as processed only when no later pass is to be retried: a recorded pass is skipped on the
				// redelivery, and the later passes are only reached through it (it is the one that finds their recipients).
				var laterPassRetries = false;
				for (var index = passes.Count - 1; index >= 0; index--)
				{
					var pass = passes[index];
					if (pass.Recordable && !laterPassRetries)
					{
						pass.Recordable = false;
						await CompleteAsync(context, pass.Summary, pass.ProcessingKey).ConfigureAwait(false);
					}

					laterPassRetries |= IsRetryReply(pass.Reply);
				}

				return CombineReplies(passes.Select(x => x.Reply).ToList());
			}
			finally
			{
				// Not recorded: nothing was written for a dispatch that did not finish (or whose later passes did not), so its
				// redelivery is processed again.
				foreach (var pass in passes.Where(x => x.Recordable))
					_processedMessageStore.Abandon(pass.ProcessingKey);
			}
		}

		private static bool IsRetryReply(SmtpResponse reply) => (int)reply.ReplyCode >= 400 && (int)reply.ReplyCode < 500;

		/// <summary>The outcome of dispatching the message for one department's recipients.</summary>
		private sealed class DispatchPass
		{
			public SmtpResponse Reply { get; init; }
			public SmtpMessageSummary Summary { get; init; }
			public string ProcessingKey { get; init; }
			public IReadOnlyList<string> OtherDepartmentRecipients { get; init; }

			/// <summary>It was handled for good (250) and still holds its in-progress mark; it is to be recorded as processed.</summary>
			public bool Recordable { get; set; }
		}

		/// <summary>
		/// One SMTP reply for several department passes: retry (4xx) when any pass may succeed later (the passes that worked are
		/// recorded and skipped on the redelivery); otherwise bounce when any pass was rejected outright, so no department's
		/// recipient is lost without the sender hearing of it; otherwise accept when anything was dispatched. An address that names
		/// no department or group only bounces the message when nothing else in it was dispatched. A recipient of a department
		/// this relay never dispatches (<see cref="NotServedRecipients"/>) is a rejection like any other: it was accepted for a
		/// real department and no redelivery reaches it, so the bounce is the only way the sender learns it was not delivered.
		/// </summary>
		internal static SmtpResponse CombineReplies(IReadOnlyList<SmtpResponse> replies)
		{
			if (replies.Count == 1)
				return replies[0];

			var retry = replies.FirstOrDefault(IsRetryReply);
			if (retry != null)
				return retry;

			var rejected = replies.FirstOrDefault(x => (int)x.ReplyCode >= 500 && !ReferenceEquals(x, UnknownRecipients));
			if (rejected != null)
				return rejected;

			return replies.FirstOrDefault(x => x.ReplyCode == SmtpReplyCode.Ok) ?? replies[0];
		}

		/// <summary>
		/// Dispatches the message for one set of envelope recipients (all of them, or the ones a previous pass found to belong to
		/// another department), and reports the envelope recipients that belong to yet another department.
		///
		/// The dispatch is keyed by its message id plus these recipients and marked in progress while it runs: a concurrent
		/// redelivery is asked to retry (451) rather than answered 250 before this attempt is known to have worked. It is recorded
		/// as processed (by the caller) only once it has worked: a crash or failure part-way leaves nothing behind, so the
		/// redelivery is processed again, and Resgrid's idempotency key returns what was already saved.
		/// </summary>
		private async Task<DispatchPass> DispatchAsync(ISessionContext context, InboundMessage inbound,
			IReadOnlyList<string> envelope, IReadOnlyList<string> headerRecipients, bool reportReceived, CancellationToken cancellationToken)
		{
			var message = inbound.Message;
			var stableMessageId = inbound.StableMessageId;
			var subject = inbound.Subject;
			var messageBody = inbound.Body;
			var messageSummary = SmtpMessageSummary.Create(message, stableMessageId, subject, inbound.Nature, messageBody);
			if (reportReceived)
				_telemetry.MessageReceived(context, messageSummary);

			var processingKey = ProcessingKey(stableMessageId, envelope);
			var otherDepartmentRecipients = new List<string>();
			var inProgress = false;
			var processingStopwatch = Stopwatch.StartNew();

			DispatchPass Result(SmtpResponse reply, bool handled)
			{
				// A handled pass keeps its in-progress mark until the caller records it.
				var recordable = handled && inProgress;
				if (recordable)
					inProgress = false;

				return new DispatchPass
				{
					Reply = reply,
					Summary = messageSummary,
					ProcessingKey = processingKey,
					OtherDepartmentRecipients = otherDepartmentRecipients,
					Recordable = recordable
				};
			}

			try
			{
				switch (await _processedMessageStore.TryBeginAsync(processingKey, cancellationToken).ConfigureAwait(false))
				{
					case ProcessingRegistration.Duplicate:
						_telemetry.DuplicateMessage(context, messageSummary);
						return Result(SmtpResponse.Ok, handled: false);

					case ProcessingRegistration.InProgress:
						messageSummary.SetSmtpReply((int)TryAgainLater.ReplyCode, TryAgainLater.Message);
						_telemetry.MessageInProgress(context, messageSummary);
						return Result(TryAgainLater, handled: false);
				}

				inProgress = true;

				var dispatchResult = _dispatchAddressParser.ParseRecipients(envelope, headerRecipients);
				messageSummary.SetDispatchTargets(dispatchResult.DispatchCodes);
				CollectOtherDepartmentRecipients(dispatchResult, envelope, otherDepartmentRecipients);

				if (!dispatchResult.HasTargets)
				{
					_telemetry.UnroutableMessage(context, messageSummary);
					return Result(SmtpResponse.Ok, handled: true);
				}

				// Resgrid has no API for distribution lists, and the system key cannot send messages (the Messages API only takes
				// user/department tokens). The mailbox filter refuses those recipients at RCPT TO; any left (named only in the
				// headers) are dropped here and logged.
				var unsupported = dispatchResult.DispatchCodes
					.Where(x => x.Type == DispatchCodeType.DistributionList || (x.Type == DispatchCodeType.GroupMessage && _usesSystemApiKey))
					.ToList();
				if (unsupported.Count > 0)
				{
					_telemetry.UnsupportedTarget(context, messageSummary);
					foreach (var code in unsupported)
					{
						dispatchResult.DispatchCodes.Remove(code);
						messageSummary.AddUnresolvedTarget(code, "not supported");
					}
				}

				await ResolveDispatchCodesAsync(dispatchResult, messageSummary, cancellationToken).ConfigureAwait(false);
				messageSummary.SetDispatchTargets(dispatchResult.DispatchCodes);
				CollectOtherDepartmentRecipients(dispatchResult, envelope, otherDepartmentRecipients);

				if (otherDepartmentRecipients.Count > 0 && otherDepartmentRecipients.Count >= envelope.Count)
				{
					// Every recipient accepted for this pass belongs to a department other than the one it is pinned to, so no pass
					// makes progress on them, now or on a redelivery. Bounce them; dispatching what only the headers name would let a
					// 250 hide that they were never delivered.
					messageSummary.SetSmtpReply((int)NotServedRecipients.ReplyCode, NotServedRecipients.Message);
					_telemetry.UnroutableMessage(context, messageSummary);
					return Result(NotServedRecipients, handled: false);
				}

				if (!dispatchResult.HasCallTargets && !dispatchResult.HasGroupMessageTargets)
				{
					// Every recipient was a dispatch address, but none named a department or group Resgrid knows. Bounce it so the
					// sender (usually a CAD system holding a stale or mistyped code) finds out instead of believing it was paged.
					messageSummary.SetSmtpReply((int)UnknownRecipients.ReplyCode, UnknownRecipients.Message);
					_telemetry.UnroutableMessage(context, messageSummary);
					return Result(UnknownRecipients, handled: false);
				}

				// Only a message that routes is worth a raw copy, and losing the copy must not cost the dispatch.
				await SaveRawMessageAsync(context, inbound, messageSummary, cancellationToken).ConfigureAwait(false);

				var fromMailbox = message.From.Mailboxes.FirstOrDefault();
				var attachments = inbound.GetAttachments(ExtractAttachments);
				messageSummary.SetAttachments(attachments, _options.MaxAttachmentBytes);
				_telemetry.MessageProcessingStarted(context, messageSummary);

				var skippedAttachments = new List<string>();
				foreach (var attachment in attachments.Where(x => x.Data.Length > _options.MaxAttachmentBytes))
				{
					skippedAttachments.Add($"{attachment.Name} skipped because it exceeded {_options.MaxAttachmentBytes} bytes.");
				}

				if (!dispatchResult.HasCallTargets)
				{
					// Group-message addresses only: no call, just the broadcast message.
					await SendGroupMessageOnceAsync(dispatchResult, inbound, messageSummary, cancellationToken).ConfigureAwait(false);
					_telemetry.MessageProcessed(context, messageSummary, processingStopwatch.Elapsed);
					return Result(SmtpResponse.Ok, handled: true);
				}

				var (dispatchList, isPending) = BuildCallDispatch(dispatchResult);
				messageSummary.CreatedAsPending = isPending;

				var callId = await _callsClient.SaveCallAsync(new NewCallInput
				{
					Priority = _options.DefaultCallPriority,
					Name = Trim(subject, 200),
					Nature = Trim(inbound.Nature, 500),
					Note = BuildNote(message, messageBody, skippedAttachments, isPending),
					DispatchList = dispatchList,
					IsPending = isPending ? true : null,
					Type = String.IsNullOrWhiteSpace(_options.DefaultCallType) ? null : _options.DefaultCallType.Trim(),
					ContactName = fromMailbox == null ? null : Trim(String.IsNullOrWhiteSpace(fromMailbox.Name) ? fromMailbox.Address : fromMailbox.Name, 200),
					ContactInfo = fromMailbox?.Address,
					ExternalId = stableMessageId,
					ReferenceId = message.MessageId,
					DepartmentId = dispatchResult.DepartmentId
				}, stableMessageId, cancellationToken).ConfigureAwait(false);

				messageSummary.CallId = callId;

				// Each attachment is keyed by its position in the message, which is the same on every redelivery, so a retry after
				// a failed upload re-sends only what Resgrid has not stored yet.
				var uploadableAttachments = attachments
					.Select((attachment, index) => (Attachment: attachment, Key: $"{stableMessageId}#attachment-{index}"))
					.Where(x => x.Attachment.Data.Length <= _options.MaxAttachmentBytes)
					.ToList();
				if (uploadableAttachments.Count > 0)
				{
					var author = GetAttachmentAuthor(dispatchResult);
					if (author == null)
					{
						// A system caller has no user of its own, so Resgrid records whatever it names as the author; with neither a
						// configured department nor one from the routing there is nothing to name. The call is already out: keep it
						// and say why the files are missing rather than failing (and retrying) the whole dispatch over them.
						foreach (var (attachment, _) in uploadableAttachments)
							messageSummary.AddRejectedAttachment(attachment.Name);

						_telemetry.ProcessingWarning(context, messageSummary,
							"attachments were not uploaded: a system caller must name the author and no department id is known", null);
					}
					else
					{
						foreach (var (attachment, key) in uploadableAttachments)
						{
							try
							{
								await _callsClient.SaveCallFileAsync(new SaveCallFileInput
								{
									CallId = callId,
									UserId = author,
									Type = (int)attachment.Type,
									Name = attachment.Name,
									Data = Convert.ToBase64String(attachment.Data),
									Note = $"SMTP attachment imported from message {stableMessageId}",
									DepartmentId = dispatchResult.DepartmentId
								}, key, cancellationToken).ConfigureAwait(false);
							}
							catch (Exception ex) when (IsPermanent(ex, out _))
							{
								// The call already exists and was dispatched; bouncing the email over a file Resgrid will never take
								// would tell the sender the dispatch failed. Record it and carry on with the rest.
								messageSummary.AddRejectedAttachment(attachment.Name);
							}
						}
					}
				}

				// Last, because a message has no idempotency key: if anything before it fails the sender retries and the call and
				// files are deduplicated by Resgrid, whereas a message already sent would go out twice.
				if (dispatchResult.HasGroupMessageTargets)
				{
					try
					{
						await SendGroupMessageOnceAsync(dispatchResult, inbound, messageSummary, cancellationToken).ConfigureAwait(false);
					}
					catch (Exception ex) when (IsPermanent(ex, out _))
					{
						// The call went out; bouncing now would tell the sender the dispatch failed.
						foreach (var code in dispatchResult.DispatchCodes.Where(x => x.Type == DispatchCodeType.GroupMessage))
							messageSummary.AddUnresolvedTarget(code, "message rejected by Resgrid");
					}
				}

				_telemetry.MessageProcessed(context, messageSummary, processingStopwatch.Elapsed);
				return Result(SmtpResponse.Ok, handled: true);
			}
			catch (DispatchInProgressException)
			{
				// Another delivery of this message is sending the same group message right now; this one retries once it is done.
				messageSummary.SetSmtpReply((int)TryAgainLater.ReplyCode, TryAgainLater.Message);
				_telemetry.MessageInProgress(context, messageSummary);
				return Result(TryAgainLater, handled: false);
			}
			catch (Exception ex)
			{
				var reply = FailureReply(ex);
				messageSummary.SetSmtpReply((int)reply.ReplyCode, reply.Message);
				_telemetry.MessageFailed(context, messageSummary, ex, processingStopwatch.Elapsed);
				return Result(reply, handled: false);
			}
			finally
			{
				// Not recorded: nothing is written for a dispatch that did not finish, so its redelivery is processed again.
				if (inProgress)
					_processedMessageStore.Abandon(processingKey);
			}
		}

		/// <summary>
		/// Records the dispatch as processed. It already worked, so failing to record it costs at most a redelivery that Resgrid
		/// deduplicates; that is logged, not turned into an error reply.
		/// </summary>
		private async Task CompleteAsync(ISessionContext context, SmtpMessageSummary summary, string processingKey)
		{
			try
			{
				// Not the session's token: the dispatch happened, so its record has to be written even if the session is ending.
				await _processedMessageStore.CompleteAsync(processingKey, CancellationToken.None).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				_telemetry.ProcessingWarning(context, summary, "the dispatch could not be recorded as processed; a redelivery would be sent to Resgrid again", ex);
			}
		}

		/// <summary>
		/// Who an attachment is uploaded as. Resgrid takes the author of a token caller's upload from the token and ignores the
		/// field (which it still requires), so a token caller never needs a user id. A system caller has no user, and Resgrid
		/// stores the one it names: the configured department (as before) or the dispatch's department; null when neither is known.
		/// </summary>
		private string GetAttachmentAuthor(DispatchParseResult dispatchResult)
		{
			var currentUserId = _callsClient.CurrentUserId;
			if (!_usesSystemApiKey)
				return String.IsNullOrWhiteSpace(currentUserId) ? TokenCallerAttachmentAuthor : currentUserId;

			if (!String.IsNullOrWhiteSpace(currentUserId))
				return currentUserId;

			return String.IsNullOrWhiteSpace(dispatchResult.DepartmentId) ? null : dispatchResult.DepartmentId;
		}

		private async Task SaveRawMessageAsync(ISessionContext context, InboundMessage inbound, SmtpMessageSummary summary, CancellationToken cancellationToken)
		{
			if (!_options.SaveRawMessages)
				return;

			if (inbound.RawMessagePath != null)
			{
				summary.RawMessagePath = inbound.RawMessagePath;
				return;
			}

			var rawMessagePath = Path.Combine(_messageDirectory, RawMessageFileName(inbound.StableMessageId));
			try
			{
				Directory.CreateDirectory(_messageDirectory);
				inbound.Raw.Position = 0;
				await using (var fileStream = File.Create(rawMessagePath))
				{
					await inbound.Raw.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
				}

				inbound.RawMessagePath = rawMessagePath;
				summary.RawMessagePath = rawMessagePath;
			}
			catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
			{
				_telemetry.ProcessingWarning(context, summary, $"the raw copy of the message could not be saved to {rawMessagePath}", ex);
			}
		}

		/// <summary>
		/// Adds the envelope recipients this dispatch left out because they belong to a different department. Header-only
		/// addresses are not added: this relay did not accept them, and when they were sent here at all it was in their own
		/// transaction.
		/// </summary>
		private static void CollectOtherDepartmentRecipients(DispatchParseResult dispatchResult, IReadOnlyList<string> envelope, List<string> otherDepartmentRecipients)
		{
			foreach (var address in dispatchResult.OtherDepartmentAddresses)
			{
				if (envelope.Contains(address, StringComparer.OrdinalIgnoreCase) && !otherDepartmentRecipients.Contains(address, StringComparer.OrdinalIgnoreCase))
					otherDepartmentRecipients.Add(address);
			}
		}

		/// <summary>
		/// What the processed-message store keys a dispatch by: the message id plus the envelope recipients it was accepted for.
		/// Routing follows the recipients, so the same message delivered to another department in a separate transaction (hosted
		/// mode, where each department's subdomain gets its own) is a separate dispatch, not a duplicate. Without an envelope
		/// (never the case from SmtpServer) it is the message id alone.
		/// </summary>
		internal static string ProcessingKey(string stableMessageId, IReadOnlyCollection<string> envelopeRecipients)
		{
			if (envelopeRecipients == null || envelopeRecipients.Count == 0)
				return stableMessageId;

			var normalized = envelopeRecipients
				.Where(x => !String.IsNullOrWhiteSpace(x))
				.Select(x => x.Trim().ToLowerInvariant())
				.Distinct(StringComparer.Ordinal)
				.OrderBy(x => x, StringComparer.Ordinal);

			var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(String.Join("\n", normalized)))).Substring(0, 32);
			return $"{stableMessageId}|rcpt:{hash}";
		}

		/// <summary>The parsed message, shared by every department pass.</summary>
		private sealed class InboundMessage
		{
			private List<AttachmentPayload> _attachments;

			public MimeMessage Message { get; init; }
			public MemoryStream Raw { get; init; }
			public string Subject { get; init; }
			public string Body { get; init; }
			public string Nature { get; init; }
			public string StableMessageId { get; init; }
			public string RawMessagePath { get; set; }

			public List<AttachmentPayload> GetAttachments(Func<MimeMessage, IEnumerable<AttachmentPayload>> extract) =>
				_attachments ??= extract(Message).ToList();
		}

		/// <summary>
		/// SmtpServer answers any exception from SaveAsync with 554, a permanent failure: the sending server bounces the dispatch
		/// email and never retries. So failures are answered here. A retry is safe (Resgrid keys the call and each attachment by
		/// the message id, so a redelivery returns what was already saved), so anything that may succeed later gets 451 and the
		/// sender keeps retrying, typically for days.
		/// </summary>
		internal static readonly SmtpResponse TryAgainLater =
			new SmtpResponse(SmtpReplyCode.Aborted, "4.3.0 The dispatch could not be processed right now, try again later");

		internal static readonly SmtpResponse Unparseable =
			new SmtpResponse(SmtpReplyCode.TransactionFailed, "5.6.0 The message could not be parsed");

		internal static readonly SmtpResponse UnknownRecipients =
			new SmtpResponse(SmtpReplyCode.MailboxUnavailable, "5.1.1 No Resgrid department or group has this dispatch address");

		/// <summary>
		/// The transaction accepted a recipient this relay can never dispatch: every recipient of a pass belongs to another
		/// department than the one the pass is pinned to (a code of another department than DefaultDepartmentId, accepted at RCPT
		/// TO only because its lookup failed there, or a hosted-mode address whose code is another department's than its
		/// subdomain). No later pass or redelivery would serve it, so it is bounced, and never hidden behind another pass's 250.
		/// </summary>
		internal static readonly SmtpResponse NotServedRecipients =
			new SmtpResponse(SmtpReplyCode.MailboxUnavailable, "5.1.1 A recipient belongs to a Resgrid department this relay does not dispatch for");

		internal static SmtpResponse FailureReply(Exception exception)
		{
			return IsPermanent(exception, out var reason)
				? new SmtpResponse(SmtpReplyCode.TransactionFailed, reason)
				: TryAgainLater;
		}

		/// <summary>
		/// Only an answer Resgrid will give again to the same request is permanent: the request itself was rejected (400, 422),
		/// what it refers to is gone (410), or it is too large (413). Everything else may succeed later and is retried:
		/// 401/403 (a key or permission an operator can fix), 404 (Resgrid reports a missing code or record in the body of a 200;
		/// a 404 means the request did not reach the endpoint: a proxy, a deploy, a wrong base URL or API version),
		/// 408/409/429 (timing, or another attempt still running), 5xx, timeouts, network errors and anything unexpected.
		/// </summary>
		internal static bool IsPermanent(Exception exception, out string reason)
		{
			if (exception is HttpRequestException http && http.StatusCode is HttpStatusCode status)
			{
				switch (status)
				{
					case HttpStatusCode.BadRequest:
					case HttpStatusCode.Gone:
					case HttpStatusCode.RequestEntityTooLarge:
					case HttpStatusCode.UnprocessableEntity:
						reason = $"5.6.0 Resgrid rejected the dispatch (HTTP {(int)status})";
						return true;
				}
			}

			reason = null;
			return false;
		}

		/// <summary>
		/// Checks every dispatch address against Resgrid and turns it into what SaveCall needs, dropping (and recording) the ones
		/// that name nothing. Mirrors how Resgrid's own inbound-email pipeline treats these addresses:
		///   Department   (code@dispatch-domain)  the code is the department's dispatch email code, a secret: it must resolve via
		///                GET Departments/GetDepartmentByDispatchCode, otherwise anyone could page a whole department by mailing
		///                any address at the domain. With the system key the code also tells us WHICH department.
		///   Group        (code@groups-domain)    GET Groups/GetGroupByDispatchCode -> numeric id for "G:{id}".
		///   GroupMessage (code@group-message-domain) GET Groups/GetGroupByMessageCode -> numeric id for the message recipient.
		/// With the system key every target must sit in one department, known from a department code, the hosted-mode subdomain
		/// or DefaultDepartmentId (Resgrid does not return a group's department, so it is passed in and Resgrid checks it).
		/// ResolveDispatchCodes=false keeps the legacy behavior for group codes (sent as "G:{code}", which Resgrid matches to a
		/// group id or name); ValidateDepartmentCodes=false skips the department check (only for a Resgrid without the endpoint).
		/// </summary>
		private async Task ResolveDispatchCodesAsync(DispatchParseResult dispatchResult, SmtpMessageSummary summary, CancellationToken cancellationToken)
		{
			if (!_lookupService.CanLookup)
				return;

			var unresolved = new List<(DispatchCode Code, string Reason)>();
			var claimedDepartmentId = dispatchResult.DepartmentId;
			string departmentId = null;

			if (_options.ValidateDepartmentCodes)
			{
				foreach (var code in dispatchResult.DispatchCodes.Where(x => x.Type == DispatchCodeType.Department))
				{
					var department = await _lookupService.LookupDepartmentByDispatchCodeAsync(code.Code, cancellationToken).ConfigureAwait(false);
					if (department == null)
					{
						unresolved.Add((code, "unknown department dispatch code"));
						continue;
					}

					if (_usesSystemApiKey &&
						((!String.IsNullOrWhiteSpace(claimedDepartmentId) && !SameId(department.DepartmentId, claimedDepartmentId)) ||
						 (departmentId != null && !SameId(department.DepartmentId, departmentId))))
					{
						// Not part of this department's dispatch. When the transaction accepted it, the caller dispatches it on its own.
						unresolved.Add((code, "belongs to a different department"));
						dispatchResult.AddOtherDepartmentAddress(dispatchResult.AddressOf(code));
						continue;
					}

					code.ResolvedId = department.DepartmentId;
					departmentId ??= department.DepartmentId;
				}
			}

			if (String.IsNullOrWhiteSpace(departmentId))
				departmentId = String.IsNullOrWhiteSpace(claimedDepartmentId) ? null : claimedDepartmentId;

			foreach (var code in dispatchResult.DispatchCodes.Where(x => x.Type == DispatchCodeType.Group || x.Type == DispatchCodeType.GroupMessage))
			{
				if (code.Type == DispatchCodeType.Group && !_options.ResolveDispatchCodes)
					continue;

				if (_usesSystemApiKey && departmentId == null)
				{
					unresolved.Add((code, "no department: add the department dispatch address, a hosted-mode subdomain or DefaultDepartmentId"));
					continue;
				}

				var lookupDepartmentId = _usesSystemApiKey ? departmentId : null;
				var group = code.Type == DispatchCodeType.Group
					? await _lookupService.LookupGroupByDispatchCodeAsync(code.Code, lookupDepartmentId, cancellationToken).ConfigureAwait(false)
					: await _lookupService.LookupGroupByMessageCodeAsync(code.Code, lookupDepartmentId, cancellationToken).ConfigureAwait(false);

				if (group == null)
				{
					unresolved.Add((code, code.Type == DispatchCodeType.Group ? "unknown group dispatch code" : "unknown group message code"));
					continue;
				}

				code.ResolvedId = group.GroupId;
			}

			foreach (var (code, reason) in unresolved)
			{
				dispatchResult.DispatchCodes.Remove(code);
				summary.AddUnresolvedTarget(code, reason);
			}

			if (_usesSystemApiKey)
				dispatchResult.DepartmentId = departmentId;
		}

		private static bool SameId(string left, string right) =>
			String.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

		/// <summary>
		/// The DispatchList and pending flag for the call, following Resgrid's SaveCall rules:
		///   - a department address means the whole department. A token caller gets that with "0". The system key is never
		///     allowed to page a whole department on its own (Resgrid pages nobody for it), so the call is saved PENDING with "0":
		///     it appears in the dispatchers' pending queue and pages everyone once a dispatcher sends it.
		///   - otherwise the resolved groups ("G:{id}|G:{id}").
		/// </summary>
		internal (string DispatchList, bool IsPending) BuildCallDispatch(DispatchParseResult dispatchResult)
		{
			if (dispatchResult.DispatchCodes.Any(x => x.Type == DispatchCodeType.Department))
				return ("0", _usesSystemApiKey);

			return (DispatchListBuilder.Build(dispatchResult.DispatchCodes, _options.DepartmentDispatchPrefix), false);
		}

		/// <summary>
		/// Sends the group message once per message: Resgrid has no idempotency key for messages, and the same message now reaches
		/// this method again whenever it arrives in another transaction (a sender that splits its recipients by domain sends the
		/// dispatch address and the group-message address separately, and both carry the same To/Cc headers). The send is
		/// recorded under the message id, department and groups only after it worked; while another delivery is sending it, this
		/// one is asked to retry.
		/// </summary>
		private async Task SendGroupMessageOnceAsync(DispatchParseResult dispatchResult, InboundMessage inbound, SmtpMessageSummary summary, CancellationToken cancellationToken)
		{
			if (_messagesClient == null)
				throw new InvalidOperationException("No Resgrid messages client is configured to deliver group messages.");

			var groupIds = dispatchResult.DispatchCodes
				.Where(x => x.Type == DispatchCodeType.GroupMessage)
				.Select(x => x.DispatchToken)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();

			var sendKey = $"{inbound.StableMessageId}#group-message|{dispatchResult.DepartmentId?.Trim()}|" +
				String.Join(",", groupIds.Select(x => x.Trim().ToLowerInvariant()).OrderBy(x => x, StringComparer.Ordinal));

			switch (await _processedMessageStore.TryBeginAsync(sendKey, cancellationToken).ConfigureAwait(false))
			{
				case ProcessingRegistration.Duplicate:
					return;
				case ProcessingRegistration.InProgress:
					throw new DispatchInProgressException();
			}

			try
			{
				summary.GroupMessageId = await _messagesClient.SendMessageAsync(new NewMessageInput
				{
					Title = Trim(inbound.Subject, 200),
					Body = Trim(inbound.Body, 4000),
					Type = 0,
					Recipients = groupIds.Select(id => new MessageRecipientInput { Id = $"G:{id}", Type = MessageRecipientTypes.Group }).ToList()
				}, cancellationToken).ConfigureAwait(false);
			}
			catch
			{
				_processedMessageStore.Abandon(sendKey);
				throw;
			}

			try
			{
				await _processedMessageStore.CompleteAsync(sendKey, CancellationToken.None).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				_telemetry.ProcessingWarning(null, summary, "the group message could not be recorded as sent; a redelivery would send it again", ex);
			}
		}

		/// <summary>
		/// The envelope (RCPT TO) recipients: the addresses this relay actually accepted the message for, and the only place a
		/// Bcc'd or forwarded dispatch address appears at all (CAD systems commonly Bcc the dispatch address, and a forwarded
		/// message keeps the original To header). They decide the department and are part of the duplicate key.
		/// </summary>
		internal static IReadOnlyList<string> GetEnvelopeRecipients(IMessageTransaction transaction)
		{
			return transaction?.To?
				.Where(x => x != null && !String.IsNullOrWhiteSpace(x.User) && !String.IsNullOrWhiteSpace(x.Host))
				.Select(x => $"{x.User}@{x.Host}")
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList() ?? new List<string>();
		}

		/// <summary>
		/// The To/Cc header addresses. They add targets of the envelope's department that were sent in another transaction (a
		/// sender splitting recipients by domain), which Resgrid's idempotency key folds into the same call; they never decide
		/// the department when there is an envelope.
		/// </summary>
		internal static IReadOnlyList<string> GetHeaderRecipients(MimeMessage message)
		{
			return message.To.Mailboxes
				.Concat(message.Cc.Mailboxes)
				.Select(x => x.Address)
				.Where(x => !String.IsNullOrWhiteSpace(x))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();
		}

		private string BuildNote(MimeMessage message, string messageBody, List<string> skippedAttachments, bool isPending)
		{
			var builder = new StringBuilder();
			if (isPending)
				builder.AppendLine("Department-wide email dispatch received by the hosted relay: saved as a pending call, send it to dispatch the department.");
			builder.AppendLine($"Imported from SMTP at {DateTimeOffset.UtcNow:O}");
			builder.AppendLine($"From: {String.Join(", ", message.From.Mailboxes.Select(x => x.ToString()))}");
			builder.AppendLine($"To: {String.Join(", ", message.To.Mailboxes.Select(x => x.ToString()))}");
			if (message.Cc.Count > 0)
				builder.AppendLine($"Cc: {String.Join(", ", message.Cc.Mailboxes.Select(x => x.ToString()))}");
			builder.AppendLine($"Subject: {message.Subject}");
			builder.AppendLine($"Message-Id: {message.MessageId}");
			builder.AppendLine();
			builder.AppendLine(Trim(messageBody, 8000));

			if (skippedAttachments.Count > 0)
			{
				builder.AppendLine();
				builder.AppendLine(String.Join(Environment.NewLine, skippedAttachments));
			}

			return builder.ToString();
		}

		private IEnumerable<AttachmentPayload> ExtractAttachments(MimeMessage message)
		{
			foreach (var attachment in message.Attachments)
			{
				switch (attachment)
				{
					case MimePart mimePart:
						using (var attachmentStream = new MemoryStream())
						{
							mimePart.Content.DecodeTo(attachmentStream);
							yield return new AttachmentPayload
							{
								Name = String.IsNullOrWhiteSpace(mimePart.FileName) ? "attachment.bin" : mimePart.FileName,
								Data = attachmentStream.ToArray(),
								Type = ResolveAttachmentType(mimePart.ContentType?.MimeType)
							};
						}
						break;
					case MessagePart messagePart:
						using (var attachmentStream = new MemoryStream())
						{
							messagePart.Message.WriteTo(attachmentStream);
							yield return new AttachmentPayload
							{
								Name = "attached-message.eml",
								Data = attachmentStream.ToArray(),
								Type = CallFileType.File
							};
						}
						break;
				}
			}
		}

		private static CallFileType ResolveAttachmentType(string mimeType)
		{
			if (String.IsNullOrWhiteSpace(mimeType))
				return CallFileType.File;

			if (mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
				return CallFileType.Audio;

			if (mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
				return CallFileType.Image;

			if (mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
				return CallFileType.Video;

			return CallFileType.File;
		}

		private static string GetMessageBody(MimeMessage message)
		{
			if (!String.IsNullOrWhiteSpace(message.TextBody))
				return message.TextBody.Trim();

			if (!String.IsNullOrWhiteSpace(message.HtmlBody))
			{
				var text = HtmlToPlainText(message.HtmlBody);
				if (!String.IsNullOrWhiteSpace(text))
					return text;
			}

			return "(No message body)";
		}

		private static readonly HashSet<string> BlockTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"br", "p", "div", "tr", "li", "h1", "h2", "h3", "h4", "h5", "h6", "table", "ul", "ol", "pre", "blockquote", "hr"
		};

		/// <summary>
		/// Many CAD systems send HTML-only dispatch mail. Its markup must not become the call's nature and note (responders read
		/// those on a phone), so the visible text is pulled out: script/style content dropped, block elements turned into lines.
		/// </summary>
		internal static string HtmlToPlainText(string html)
		{
			var builder = new StringBuilder();
			var skipDepth = 0;

			using (var reader = new StringReader(html))
			{
				var tokenizer = new MimeKit.Text.HtmlTokenizer(reader) { DecodeCharacterReferences = true };
				while (tokenizer.ReadNextToken(out var token))
				{
					switch (token)
					{
						case MimeKit.Text.HtmlTagToken tag:
							if (tag.Id == MimeKit.Text.HtmlTagId.Script || tag.Id == MimeKit.Text.HtmlTagId.Style || tag.Id == MimeKit.Text.HtmlTagId.Head)
							{
								if (!tag.IsEndTag && !tag.IsEmptyElement)
									skipDepth++;
								else if (tag.IsEndTag && skipDepth > 0)
									skipDepth--;
							}
							else if (skipDepth == 0 && BlockTags.Contains(tag.Name))
							{
								builder.AppendLine();
							}
							else if (skipDepth == 0 && (tag.Id == MimeKit.Text.HtmlTagId.TD || tag.Id == MimeKit.Text.HtmlTagId.TH) && !tag.IsEndTag)
							{
								builder.Append(' ');
							}
							break;
						case MimeKit.Text.HtmlDataToken data when skipDepth == 0:
							builder.Append(data.Data);
							break;
					}
				}
			}

			var lines = builder.ToString()
				.Replace(' ', ' ')
				.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
				.Select(x => System.Text.RegularExpressions.Regex.Replace(x, @"[ \t]+", " ").Trim())
				.Where(x => x.Length > 0);

			return String.Join(Environment.NewLine, lines);
		}

		private static string GetNature(string body, string fallback)
		{
			if (!String.IsNullOrWhiteSpace(body))
			{
				var firstLine = body
					.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
					.Select(x => x.Trim())
					.FirstOrDefault(x => !String.IsNullOrWhiteSpace(x));

				if (!String.IsNullOrWhiteSpace(firstLine))
					return firstLine;
			}

			return fallback;
		}

		/// <summary>
		/// The Message-Id, or for a message without one a hash of what identifies this particular email: sender, recipients,
		/// subject, body, the Date header and the attachments' names and sizes. The Date matters: a CAD system that sends the
		/// same test page every morning sends the same subject and body each time, and without it the second day's page would
		/// be a "duplicate" here and Resgrid's idempotency key would hand back the first day's call.
		/// </summary>
		public static string GetStableMessageId(MimeMessage message)
		{
			if (!String.IsNullOrWhiteSpace(message.MessageId))
				return message.MessageId.Trim('<', '>', ' ');

			var rawId = String.Join("|",
				message.Subject,
				String.Join(",", message.From.Mailboxes.Select(x => x.Address)),
				String.Join(",", message.To.Mailboxes.Select(x => x.Address)),
				String.Join(",", message.Cc.Mailboxes.Select(x => x.Address)),
				message.Headers[HeaderId.Date]?.Trim(),
				AttachmentFingerprint(message),
				GetMessageBody(message));

			return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawId)));
		}

		private static string AttachmentFingerprint(MimeMessage message)
		{
			var parts = new List<string>();
			foreach (var attachment in message.Attachments)
			{
				using var measure = new MimeKit.IO.MeasuringStream();
				switch (attachment)
				{
					case MimePart mimePart:
						mimePart.Content?.DecodeTo(measure);
						parts.Add($"{mimePart.FileName}:{measure.Length}");
						break;
					case MessagePart messagePart:
						messagePart.Message?.WriteTo(measure);
						parts.Add($"attached-message.eml:{measure.Length}");
						break;
				}
			}

			return String.Join(",", parts);
		}

		private static string ResolvePath(string path)
		{
			if (Path.IsPathRooted(path))
				return path;

			return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
		}

		/// <summary>
		/// The Message-Id is chosen by the sender and may run to hundreds of characters. A name past the filesystem's limit would
		/// throw on every redelivery, so the message would be answered 451 forever and never dispatched; long ids are hashed.
		/// </summary>
		internal static string RawMessageFileName(string stableMessageId)
		{
			var invalidCharacters = Path.GetInvalidFileNameChars();
			var sanitized = new string(stableMessageId.Select(x => invalidCharacters.Contains(x) || Char.IsControl(x) ? '_' : x).ToArray());

			if (sanitized.Length > 120 || sanitized.Trim('.').Length == 0)
				sanitized = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stableMessageId)));

			return sanitized + ".eml";
		}

		private static string Trim(string value, int maxLength)
		{
			if (String.IsNullOrWhiteSpace(value))
				return value;

			return value.Length <= maxLength ? value : value.Substring(0, maxLength);
		}
	}

	public sealed class RelayMailboxFilter : IMailboxFilter, IMailboxFilterFactory
	{
		/// <summary>Session property holding the department the current transaction's recipients belong to.</summary>
		internal const string TransactionDepartmentKey = "Resgrid:Relay:TransactionDepartment";

		/// <summary>
		/// 452 4.5.3 ("too many recipients") makes the sender keep this recipient and deliver it again in a later transaction,
		/// which then holds only its own department. Thrown as an <see cref="SmtpResponseException"/>, the only way a mailbox
		/// filter can give a reply other than 250/550.
		/// </summary>
		internal static readonly SmtpResponse OtherDepartmentRecipient = new SmtpResponse(SmtpReplyCode.InsufficientStorage,
			"4.5.3 This recipient belongs to a different Resgrid department than the others in this transaction, send it separately");

		/// <summary>
		/// 550 5.1.1 for a recipient of another department than <see cref="SmtpRelayOptions.DefaultDepartmentId"/>. That department
		/// is the only one this relay dispatches (it overrides every recipient's own, and a code of any other is dropped when the
		/// message is processed), so a later transaction would not serve the recipient either: 452 would only make the sender
		/// retry it for days without it ever being delivered or bounced.
		/// </summary>
		internal static readonly SmtpResponse DepartmentNotServed = new SmtpResponse(SmtpReplyCode.MailboxUnavailable,
			"5.1.1 This recipient belongs to a Resgrid department this relay does not dispatch for");

		private readonly SmtpRelayOptions _options;
		private readonly ISmtpTelemetry _telemetry;
		private readonly bool _usesSystemApiKey;
		private readonly Func<string, CancellationToken, Task<string>> _departmentCodeResolver;

		/// <param name="departmentCodeResolver">
		/// Resolves a department dispatch code to its department id (null when unknown), so recipients on a shared dispatch domain
		/// can be kept to one department per transaction too. Without it only hosted-mode subdomains are told apart.
		/// </param>
		public RelayMailboxFilter(SmtpRelayOptions options, ISmtpTelemetry telemetry, bool usesSystemApiKey = false,
			Func<string, CancellationToken, Task<string>> departmentCodeResolver = null)
		{
			_options = options ?? throw new ArgumentNullException(nameof(options));
			_telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
			_usesSystemApiKey = usesSystemApiKey;
			_departmentCodeResolver = departmentCodeResolver;
		}

		public Task<bool> CanAcceptFromAsync(ISessionContext context, IMailbox from, int size, CancellationToken cancellationToken)
		{
			// MAIL FROM starts a new transaction (SmtpServer has just reset it): the previous one's department no longer applies.
			context?.Properties?.Remove(TransactionDepartmentKey);

			_telemetry.SenderAccepted(context, from, size);
			return Task.FromResult(true);
		}

		/// <summary>
		/// Accepts only recipients the relay can act on, so anything else is refused (550) in the SMTP dialogue and the sender
		/// learns about it, instead of being accepted with a 250 and silently dropped: department and group dispatch addresses
		/// always; group-message addresses only with a user/department token (the system key cannot send messages); distribution
		/// lists never (Resgrid has no API for them).
		///
		/// One message is one dispatch in one department, so a transaction carries recipients of one department only: a recipient
		/// of another department than the transaction's first (by hosted-mode subdomain, or by what its department code resolves
		/// to) is deferred with 452 4.5.3 and the sender delivers it again in a transaction of its own. With DefaultDepartmentId
		/// set, that department is the only one the relay dispatches: its recipients are never the ones deferred, and a recipient
		/// of any other department is refused with 550 5.1.1, alone or not, since no transaction of its own would serve it.
		/// </summary>
		public async Task<bool> CanDeliverToAsync(ISessionContext context, IMailbox to, IMailbox from, CancellationToken token)
		{
			var parser = new SmtpDispatchAddressParser(_options);
			var accepted = false;
			string reason = "recipient domain is not configured for Resgrid dispatch routing";

			if (parser.TryParse($"{to.User}@{to.Host}", out var code, out var subdomainDepartmentId))
			{
				switch (code.Type)
				{
					case DispatchCodeType.Department:
					case DispatchCodeType.Group:
						accepted = true;
						break;
					case DispatchCodeType.GroupMessage when !_usesSystemApiKey:
						accepted = true;
						break;
					case DispatchCodeType.GroupMessage:
						reason = "group-message addresses need a user or department token; the system API key cannot send messages";
						break;
					default:
						reason = "distribution-list addresses are not supported by the relay";
						break;
				}
			}

			if (accepted && context?.Properties != null)
			{
				var department = String.IsNullOrWhiteSpace(subdomainDepartmentId)
					? await ResolveDepartmentAsync(code, token).ConfigureAwait(false)
					: subdomainDepartmentId.Trim();

				if (!String.IsNullOrWhiteSpace(department))
				{
					var pinnedDepartment = String.IsNullOrWhiteSpace(_options.DefaultDepartmentId) ? null : _options.DefaultDepartmentId.Trim();
					if (pinnedDepartment != null && !String.Equals(pinnedDepartment, department, StringComparison.OrdinalIgnoreCase))
					{
						// Not a conflict with another recipient that a transaction of its own would resolve: no transaction serves it.
						_telemetry.RecipientEvaluated(context, to, from, false,
							"belongs to a different department than DefaultDepartmentId, the only one this relay dispatches; refused (550)");
						throw new SmtpResponseException(DepartmentNotServed);
					}

					// Only an earlier accepted recipient's department is worth a 452: alone, this recipient is accepted.
					var transactionDepartment = context.Properties.TryGetValue(TransactionDepartmentKey, out var value) ? value as string : null;
					if (!String.IsNullOrWhiteSpace(transactionDepartment) &&
						!String.Equals(transactionDepartment, department, StringComparison.OrdinalIgnoreCase))
					{
						_telemetry.RecipientEvaluated(context, to, from, false,
							"belongs to a different department than this transaction's other recipients; deferred (452) so it is sent separately");
						throw new SmtpResponseException(OtherDepartmentRecipient);
					}

					context.Properties[TransactionDepartmentKey] = transactionDepartment ?? department;
				}
			}

			_telemetry.RecipientEvaluated(context, to, from, accepted, accepted ? null : reason);

			return accepted;
		}

		private async Task<string> ResolveDepartmentAsync(DispatchCode code, CancellationToken token)
		{
			if (code.Type != DispatchCodeType.Department || _departmentCodeResolver == null)
				return null;

			try
			{
				return await _departmentCodeResolver(code.Code, token).ConfigureAwait(false);
			}
			catch (Exception ex) when (!(ex is OperationCanceledException && token.IsCancellationRequested))
			{
				// The lookup is retried when the message is processed, which also keeps departments apart; a lookup failing
				// here must not refuse the recipient.
				return null;
			}
		}

		public IMailboxFilter CreateInstance(ISessionContext context)
		{
			return this;
		}
	}

	/// <summary>The outcome of asking to process a dispatch.</summary>
	internal enum ProcessingRegistration
	{
		/// <summary>Not processed yet and not in progress: the caller now owns it and must Complete or Abandon it.</summary>
		Started,

		/// <summary>Already processed successfully within the duplicate window.</summary>
		Duplicate,

		/// <summary>Another delivery is processing it right now.</summary>
		InProgress
	}

	/// <summary>Another delivery is still processing the same dispatch; this one should be retried later (451).</summary>
	internal sealed class DispatchInProgressException : Exception
	{
		public DispatchInProgressException()
			: base("Another delivery of this message is still being processed.")
		{
		}
	}

	/// <summary>
	/// Which dispatches were already processed (on disk, for the duplicate window) and which are being processed right now (in
	/// memory). A dispatch is written to disk only once it has worked: written before the work, a crash part-way through would
	/// turn every redelivery for the whole window into a "duplicate" and the dispatch would be lost without a word. A crash now
	/// leaves nothing behind, and the redelivery is processed again, which is safe because Resgrid's idempotency key returns
	/// the call and files already saved. The in-memory set answers a redelivery that arrives while the first attempt is still
	/// running, which must not be told 250 before that attempt is known to have worked.
	/// </summary>
	internal sealed class ProcessedMessageStore
	{
		private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
		{
			WriteIndented = true
		};

		private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
		private readonly ConcurrentDictionary<string, byte> _inProgress = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
		private readonly string _path;
		private readonly TimeSpan _retention;

		public ProcessedMessageStore(string path, int duplicateWindowHours)
		{
			_path = path;
			_retention = TimeSpan.FromHours(duplicateWindowHours <= 0 ? 72 : duplicateWindowHours);
		}

		public async Task<ProcessingRegistration> TryBeginAsync(string key, CancellationToken cancellationToken)
		{
			await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				if (_inProgress.ContainsKey(key))
					return ProcessingRegistration.InProgress;

				var entries = await LoadAsync(cancellationToken).ConfigureAwait(false);
				var cutoff = DateTimeOffset.UtcNow.Subtract(_retention);
				var expiredKeys = entries
					.Where(x => x.Value < cutoff)
					.Select(x => x.Key)
					.ToArray();

				foreach (var expiredKey in expiredKeys)
				{
					entries.Remove(expiredKey);
				}

				if (expiredKeys.Length > 0)
				{
					try
					{
						await SaveAsync(entries, cancellationToken).ConfigureAwait(false);
					}
					catch (IOException)
					{
						// Only housekeeping; the expired entries are dropped again next time.
					}
				}

				if (entries.ContainsKey(key))
					return ProcessingRegistration.Duplicate;

				_inProgress[key] = 0;
				return ProcessingRegistration.Started;
			}
			finally
			{
				_gate.Release();
			}
		}

		/// <summary>Records a started dispatch as processed (it worked) and ends its in-progress mark.</summary>
		public async Task CompleteAsync(string key, CancellationToken cancellationToken)
		{
			try
			{
				await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
				try
				{
					var entries = await LoadAsync(cancellationToken).ConfigureAwait(false);
					entries[key] = DateTimeOffset.UtcNow;
					await SaveAsync(entries, cancellationToken).ConfigureAwait(false);
				}
				finally
				{
					_gate.Release();
				}
			}
			finally
			{
				_inProgress.TryRemove(key, out _);
			}
		}

		/// <summary>Ends a started dispatch's in-progress mark without recording it, so its redelivery is processed again.</summary>
		public void Abandon(string key)
		{
			_inProgress.TryRemove(key, out _);
		}

		private async Task<Dictionary<string, DateTimeOffset>> LoadAsync(CancellationToken cancellationToken)
		{
			var directory = Path.GetDirectoryName(_path);
			if (!String.IsNullOrWhiteSpace(directory))
				Directory.CreateDirectory(directory);

			if (!File.Exists(_path))
				return new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

			try
			{
				var payload = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
				if (String.IsNullOrWhiteSpace(payload))
					return new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

				var items = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(payload);
				if (items == null)
					return new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

				return new Dictionary<string, DateTimeOffset>(items, StringComparer.OrdinalIgnoreCase);
			}
			catch (Exception ex) when (ex is JsonException || ex is IOException)
			{
				return new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
			}
		}

		private async Task SaveAsync(Dictionary<string, DateTimeOffset> entries, CancellationToken cancellationToken)
		{
			// Swap a complete temp file in: a store torn by a crash reads back as empty, and every message in the duplicate
			// window would then be dispatched again when its sender retries.
			var payload = JsonSerializer.Serialize(entries, SerializerOptions);
			var tempPath = _path + ".tmp";
			await File.WriteAllTextAsync(tempPath, payload, cancellationToken).ConfigureAwait(false);
			File.Move(tempPath, _path, overwrite: true);
		}
	}
}
