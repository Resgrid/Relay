using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Resgrid.Providers.ApiClient.V4.Models;

namespace Resgrid.Relay.Engine.Voice
{
	/// <summary>Shared helpers for the cross-platform voice modes.</summary>
	public static class VoiceModeRuntime
	{
		/// <summary>Awaits until cancellation without throwing.</summary>
		public static async Task WaitForCancellationAsync(CancellationToken cancellationToken)
		{
			try
			{
				await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
			}
			catch (TaskCanceledException)
			{
				// expected on shutdown
			}
		}

		/// <summary>Builds a speakable dispatch announcement from a call.</summary>
		public static string FormatCallAnnouncement(CallResultData call)
		{
			if (call == null)
				return string.Empty;

			var sb = new StringBuilder();
			sb.Append("Attention. ");

			if (!string.IsNullOrWhiteSpace(call.Name))
				sb.Append(call.Name).Append(". ");

			if (!string.IsNullOrWhiteSpace(call.Nature))
				sb.Append(call.Nature).Append(". ");

			if (!string.IsNullOrWhiteSpace(call.Address))
				sb.Append("Location, ").Append(call.Address).Append(". ");

			sb.Append("Priority ").Append(call.Priority).Append('.');

			if (!string.IsNullOrWhiteSpace(call.Number))
				sb.Append(" Call number ").Append(call.Number).Append('.');

			return sb.ToString();
		}
	}
}
