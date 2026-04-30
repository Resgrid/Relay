using MimeKit;
using Resgrid.Audio.Relay.Console.Configuration;
using Resgrid.Providers.ApiClient.V4;
using Sentry;
using Serilog;
using Serilog.Core;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Protocol;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Resgrid.Audio.Relay.Console.Smtp
{
	internal interface ISmtpTelemetry : IAsyncDisposable
	{
		void RelayStarting(SmtpRelayOptions options);
		void RelayStopped(SmtpRelayOptions options);
		void RelayFaulted(SmtpRelayOptions options, Exception exception);
		void SessionCreated(ISessionContext context);
		void SessionCompleted(ISessionContext context);
		void SessionCancelled(ISessionContext context);
		void SessionFaulted(ISessionContext context, Exception exception);
		void SenderAccepted(ISessionContext context, IMailbox from, int size);
		void RecipientEvaluated(ISessionContext context, IMailbox to, IMailbox from, bool accepted, string reason);
		void MessageReceived(ISessionContext context, SmtpMessageSummary message);
		void DuplicateMessage(ISessionContext context, SmtpMessageSummary message);
		void UnroutableMessage(ISessionContext context, SmtpMessageSummary message);
		void MessageProcessingStarted(ISessionContext context, SmtpMessageSummary message);
		void MessageProcessed(ISessionContext context, SmtpMessageSummary message, TimeSpan duration);
		void MessageFailed(ISessionContext context, SmtpMessageSummary message, Exception exception, TimeSpan duration);
	}

	internal sealed class SmtpTelemetry : ISmtpTelemetry
	{
		private readonly ILogger _logger;
		private readonly CountlyTelemetryClient _countlyClient;
		private readonly IDisposable _sentrySdk;
		private readonly string _environment;
		private readonly string _release;
		private readonly string _serverName;

		private SmtpTelemetry(ILogger logger, RelayTelemetryOptions options, string serverName)
		{
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
			_serverName = String.IsNullOrWhiteSpace(serverName) ? "resgrid-relay" : serverName.Trim();
			_environment = ResolveEnvironment(options);
			_release = ResolveRelease(options);
			_countlyClient = new CountlyTelemetryClient(_logger, options?.Countly, _serverName, _environment, _release);

			if (!String.IsNullOrWhiteSpace(options?.Sentry?.Dsn))
			{
				_sentrySdk = SentrySdk.Init(sentryOptions =>
				{
					sentryOptions.Dsn = options.Sentry.Dsn.Trim();
					sentryOptions.Environment = _environment;
					sentryOptions.Release = _release;
					sentryOptions.ServerName = _serverName;
					sentryOptions.AttachStacktrace = true;
					sentryOptions.SendDefaultPii = options.Sentry.SendDefaultPii;
					sentryOptions.ShutdownTimeout = TimeSpan.FromSeconds(2);
				});
			}

			_logger.Information(
				"SMTP observability initialized. SentryEnabled={SentryEnabled} CountlyEnabled={CountlyEnabled} Environment={Environment} Release={Release}",
				_sentrySdk != null,
				_countlyClient.IsEnabled,
				_environment,
				_release);
		}

		public static ISmtpTelemetry Create(RelayHostOptions hostOptions, Logger logger)
		{
			if (hostOptions == null)
				throw new ArgumentNullException(nameof(hostOptions));
			if (logger == null)
				throw new ArgumentNullException(nameof(logger));

			return new SmtpTelemetry(
				logger.ForContext("RelayMode", "smtp").ForContext("Component", "SmtpRelay"),
				hostOptions.Telemetry ?? new RelayTelemetryOptions(),
				hostOptions.Smtp?.ServerName);
		}

		public void RelayStarting(SmtpRelayOptions options)
		{
			var domains = (options?.DepartmentAddressDomains ?? Array.Empty<string>())
				.Concat(options?.GroupAddressDomains ?? Array.Empty<string>())
				.Where(x => !String.IsNullOrWhiteSpace(x))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToArray();

			_logger.Information(
				"SMTP relay listening on port {Port} for {AcceptedDomains}",
				options?.Port ?? 0,
				domains);

			_countlyClient.TrackEvent("smtp_relay_started", new Dictionary<string, string>
			{
				["port"] = (options?.Port ?? 0).ToString(CultureInfo.InvariantCulture),
				["domain_count"] = domains.Length.ToString(CultureInfo.InvariantCulture)
			});
		}

		public void RelayStopped(SmtpRelayOptions options)
		{
			_logger.Information("SMTP relay stopped listening on port {Port}", options?.Port ?? 0);

			_countlyClient.TrackEvent("smtp_relay_stopped", new Dictionary<string, string>
			{
				["port"] = (options?.Port ?? 0).ToString(CultureInfo.InvariantCulture)
			});
		}

		public void RelayFaulted(SmtpRelayOptions options, Exception exception)
		{
			_logger.Error(exception, "SMTP relay faulted while listening on port {Port}", options?.Port ?? 0);

			_countlyClient.TrackEvent("smtp_relay_faulted", new Dictionary<string, string>
			{
				["port"] = (options?.Port ?? 0).ToString(CultureInfo.InvariantCulture),
				["failure_type"] = exception?.GetType().Name ?? "UnknownException"
			});

			CaptureException(exception, null, null, "relay_fault");
		}

		public void SessionCreated(ISessionContext context)
		{
			var state = SmtpSessionStateAccessor.GetOrCreate(context);
			AttachSessionHandlers(context, state);

			_logger.Information(
				"SMTP connection opened. SessionId={SessionId} RemoteEndPoint={RemoteEndPoint} LocalEndPoint={LocalEndPoint} Secure={Secure}",
				state.SessionId,
				state.RemoteEndPoint,
				state.LocalEndPoint,
				state.IsSecure);

			_countlyClient.TrackEvent("smtp_connection_started", BuildConnectionSegmentation(state));
		}

		public void SessionCompleted(ISessionContext context)
		{
			var state = SmtpSessionStateAccessor.GetOrCreate(context);
			DetachSessionHandlers(context, state);
			var duration = DateTimeOffset.UtcNow.Subtract(state.StartedAtUtc);

			_logger.Information(
				"SMTP connection completed. SessionId={SessionId} RemoteEndPoint={RemoteEndPoint} Sender={Sender} MessageCount={MessageCount} AcceptedRecipientCount={AcceptedRecipientCount} RejectedRecipientCount={RejectedRecipientCount} DurationMs={DurationMs}",
				state.SessionId,
				state.RemoteEndPoint,
				state.Sender,
				state.MessageCount,
				state.AcceptedRecipientCount,
				state.RejectedRecipientCount,
				(long)duration.TotalMilliseconds);

			_countlyClient.TrackEvent("smtp_connection_completed", BuildConnectionSegmentation(state), duration);
		}

		public void SessionCancelled(ISessionContext context)
		{
			var state = SmtpSessionStateAccessor.GetOrCreate(context);
			DetachSessionHandlers(context, state);
			var duration = DateTimeOffset.UtcNow.Subtract(state.StartedAtUtc);

			_logger.Warning(
				"SMTP connection cancelled. SessionId={SessionId} RemoteEndPoint={RemoteEndPoint} DurationMs={DurationMs}",
				state.SessionId,
				state.RemoteEndPoint,
				(long)duration.TotalMilliseconds);

			_countlyClient.TrackEvent("smtp_connection_cancelled", BuildConnectionSegmentation(state), duration);
		}

		public void SessionFaulted(ISessionContext context, Exception exception)
		{
			var state = SmtpSessionStateAccessor.GetOrCreate(context);
			DetachSessionHandlers(context, state);
			var duration = DateTimeOffset.UtcNow.Subtract(state.StartedAtUtc);

			_logger.Error(
				exception,
				"SMTP connection faulted. SessionId={SessionId} RemoteEndPoint={RemoteEndPoint} Sender={Sender} MessageCount={MessageCount} DurationMs={DurationMs}",
				state.SessionId,
				state.RemoteEndPoint,
				state.Sender,
				state.MessageCount,
				(long)duration.TotalMilliseconds);

			_countlyClient.TrackEvent("smtp_connection_faulted", MergeSegmentations(
				BuildConnectionSegmentation(state),
				new Dictionary<string, string>
				{
					["failure_type"] = exception?.GetType().Name ?? "UnknownException"
				}), duration);

			if (!state.MessageFailureReported)
				CaptureException(exception, state, null, "session_fault");
		}

		public void SenderAccepted(ISessionContext context, IMailbox from, int size)
		{
			var state = SmtpSessionStateAccessor.GetOrCreate(context);
			state.Sender = FormatMailbox(from);

			_logger.Information(
				"SMTP sender accepted. SessionId={SessionId} RemoteEndPoint={RemoteEndPoint} Sender={Sender} MessageSize={MessageSize}",
				state.SessionId,
				state.RemoteEndPoint,
				state.Sender,
				size);

			_countlyClient.TrackEvent("smtp_sender_accepted", MergeSegmentations(
				BuildConnectionSegmentation(state),
				new Dictionary<string, string>
				{
					["sender_domain"] = GetDomain(state.Sender),
					["message_size"] = size.ToString(CultureInfo.InvariantCulture)
				}));
		}

		public void RecipientEvaluated(ISessionContext context, IMailbox to, IMailbox from, bool accepted, string reason)
		{
			var state = SmtpSessionStateAccessor.GetOrCreate(context);
			var recipient = FormatMailbox(to);
			if (accepted)
				state.RecordAcceptedRecipient(recipient);
			else
				state.RecordRejectedRecipient(recipient);

			if (accepted)
			{
				_logger.Information(
					"SMTP recipient {Outcome}. SessionId={SessionId} RemoteEndPoint={RemoteEndPoint} Sender={Sender} Recipient={Recipient} Reason={Reason}",
					"accepted",
					state.SessionId,
					state.RemoteEndPoint,
					FormatMailbox(from),
					recipient,
					reason);
			}
			else
			{
				_logger.Warning(
					"SMTP recipient {Outcome}. SessionId={SessionId} RemoteEndPoint={RemoteEndPoint} Sender={Sender} Recipient={Recipient} Reason={Reason}",
					"rejected",
					state.SessionId,
					state.RemoteEndPoint,
					FormatMailbox(from),
					recipient,
					reason);
			}

			_countlyClient.TrackEvent(
				accepted ? "smtp_recipient_accepted" : "smtp_recipient_rejected",
				MergeSegmentations(
					BuildConnectionSegmentation(state),
					new Dictionary<string, string>
					{
						["recipient_domain"] = GetDomain(recipient),
						["outcome"] = accepted ? "accepted" : "rejected",
						["reason"] = NormalizeSegmentationValue(reason)
					}));
		}

		public void MessageReceived(ISessionContext context, SmtpMessageSummary message)
		{
			var state = SmtpSessionStateAccessor.GetOrCreate(context);
			state.IncrementMessageCount();

			_logger.Information(
				"SMTP message received. SessionId={SessionId} StableMessageId={StableMessageId} MessageId={MessageId} From={@FromAddresses} To={@ToAddresses} Cc={@CcAddresses} Subject={Subject}",
				state.SessionId,
				message.StableMessageId,
				message.ReferenceMessageId,
				message.FromAddresses,
				message.ToAddresses,
				message.CcAddresses,
				message.Subject);

			_countlyClient.TrackEvent("smtp_message_received", BuildMessageSegmentation(state, message));
		}

		public void DuplicateMessage(ISessionContext context, SmtpMessageSummary message)
		{
			var state = SmtpSessionStateAccessor.GetOrCreate(context);
			_logger.Information(
				"Skipping duplicate SMTP message. SessionId={SessionId} StableMessageId={StableMessageId} MessageId={MessageId} Subject={Subject}",
				state.SessionId,
				message.StableMessageId,
				message.ReferenceMessageId,
				message.Subject);

			_countlyClient.TrackEvent("smtp_message_duplicate", BuildMessageSegmentation(state, message));
		}

		public void UnroutableMessage(ISessionContext context, SmtpMessageSummary message)
		{
			var state = SmtpSessionStateAccessor.GetOrCreate(context);
			_logger.Warning(
				"SMTP message did not contain a usable Resgrid dispatch recipient. SessionId={SessionId} StableMessageId={StableMessageId} MessageId={MessageId} From={@FromAddresses} To={@ToAddresses} Cc={@CcAddresses} Subject={Subject}",
				state.SessionId,
				message.StableMessageId,
				message.ReferenceMessageId,
				message.FromAddresses,
				message.ToAddresses,
				message.CcAddresses,
				message.Subject);

			_countlyClient.TrackEvent("smtp_message_unroutable", MergeSegmentations(
				BuildMessageSegmentation(state, message),
				new Dictionary<string, string>
				{
					["route_kind"] = "none"
				}));
		}

		public void MessageProcessingStarted(ISessionContext context, SmtpMessageSummary message)
		{
			var state = SmtpSessionStateAccessor.GetOrCreate(context);
			_logger.Information(
				"Processing SMTP message. SessionId={SessionId} StableMessageId={StableMessageId} Subject={Subject} RouteKind={RouteKind} DispatchTargets={@DispatchTargets} AttachmentNames={@AttachmentNames} OversizedAttachmentNames={@OversizedAttachmentNames} RawMessagePath={RawMessagePath}",
				state.SessionId,
				message.StableMessageId,
				message.Subject,
				message.RouteKind,
				message.DispatchTargets,
				message.AttachmentNames,
				message.OversizedAttachmentNames,
				message.RawMessagePath);

			_countlyClient.TrackEvent("smtp_message_processing_started", BuildMessageSegmentation(state, message));
		}

		public void MessageProcessed(ISessionContext context, SmtpMessageSummary message, TimeSpan duration)
		{
			var state = SmtpSessionStateAccessor.GetOrCreate(context);
			_logger.Information(
				"Processed SMTP message into Resgrid call. SessionId={SessionId} StableMessageId={StableMessageId} MessageId={MessageId} CallId={CallId} RouteKind={RouteKind} AttachmentCount={AttachmentCount} OversizedAttachmentCount={OversizedAttachmentCount} DurationMs={DurationMs}",
				state.SessionId,
				message.StableMessageId,
				message.ReferenceMessageId,
				message.CallId,
				message.RouteKind,
				message.AttachmentCount,
				message.OversizedAttachmentCount,
				(long)duration.TotalMilliseconds);

			_countlyClient.TrackEvent("smtp_message_processed", MergeSegmentations(
				BuildMessageSegmentation(state, message),
				new Dictionary<string, string>
				{
					["outcome"] = "processed"
				}), duration);
		}

		public void MessageFailed(ISessionContext context, SmtpMessageSummary message, Exception exception, TimeSpan duration)
		{
			var state = SmtpSessionStateAccessor.GetOrCreate(context);
			state.MessageFailureReported = true;

			_logger.Error(
				exception,
				"Failed processing SMTP message. SessionId={SessionId} StableMessageId={StableMessageId} MessageId={MessageId} CallId={CallId} Subject={Subject} RouteKind={RouteKind} DispatchTargets={@DispatchTargets} AttachmentNames={@AttachmentNames} OversizedAttachmentNames={@OversizedAttachmentNames} DurationMs={DurationMs}",
				state.SessionId,
				message.StableMessageId,
				message.ReferenceMessageId,
				message.CallId,
				message.Subject,
				message.RouteKind,
				message.DispatchTargets,
				message.AttachmentNames,
				message.OversizedAttachmentNames,
				(long)duration.TotalMilliseconds);

			_countlyClient.TrackEvent("smtp_message_failed", MergeSegmentations(
				BuildMessageSegmentation(state, message),
				new Dictionary<string, string>
				{
					["outcome"] = "failed",
					["failure_type"] = exception?.GetType().Name ?? "UnknownException"
				}), duration);

			CaptureException(exception, state, message, "message_failed");
		}

		public async ValueTask DisposeAsync()
		{
			await _countlyClient.DisposeAsync().ConfigureAwait(false);
			if (_sentrySdk != null)
			{
				await SentrySdk.FlushAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
				_sentrySdk.Dispose();
			}
		}

		private void AttachSessionHandlers(ISessionContext context, SmtpSessionState state)
		{
			if (state.CommandExecutingHandler == null)
			{
				state.CommandExecutingHandler = (_, eventArgs) => OnCommandExecuting(context, eventArgs.Command);
				context.CommandExecuting += state.CommandExecutingHandler;
			}

			if (state.ResponseExceptionHandler == null)
			{
				state.ResponseExceptionHandler = (_, eventArgs) => OnResponseException(context, eventArgs.Exception);
				context.ResponseException += state.ResponseExceptionHandler;
			}
		}

		private void DetachSessionHandlers(ISessionContext context, SmtpSessionState state)
		{
			if (state.CommandExecutingHandler != null)
			{
				context.CommandExecuting -= state.CommandExecutingHandler;
				state.CommandExecutingHandler = null;
			}

			if (state.ResponseExceptionHandler != null)
			{
				context.ResponseException -= state.ResponseExceptionHandler;
				state.ResponseExceptionHandler = null;
			}
		}

		private void OnCommandExecuting(ISessionContext context, SmtpCommand command)
		{
			if (command == null)
				return;

			var state = SmtpSessionStateAccessor.GetOrCreate(context);
			state.LastCommandName = command.GetType().Name;

			switch (command)
			{
				case EhloCommand ehloCommand:
					LogClientIdentity(state, ehloCommand.DomainOrAddress, "EHLO");
					break;
				case HeloCommand heloCommand:
					LogClientIdentity(state, heloCommand.DomainOrAddress, "HELO");
					break;
				case ProxyCommand proxyCommand:
					state.ProxySourceEndPoint = proxyCommand.SourceEndpoint?.ToString();
					state.ProxyDestinationEndPoint = proxyCommand.DestinationEndpoint?.ToString();
					_logger.Information(
						"SMTP proxy metadata received. SessionId={SessionId} ProxySourceEndPoint={ProxySourceEndPoint} ProxyDestinationEndPoint={ProxyDestinationEndPoint}",
						state.SessionId,
						state.ProxySourceEndPoint,
						state.ProxyDestinationEndPoint);
					break;
			}
		}

		private void OnResponseException(ISessionContext context, SmtpResponseException exception)
		{
			var state = SmtpSessionStateAccessor.GetOrCreate(context);
			var response = exception?.Response;

			_logger.Warning(
				"SMTP command rejected. SessionId={SessionId} RemoteEndPoint={RemoteEndPoint} Command={Command} ReplyCode={ReplyCode} ResponseMessage={ResponseMessage}",
				state.SessionId,
				state.RemoteEndPoint,
				state.LastCommandName,
				response?.ReplyCode,
				response?.Message);

			_countlyClient.TrackEvent("smtp_command_rejected", MergeSegmentations(
				BuildConnectionSegmentation(state),
				new Dictionary<string, string>
				{
					["command"] = NormalizeSegmentationValue(state.LastCommandName),
					["reply_code"] = response == null ? "unknown" : response.ReplyCode.ToString()
				}));
		}

		private void LogClientIdentity(SmtpSessionState state, string identity, string commandName)
		{
			if (String.IsNullOrWhiteSpace(identity))
				return;

			state.ClientIdentity = identity.Trim();
			_logger.Information(
				"SMTP client identified. SessionId={SessionId} RemoteEndPoint={RemoteEndPoint} Command={Command} Identity={Identity}",
				state.SessionId,
				state.RemoteEndPoint,
				commandName,
				state.ClientIdentity);

			_countlyClient.TrackEvent("smtp_client_identified", MergeSegmentations(
				BuildConnectionSegmentation(state),
				new Dictionary<string, string>
				{
					["command"] = commandName.ToLowerInvariant(),
					["identity"] = NormalizeSegmentationValue(state.ClientIdentity)
				}));
		}

		private void CaptureException(Exception exception, SmtpSessionState state, SmtpMessageSummary message, string scopeName)
		{
			if (_sentrySdk == null || exception == null)
				return;

			try
			{
				SentrySdk.CaptureException(exception, scope =>
				{
					scope.SetTag("relay.mode", "smtp");
					scope.SetTag("smtp.scope", scopeName);
					scope.SetTag("smtp.environment", _environment);
					scope.SetTag("smtp.server_name", _serverName);
					if (state != null)
					{
						scope.SetTag("smtp.session_id", state.SessionId.ToString());
						scope.SetExtra("smtp.remote_endpoint", state.RemoteEndPoint);
						scope.SetExtra("smtp.local_endpoint", state.LocalEndPoint);
						scope.SetExtra("smtp.proxy_source_endpoint", state.ProxySourceEndPoint);
						scope.SetExtra("smtp.proxy_destination_endpoint", state.ProxyDestinationEndPoint);
						scope.SetExtra("smtp.client_identity", state.ClientIdentity);
						scope.SetExtra("smtp.sender", state.Sender);
						scope.SetExtra("smtp.message_count", state.MessageCount);
						scope.SetExtra("smtp.accepted_recipients", state.AcceptedRecipients.ToArray());
						scope.SetExtra("smtp.rejected_recipients", state.RejectedRecipients.ToArray());
					}

					if (message != null)
					{
						scope.SetTag("smtp.route_kind", message.RouteKind);
						scope.SetExtra("smtp.stable_message_id", message.StableMessageId);
						scope.SetExtra("smtp.message_id", message.ReferenceMessageId);
						scope.SetExtra("smtp.subject", message.Subject);
						scope.SetExtra("smtp.nature", message.Nature);
						scope.SetExtra("smtp.from_addresses", message.FromAddresses);
						scope.SetExtra("smtp.to_addresses", message.ToAddresses);
						scope.SetExtra("smtp.cc_addresses", message.CcAddresses);
						scope.SetExtra("smtp.dispatch_targets", message.DispatchTargets);
						scope.SetExtra("smtp.attachment_names", message.AttachmentNames);
						scope.SetExtra("smtp.oversized_attachment_names", message.OversizedAttachmentNames);
						scope.SetExtra("smtp.raw_message_path", message.RawMessagePath);
						scope.SetExtra("smtp.call_id", message.CallId);
					}
				});
			}
			catch (Exception sentryException)
			{
				_logger.Warning(sentryException, "Failed sending SMTP exception to Sentry");
			}
		}

		private Dictionary<string, string> BuildConnectionSegmentation(SmtpSessionState state)
		{
			var segmentation = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["secure"] = state.IsSecure ? "true" : "false",
				["message_count"] = state.MessageCount.ToString(CultureInfo.InvariantCulture),
				["accepted_recipient_count"] = state.AcceptedRecipientCount.ToString(CultureInfo.InvariantCulture),
				["rejected_recipient_count"] = state.RejectedRecipientCount.ToString(CultureInfo.InvariantCulture)
			};

			AddSegmentationValue(segmentation, "sender_domain", GetDomain(state.Sender));
			AddSegmentationValue(segmentation, "client_identity", state.ClientIdentity);
			return segmentation;
		}

		private Dictionary<string, string> BuildMessageSegmentation(SmtpSessionState state, SmtpMessageSummary message)
		{
			var segmentation = BuildConnectionSegmentation(state);
			segmentation["dispatch_target_count"] = message.DispatchTargetCount.ToString(CultureInfo.InvariantCulture);
			segmentation["attachment_count"] = message.AttachmentCount.ToString(CultureInfo.InvariantCulture);
			segmentation["oversized_attachment_count"] = message.OversizedAttachmentCount.ToString(CultureInfo.InvariantCulture);
			AddSegmentationValue(segmentation, "sender_domain", message.SenderDomain);
			AddSegmentationValue(segmentation, "route_kind", message.RouteKind);
			return segmentation;
		}

		private static Dictionary<string, string> MergeSegmentations(
			Dictionary<string, string> baseSegmentation,
			Dictionary<string, string> additionalSegmentation)
		{
			var segmentation = new Dictionary<string, string>(baseSegmentation, StringComparer.OrdinalIgnoreCase);
			if (additionalSegmentation == null)
				return segmentation;

			foreach (var item in additionalSegmentation)
			{
				if (!String.IsNullOrWhiteSpace(item.Value))
					segmentation[item.Key] = item.Value;
			}

			return segmentation;
		}

		private static void AddSegmentationValue(Dictionary<string, string> segmentation, string key, string value)
		{
			var normalizedValue = NormalizeSegmentationValue(value);
			if (!String.IsNullOrWhiteSpace(normalizedValue))
				segmentation[key] = normalizedValue;
		}

		private static string NormalizeSegmentationValue(string value)
		{
			if (String.IsNullOrWhiteSpace(value))
				return null;

			value = value.Trim().Replace(Environment.NewLine, " ").Replace('\r', ' ').Replace('\n', ' ');
			return value.Length <= 120 ? value : value.Substring(0, 120);
		}

		private static string ResolveEnvironment(RelayTelemetryOptions options)
		{
			return FirstNonEmpty(
					options?.Environment,
					Environment.GetEnvironmentVariable("RELAY_ENVIRONMENT"),
					Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"),
					Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
					"production")
				.Trim();
		}

		private static string ResolveRelease(RelayTelemetryOptions options)
		{
			return FirstNonEmpty(
				options?.Sentry?.Release,
				$"resgrid-relay@{Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown"}");
		}

		private static string FirstNonEmpty(params string[] values)
		{
			return values.FirstOrDefault(x => !String.IsNullOrWhiteSpace(x)) ?? String.Empty;
		}

		private static string FormatMailbox(IMailbox mailbox)
		{
			if (mailbox == null)
				return null;
			if (String.IsNullOrWhiteSpace(mailbox.Host))
				return mailbox.User;

			return $"{mailbox.User}@{mailbox.Host}";
		}

		private static string GetDomain(string emailAddress)
		{
			if (String.IsNullOrWhiteSpace(emailAddress))
				return null;

			var delimiterIndex = emailAddress.LastIndexOf('@');
			if (delimiterIndex < 0 || delimiterIndex == emailAddress.Length - 1)
				return null;

			return emailAddress.Substring(delimiterIndex + 1).Trim();
		}
	}

	internal sealed class SmtpMessageSummary
	{
		private SmtpMessageSummary()
		{
		}

		public string StableMessageId { get; private set; }
		public string ReferenceMessageId { get; private set; }
		public string Subject { get; private set; }
		public string Nature { get; private set; }
		public string[] FromAddresses { get; private set; } = Array.Empty<string>();
		public string[] ToAddresses { get; private set; } = Array.Empty<string>();
		public string[] CcAddresses { get; private set; } = Array.Empty<string>();
		public string[] DispatchTargets { get; private set; } = Array.Empty<string>();
		public string[] AttachmentNames { get; private set; } = Array.Empty<string>();
		public string[] OversizedAttachmentNames { get; private set; } = Array.Empty<string>();
		public string RawMessagePath { get; set; }
		public string CallId { get; set; }
		public int BodyLength { get; private set; }
		public int DepartmentDispatchCount { get; private set; }
		public int GroupDispatchCount { get; private set; }
		public int GroupMessageDispatchCount { get; private set; }
		public int DistributionListDispatchCount { get; private set; }
		public int AttachmentCount => AttachmentNames.Length;
		public int OversizedAttachmentCount => OversizedAttachmentNames.Length;
		public int DispatchTargetCount => DepartmentDispatchCount + GroupDispatchCount + GroupMessageDispatchCount + DistributionListDispatchCount;
		public string SenderDomain => GetDomain(FromAddresses.FirstOrDefault());

		public string RouteKind
		{
			get
			{
				var kinds = new List<string>(4);
				if (DepartmentDispatchCount > 0) kinds.Add("department");
				if (GroupDispatchCount > 0) kinds.Add("group");
				if (GroupMessageDispatchCount > 0) kinds.Add("groupmessage");
				if (DistributionListDispatchCount > 0) kinds.Add("distributionlist");

				return kinds.Count switch
				{
					0 => "none",
					1 => kinds[0],
					_ => "mixed"
				};
			}
		}

		public static SmtpMessageSummary Create(MimeMessage message, string stableMessageId, string subject, string nature, string messageBody)
		{
			if (message == null)
				throw new ArgumentNullException(nameof(message));

			return new SmtpMessageSummary
			{
				StableMessageId = stableMessageId,
				ReferenceMessageId = NormalizeMessageId(message.MessageId),
				Subject = subject ?? String.Empty,
				Nature = nature ?? String.Empty,
				FromAddresses = message.From.Mailboxes.Select(x => x.Address).Where(x => !String.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
				ToAddresses = message.To.Mailboxes.Select(x => x.Address).Where(x => !String.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
				CcAddresses = message.Cc.Mailboxes.Select(x => x.Address).Where(x => !String.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
				BodyLength = messageBody?.Length ?? 0
			};
		}

		public void SetDispatchTargets(IEnumerable<DispatchCode> dispatchTargets)
		{
			var items = dispatchTargets?.ToList() ?? new List<DispatchCode>();
			DepartmentDispatchCount = items.Count(x => x.Type == DispatchCodeType.Department);
			GroupDispatchCount = items.Count(x => x.Type == DispatchCodeType.Group);
			GroupMessageDispatchCount = items.Count(x => x.Type == DispatchCodeType.GroupMessage);
			DistributionListDispatchCount = items.Count(x => x.Type == DispatchCodeType.DistributionList);
			DispatchTargets = items.Select(x => $"{(int)x.Type}:{x.Code}").ToArray();
		}

		public void SetAttachments(IEnumerable<AttachmentPayload> attachments, int maxAttachmentBytes)
		{
			var items = attachments?.ToList() ?? new List<AttachmentPayload>();
			AttachmentNames = items.Select(x => x.Name).ToArray();
			OversizedAttachmentNames = items.Where(x => x.Data.Length > maxAttachmentBytes).Select(x => x.Name).ToArray();
		}

		private static string NormalizeMessageId(string messageId)
		{
			if (String.IsNullOrWhiteSpace(messageId))
				return null;

			return messageId.Trim('<', '>', ' ');
		}

		private static string GetDomain(string emailAddress)
		{
			if (String.IsNullOrWhiteSpace(emailAddress))
				return null;

			var delimiterIndex = emailAddress.LastIndexOf('@');
			if (delimiterIndex < 0 || delimiterIndex == emailAddress.Length - 1)
				return null;

			return emailAddress.Substring(delimiterIndex + 1).Trim();
		}
	}

	internal sealed class SmtpSessionState
	{
		private readonly HashSet<string> _acceptedRecipients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		private readonly HashSet<string> _rejectedRecipients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		public SmtpSessionState(Guid sessionId, string remoteEndPoint, string localEndPoint, bool isSecure)
		{
			SessionId = sessionId;
			RemoteEndPoint = remoteEndPoint;
			LocalEndPoint = localEndPoint;
			IsSecure = isSecure;
			StartedAtUtc = DateTimeOffset.UtcNow;
		}

		public Guid SessionId { get; }
		public DateTimeOffset StartedAtUtc { get; }
		public string RemoteEndPoint { get; }
		public string LocalEndPoint { get; }
		public bool IsSecure { get; }
		public string ProxySourceEndPoint { get; set; }
		public string ProxyDestinationEndPoint { get; set; }
		public string ClientIdentity { get; set; }
		public string Sender { get; set; }
		public string LastCommandName { get; set; }
		public bool MessageFailureReported { get; set; }
		public int MessageCount { get; private set; }
		public int AcceptedRecipientCount => _acceptedRecipients.Count;
		public int RejectedRecipientCount => _rejectedRecipients.Count;
		public IEnumerable<string> AcceptedRecipients => _acceptedRecipients;
		public IEnumerable<string> RejectedRecipients => _rejectedRecipients;
		public EventHandler<SmtpCommandEventArgs> CommandExecutingHandler { get; set; }
		public EventHandler<SmtpResponseExceptionEventArgs> ResponseExceptionHandler { get; set; }

		public void IncrementMessageCount()
		{
			MessageCount++;
		}

		public void RecordAcceptedRecipient(string recipient)
		{
			if (!String.IsNullOrWhiteSpace(recipient))
				_acceptedRecipients.Add(recipient);
		}

		public void RecordRejectedRecipient(string recipient)
		{
			if (!String.IsNullOrWhiteSpace(recipient))
				_rejectedRecipients.Add(recipient);
		}
	}

	internal static class SmtpSessionStateAccessor
	{
		private const string SessionStateKey = "Resgrid:SmtpSessionState";
		private const string LocalEndPointKey = "EndpointListener:LocalEndPoint";
		private const string RemoteEndPointKey = "EndpointListener:RemoteEndPoint";

		public static SmtpSessionState GetOrCreate(ISessionContext context)
		{
			if (context == null)
				throw new ArgumentNullException(nameof(context));

			if (context.Properties.TryGetValue(SessionStateKey, out var value) && value is SmtpSessionState existing)
				return existing;

			var state = new SmtpSessionState(
				context.SessionId,
				ResolveEndpoint(context.Properties, RemoteEndPointKey),
				ResolveEndpoint(context.Properties, LocalEndPointKey),
				context.Pipe?.IsSecure ?? false);

			context.Properties[SessionStateKey] = state;
			return state;
		}

		private static string ResolveEndpoint(IDictionary<string, object> properties, string key)
		{
			if (properties == null || !properties.TryGetValue(key, out var value) || value == null)
				return "unknown";

			return value.ToString();
		}
	}

	internal sealed class CountlyTelemetryClient : IAsyncDisposable
	{
		private readonly ILogger _logger;
		private readonly HttpClient _httpClient;
		private readonly Channel<CountlyEventRequest> _eventChannel;
		private readonly Task _worker;
		private readonly string _appKey;
		private readonly string _deviceId;
		private readonly string _environment;
		private readonly string _release;

		public CountlyTelemetryClient(ILogger logger, CountlyTelemetryOptions options, string serverName, string environment, string release)
		{
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
			_environment = environment;
			_release = release;

			if (options == null ||
				String.IsNullOrWhiteSpace(options.Url) ||
				String.IsNullOrWhiteSpace(options.AppKey))
			{
				return;
			}

			_appKey = options.AppKey.Trim();
			_deviceId = String.IsNullOrWhiteSpace(options.DeviceId)
				? $"{Environment.MachineName}:{serverName}"
				: options.DeviceId.Trim();

			_httpClient = new HttpClient
			{
				BaseAddress = new Uri(BuildCountlyBaseUri(options.Url)),
				Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds <= 0 ? 5 : options.RequestTimeoutSeconds)
			};

			_eventChannel = Channel.CreateUnbounded<CountlyEventRequest>(new UnboundedChannelOptions
			{
				SingleReader = true,
				SingleWriter = false
			});

			_worker = Task.Run(ProcessQueueAsync);
		}

		public bool IsEnabled => _eventChannel != null;

		public void TrackEvent(string key, Dictionary<string, string> segmentation = null, TimeSpan? duration = null)
		{
			if (!IsEnabled || String.IsNullOrWhiteSpace(key))
				return;

			var eventRequest = new CountlyEventRequest
			{
				Key = key.Trim(),
				DurationSeconds = duration?.TotalSeconds,
				Segmentation = MergeBaseSegmentation(segmentation)
			};

			if (!_eventChannel.Writer.TryWrite(eventRequest))
			{
				_logger.Warning("Countly event queue rejected SMTP telemetry event {EventKey}", key);
			}
		}

		public async ValueTask DisposeAsync()
		{
			if (!IsEnabled)
				return;

			_eventChannel.Writer.TryComplete();
			await _worker.ConfigureAwait(false);
			_httpClient.Dispose();
		}

		private async Task ProcessQueueAsync()
		{
			while (await _eventChannel.Reader.WaitToReadAsync().ConfigureAwait(false))
			{
				while (_eventChannel.Reader.TryRead(out var eventRequest))
				{
					try
					{
						await SendEventAsync(eventRequest).ConfigureAwait(false);
					}
					catch (Exception ex)
					{
						_logger.Warning(ex, "Failed sending SMTP telemetry event {EventKey} to Countly", eventRequest.Key);
					}
				}
			}
		}

		private async Task SendEventAsync(CountlyEventRequest eventRequest)
		{
			var payload = JsonSerializer.Serialize(new[]
			{
				new CountlyEventPayload
				{
					Key = eventRequest.Key,
					Count = 1,
					Duration = eventRequest.DurationSeconds,
					Segmentation = eventRequest.Segmentation,
					Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
				}
			});

			using var content = new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["app_key"] = _appKey,
				["device_id"] = _deviceId,
				["events"] = payload
			});

			using var response = await _httpClient.PostAsync("i", content).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
				_logger.Warning(
					"Countly rejected SMTP telemetry event {EventKey} with status {StatusCode}. Response={ResponseBody}",
					eventRequest.Key,
					(int)response.StatusCode,
					TrimForLog(responseBody, 500));
			}
		}

		private Dictionary<string, string> MergeBaseSegmentation(Dictionary<string, string> segmentation)
		{
			var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			if (segmentation != null)
			{
				foreach (var entry in segmentation.Where(x => !String.IsNullOrWhiteSpace(x.Value)))
				{
					merged[entry.Key] = entry.Value;
				}
			}

			merged["environment"] = _environment;
			merged["release"] = _release;
			return merged;
		}

		private static string BuildCountlyBaseUri(string url)
		{
			return $"{url.Trim().TrimEnd('/')}/";
		}

		private static string TrimForLog(string value, int maxLength)
		{
			if (String.IsNullOrWhiteSpace(value))
				return value;

			value = value.Trim().Replace(Environment.NewLine, " ").Replace('\r', ' ').Replace('\n', ' ');
			return value.Length <= maxLength ? value : value.Substring(0, maxLength);
		}

		private sealed class CountlyEventRequest
		{
			public string Key { get; set; }
			public Dictionary<string, string> Segmentation { get; set; }
			public double? DurationSeconds { get; set; }
		}

		private sealed class CountlyEventPayload
		{
			[JsonPropertyName("key")]
			public string Key { get; set; }

			[JsonPropertyName("count")]
			public int Count { get; set; }

			[JsonPropertyName("dur")]
			[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
			public double? Duration { get; set; }

			[JsonPropertyName("segmentation")]
			[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
			public Dictionary<string, string> Segmentation { get; set; }

			[JsonPropertyName("timestamp")]
			public long Timestamp { get; set; }
		}
	}
}
