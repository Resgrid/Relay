using System.Threading.Tasks;
using Resgrid.Providers.ApiClient.V3.Models;

namespace Resgrid.Providers.ApiClient.V3
{
	public class HealthApi
	{
		public static async Task<HealthResult> GetApiHealth()
		{
			return await ResgridV3ApiClient.Get<HealthResult>("Health/GetCurrent");
		}
	}
}
