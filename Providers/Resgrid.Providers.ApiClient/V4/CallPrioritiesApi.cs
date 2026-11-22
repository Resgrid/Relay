using Resgrid.Providers.ApiClient.V4.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Resgrid.Providers.ApiClient.V4
{
	/// <summary>
	/// The department's call priorities, so a call's numeric Priority can be spoken by name (custom priorities have their own
	/// ids and names; without any, Resgrid returns the defaults 0-3).
	/// Maps to: GET /api/v4/CallPriorities/GetAllCallPriorites (sic). User/department tokens only.
	/// </summary>
	public interface IResgridCallPrioritiesApi
	{
		Task<List<CallPriorityResultData>> GetAllCallPrioritiesAsync(CancellationToken cancellationToken = default);
	}

	public sealed class CallPrioritiesApi : IResgridCallPrioritiesApi
	{
		private readonly IResgridApiClient _client;

		public CallPrioritiesApi(IResgridApiClient client)
		{
			_client = client ?? throw new ArgumentNullException(nameof(client));
		}

		public async Task<List<CallPriorityResultData>> GetAllCallPrioritiesAsync(CancellationToken cancellationToken = default)
		{
			var result = await _client.GetAsync<CallPrioritiesResult>("CallPriorities/GetAllCallPriorites", cancellationToken).ConfigureAwait(false);
			return result?.Data ?? new List<CallPriorityResultData>();
		}
	}
}
