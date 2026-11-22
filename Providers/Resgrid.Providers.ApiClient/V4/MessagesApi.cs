using Resgrid.Providers.ApiClient.V4.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Resgrid.Providers.ApiClient.V4
{
	/// <summary>
	/// v4 API methods for Resgrid messages. Used for group-message email (code@group-message-domain), which Resgrid turns into a
	/// broadcast message to the group's members rather than a call.
	///
	/// Maps to: POST /api/v4/Messages/SendMessage. The Messages controller only accepts user/department tokens (OpenIddict); the
	/// system API key cannot send messages.
	/// </summary>
	public interface IResgridMessagesApi
	{
		Task<string> SendMessageAsync(NewMessageInput message, CancellationToken cancellationToken = default);
	}

	public sealed class MessagesApi : IResgridMessagesApi
	{
		private readonly IResgridApiClient _client;

		public MessagesApi(IResgridApiClient client)
		{
			_client = client ?? throw new ArgumentNullException(nameof(client));
		}

		public async Task<string> SendMessageAsync(NewMessageInput message, CancellationToken cancellationToken = default)
		{
			if (message == null)
				throw new ArgumentNullException(nameof(message));

			var result = await _client.PostAsync<SaveOperationResult>("Messages/SendMessage", message, cancellationToken).ConfigureAwait(false);
			return result?.Id;
		}
	}
}
