using System.Threading;
using System.Threading.Tasks;

namespace Resgrid.Providers.ApiClient.V4
{
	public interface IResgridApiClient
	{
		string CurrentUserId { get; }
		Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default);
		Task<T> GetAsync<T>(string url, CancellationToken cancellationToken = default) where T : class;
		Task<T> PostAsync<T>(string url, object data, CancellationToken cancellationToken = default) where T : class;
	}
}
