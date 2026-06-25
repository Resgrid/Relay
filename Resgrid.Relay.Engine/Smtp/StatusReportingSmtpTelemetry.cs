using System;
using System.Threading.Tasks;
using Resgrid.Relay.Engine.Configuration;
using SmtpServer;
using SmtpServer.Mail;

namespace Resgrid.Relay.Engine.Smtp
{
	/// <summary>
	/// Decorates an inner <see cref="ISmtpTelemetry"/>, forwarding every call to it while
	/// also projecting the relay's live state onto a <see cref="RelayStatus"/>: SMTP
	/// connection state on start/stop/fault and the processed-message counter.
	/// </summary>
	public sealed class StatusReportingSmtpTelemetry : ISmtpTelemetry
	{
		private readonly ISmtpTelemetry _inner;
		private readonly RelayStatus _status;

		public StatusReportingSmtpTelemetry(ISmtpTelemetry inner, RelayStatus status)
		{
			_inner = inner ?? throw new ArgumentNullException(nameof(inner));
			_status = status ?? throw new ArgumentNullException(nameof(status));
		}

		public void RelayStarting(SmtpRelayOptions options)
		{
			_status.Smtp = ConnectionState.Connected;
			_inner.RelayStarting(options);
		}

		public void RelayStopped(SmtpRelayOptions options)
		{
			_status.Smtp = ConnectionState.Disconnected;
			_inner.RelayStopped(options);
		}

		public void RelayFaulted(SmtpRelayOptions options, Exception exception)
		{
			_status.Smtp = ConnectionState.Disconnected;
			_inner.RelayFaulted(options, exception);
		}

		public void SessionCreated(ISessionContext context) => _inner.SessionCreated(context);

		public void SessionCompleted(ISessionContext context) => _inner.SessionCompleted(context);

		public void SessionCancelled(ISessionContext context) => _inner.SessionCancelled(context);

		public void SessionFaulted(ISessionContext context, Exception exception) =>
			_inner.SessionFaulted(context, exception);

		public void SenderAccepted(ISessionContext context, IMailbox from, int size) =>
			_inner.SenderAccepted(context, from, size);

		public void RecipientEvaluated(ISessionContext context, IMailbox to, IMailbox from, bool accepted, string reason) =>
			_inner.RecipientEvaluated(context, to, from, accepted, reason);

		public void MessageReceived(ISessionContext context, SmtpMessageSummary message) =>
			_inner.MessageReceived(context, message);

		public void DuplicateMessage(ISessionContext context, SmtpMessageSummary message) =>
			_inner.DuplicateMessage(context, message);

		public void UnroutableMessage(ISessionContext context, SmtpMessageSummary message) =>
			_inner.UnroutableMessage(context, message);

		public void UnsupportedTarget(ISessionContext context, SmtpMessageSummary message) =>
			_inner.UnsupportedTarget(context, message);

		public void MessageProcessingStarted(ISessionContext context, SmtpMessageSummary message) =>
			_inner.MessageProcessingStarted(context, message);

		public void MessageProcessed(ISessionContext context, SmtpMessageSummary message, TimeSpan duration)
		{
			_status.IncrementMessagesProcessed();
			_inner.MessageProcessed(context, message, duration);
		}

		public void MessageFailed(ISessionContext context, SmtpMessageSummary message, Exception exception, TimeSpan duration) =>
			_inner.MessageFailed(context, message, exception, duration);

		public ValueTask DisposeAsync() => _inner.DisposeAsync();
	}
}
