using System;
using System.Threading;
using System.Threading.Tasks;

namespace Resgrid.Providers.ApiClient.V4
{
	public interface IResgridHealthApi
	{
		Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default);
	}

	public sealed class HealthApi : IResgridHealthApi
	{
		private readonly IResgridApiClient _client;

		public HealthApi(IResgridApiClient client)
		{
			_client = client ?? throw new ArgumentNullException(nameof(client));
		}

		public Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
		{
			return _client.IsHealthyAsync(cancellationToken);
		}
	}
}
