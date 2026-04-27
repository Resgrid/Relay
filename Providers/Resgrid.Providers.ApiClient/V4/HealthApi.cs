using System.Threading;
using System.Threading.Tasks;

namespace Resgrid.Providers.ApiClient.V4
{
	public static class HealthApi
	{
		public static Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
		{
			return ResgridV4ApiClient.IsHealthyAsync(cancellationToken);
		}
	}
}
