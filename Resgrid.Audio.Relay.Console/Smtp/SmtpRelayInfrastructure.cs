using MimeKit;
using Resgrid.Audio.Relay.Console.Configuration;
using Resgrid.Providers.ApiClient.V4;
using Resgrid.Providers.ApiClient.V4.Models;
using SmtpServer;
using SmtpServer.ComponentModel;
using SmtpServer.Mail;
using SmtpServer.Protocol;
using SmtpServer.Storage;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Resgrid.Audio.Relay.Console.Smtp
{
	internal static class SmtpRelayRunner
	{
		public static async Task RunAsync(SmtpRelayOptions options, ISmtpTelemetry telemetry, CancellationToken cancellationToken)
		{
			if (options == null)
				throw new ArgumentNullException(nameof(options));
			if (telemetry == null)
				throw new ArgumentNullException(nameof(telemetry));

			var serviceProvider = new ServiceProvider();
			serviceProvider.Add((IMailboxFilterFactory)new RelayMailboxFilter(options, telemetry));
			serviceProvider.Add(new RelayMessageStore(options, telemetry));

			var smtpServerOptions = new SmtpServerOptionsBuilder()
				.ServerName(options.ServerName)
				.Port(options.Port)
				.MaxMessageSize(options.MaxMessageBytes, MaxMessageSizeHandling.Strict)
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

	internal interface IResgridCallsClient
	{
		Task<string> SaveCallAsync(NewCallInput call, CancellationToken cancellationToken);
		Task<string> SaveCallFileAsync(SaveCallFileInput file, CancellationToken cancellationToken);
	}

	internal sealed class ResgridCallsClient : IResgridCallsClient
	{
		public Task<string> SaveCallAsync(NewCallInput call, CancellationToken cancellationToken)
		{
			return CallsApi.SaveCallAsync(call, cancellationToken);
		}

		public Task<string> SaveCallFileAsync(SaveCallFileInput file, CancellationToken cancellationToken)
		{
			return CallsApi.SaveCallFileAsync(file, cancellationToken);
		}
	}

	internal sealed class AttachmentPayload
	{
		public string Name { get; set; }
		public byte[] Data { get; set; }
		public CallFileType Type { get; set; }
	}

	internal sealed class RelayMessageStore : MessageStore
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
		private readonly string _dataDirectory;
		private readonly string _messageDirectory;

		public RelayMessageStore(SmtpRelayOptions options, ISmtpTelemetry telemetry, IResgridCallsClient callsClient = null)
		{
			_options = options ?? throw new ArgumentNullException(nameof(options));
			_telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
			_callsClient = callsClient ?? new ResgridCallsClient();
			_dispatchAddressParser = new SmtpDispatchAddressParser(options);
			_dataDirectory = ResolvePath(options.DataDirectory);
			_messageDirectory = Path.Combine(_dataDirectory, "messages");
			_processedMessageStore = new ProcessedMessageStore(Path.Combine(_dataDirectory, "processed-messages.json"), options.DuplicateWindowHours);

			Directory.CreateDirectory(_dataDirectory);
			Directory.CreateDirectory(_messageDirectory);
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
			var message = await MimeMessage.LoadAsync(stream, cancellationToken).ConfigureAwait(false);
			var subject = String.IsNullOrWhiteSpace(message.Subject) ? "SMTP Email Import" : message.Subject.Trim();
			var messageBody = GetMessageBody(message);
			var nature = GetNature(messageBody, subject);
			var stableMessageId = GetStableMessageId(message);
			var messageSummary = SmtpMessageSummary.Create(message, stableMessageId, subject, nature, messageBody);
			_telemetry.MessageReceived(context, messageSummary);

			var messageRegistered = false;
			var processingStopwatch = Stopwatch.StartNew();

			try
			{
				if (!await _processedMessageStore.TryRegisterAsync(stableMessageId, cancellationToken).ConfigureAwait(false))
				{
					_telemetry.DuplicateMessage(context, messageSummary);
					return SmtpResponse.Ok;
				}

				messageRegistered = true;

				if (_options.SaveRawMessages)
				{
					stream.Position = 0;
					var rawMessagePath = Path.Combine(_messageDirectory, $"{SanitizeFileName(stableMessageId)}.eml");
					await using var fileStream = File.Create(rawMessagePath);
					await stream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
					messageSummary.RawMessagePath = rawMessagePath;
				}

				var dispatchTargets = GetDispatchTargets(message);
				messageSummary.SetDispatchTargets(dispatchTargets);
				if (dispatchTargets.Count == 0)
				{
					_telemetry.UnroutableMessage(context, messageSummary);
					return SmtpResponse.Ok;
				}

				var fromMailbox = message.From.Mailboxes.FirstOrDefault();
				var attachments = ExtractAttachments(message).ToList();
				messageSummary.SetAttachments(attachments, _options.MaxAttachmentBytes);
				_telemetry.MessageProcessingStarted(context, messageSummary);

				var skippedAttachments = new List<string>();
				foreach (var attachment in attachments.Where(x => x.Data.Length > _options.MaxAttachmentBytes))
				{
					skippedAttachments.Add($"{attachment.Name} skipped because it exceeded {_options.MaxAttachmentBytes} bytes.");
				}

				var callId = await _callsClient.SaveCallAsync(new NewCallInput
				{
					Priority = _options.DefaultCallPriority,
					Name = Trim(subject, 200),
					Nature = Trim(nature, 500),
					Note = BuildNote(message, messageBody, skippedAttachments),
					DispatchList = DispatchListBuilder.Build(dispatchTargets, _options.DepartmentDispatchPrefix),
					ContactName = fromMailbox == null ? null : Trim(String.IsNullOrWhiteSpace(fromMailbox.Name) ? fromMailbox.Address : fromMailbox.Name, 200),
					ContactInfo = fromMailbox?.Address,
					ExternalId = stableMessageId,
					ReferenceId = message.MessageId
				}, cancellationToken).ConfigureAwait(false);

				messageSummary.CallId = callId;

				var uploadableAttachments = attachments.Where(x => x.Data.Length <= _options.MaxAttachmentBytes).ToList();
				if (uploadableAttachments.Count > 0)
				{
					var userId = ResgridV4ApiClient.CurrentUserId;
					if (String.IsNullOrWhiteSpace(userId))
						throw new InvalidOperationException("The Resgrid access token did not contain a user id required to upload SMTP message attachments.");

					foreach (var attachment in uploadableAttachments)
					{
						await _callsClient.SaveCallFileAsync(new SaveCallFileInput
						{
							CallId = callId,
							UserId = userId,
							Type = (int)attachment.Type,
							Name = attachment.Name,
							Data = Convert.ToBase64String(attachment.Data),
							Note = $"SMTP attachment imported from message {stableMessageId}"
						}, cancellationToken).ConfigureAwait(false);
					}
				}

				_telemetry.MessageProcessed(context, messageSummary, processingStopwatch.Elapsed);
				return SmtpResponse.Ok;
			}
			catch (Exception ex)
			{
				_telemetry.MessageFailed(context, messageSummary, ex, processingStopwatch.Elapsed);

				if (messageRegistered)
				{
					try
					{
						await _processedMessageStore.RemoveAsync(stableMessageId, cancellationToken).ConfigureAwait(false);
					}
					catch (Exception cleanupException)
					{
						throw new AggregateException("SMTP processing failed and the duplicate-message registration could not be rolled back.", ex, cleanupException);
					}
				}

				throw;
			}
		}

		private List<DispatchCode> GetDispatchTargets(MimeMessage message)
		{
			return _dispatchAddressParser.ParseRecipients(
				message.To.Mailboxes
					.Concat(message.Cc.Mailboxes)
					.Select(x => x.Address));
		}

		private string BuildNote(MimeMessage message, string messageBody, List<string> skippedAttachments)
		{
			var builder = new StringBuilder();
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
				return message.HtmlBody.Trim();

			return "(No message body)";
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

		private static string GetStableMessageId(MimeMessage message)
		{
			if (!String.IsNullOrWhiteSpace(message.MessageId))
				return message.MessageId.Trim('<', '>', ' ');

			var rawId = $"{message.Subject}|{String.Join(",", message.From.Mailboxes.Select(x => x.Address))}|{String.Join(",", message.To.Mailboxes.Select(x => x.Address))}|{GetMessageBody(message)}";
			return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawId)));
		}

		private static string ResolvePath(string path)
		{
			if (Path.IsPathRooted(path))
				return path;

			return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
		}

		private static string SanitizeFileName(string value)
		{
			var invalidCharacters = Path.GetInvalidFileNameChars();
			return new string(value.Select(x => invalidCharacters.Contains(x) ? '_' : x).ToArray());
		}

		private static string Trim(string value, int maxLength)
		{
			if (String.IsNullOrWhiteSpace(value))
				return value;

			return value.Length <= maxLength ? value : value.Substring(0, maxLength);
		}
	}

	internal sealed class RelayMailboxFilter : IMailboxFilter, IMailboxFilterFactory
	{
		private readonly SmtpRelayOptions _options;
		private readonly ISmtpTelemetry _telemetry;

		public RelayMailboxFilter(SmtpRelayOptions options, ISmtpTelemetry telemetry)
		{
			_options = options ?? throw new ArgumentNullException(nameof(options));
			_telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
		}

		public Task<bool> CanAcceptFromAsync(ISessionContext context, IMailbox from, int size, CancellationToken cancellationToken)
		{
			_telemetry.SenderAccepted(context, from, size);
			return Task.FromResult(true);
		}

		public Task<bool> CanDeliverToAsync(ISessionContext context, IMailbox to, IMailbox from, CancellationToken token)
		{
			var accepted =
				(_options.DepartmentAddressDomains ?? Array.Empty<string>()).Any(x => String.Equals(x, to.Host, StringComparison.OrdinalIgnoreCase)) ||
				(_options.GroupAddressDomains ?? Array.Empty<string>()).Any(x => String.Equals(x, to.Host, StringComparison.OrdinalIgnoreCase));

			_telemetry.RecipientEvaluated(
				context,
				to,
				from,
				accepted,
				accepted ? null : "recipient domain is not configured for Resgrid dispatch routing");

			return Task.FromResult(accepted);
		}

		public IMailboxFilter CreateInstance(ISessionContext context)
		{
			return this;
		}
	}

	internal sealed class ProcessedMessageStore
	{
		private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
		{
			WriteIndented = true
		};

		private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
		private readonly string _path;
		private readonly TimeSpan _retention;

		public ProcessedMessageStore(string path, int duplicateWindowHours)
		{
			_path = path;
			_retention = TimeSpan.FromHours(duplicateWindowHours <= 0 ? 72 : duplicateWindowHours);
		}

		public async Task<bool> TryRegisterAsync(string messageId, CancellationToken cancellationToken)
		{
			await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
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

				var changed = expiredKeys.Length > 0;
				if (entries.ContainsKey(messageId))
				{
					if (changed)
						await SaveAsync(entries, cancellationToken).ConfigureAwait(false);

					return false;
				}

				entries[messageId] = DateTimeOffset.UtcNow;
				await SaveAsync(entries, cancellationToken).ConfigureAwait(false);
				return true;
			}
			finally
			{
				_gate.Release();
			}
		}

		public async Task RemoveAsync(string messageId, CancellationToken cancellationToken)
		{
			await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				var entries = await LoadAsync(cancellationToken).ConfigureAwait(false);
				if (!entries.Remove(messageId))
					return;

				await SaveAsync(entries, cancellationToken).ConfigureAwait(false);
			}
			finally
			{
				_gate.Release();
			}
		}

		private async Task<Dictionary<string, DateTimeOffset>> LoadAsync(CancellationToken cancellationToken)
		{
			var directory = Path.GetDirectoryName(_path);
			if (!String.IsNullOrWhiteSpace(directory))
				Directory.CreateDirectory(directory);

			if (!File.Exists(_path))
				return new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

			var payload = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
			if (String.IsNullOrWhiteSpace(payload))
				return new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

			var items = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(payload);
			return items ?? new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
		}

		private async Task SaveAsync(Dictionary<string, DateTimeOffset> entries, CancellationToken cancellationToken)
		{
			var payload = JsonSerializer.Serialize(entries, SerializerOptions);
			await File.WriteAllTextAsync(_path, payload, cancellationToken).ConfigureAwait(false);
		}
	}
}
