using System.Threading.Tasks;
using Resgrid.Providers.ApiClient.V3.Models;

namespace Resgrid.Providers.ApiClient.V3
{
    public class CallsApi
    {
	    public static async Task<Call> AddNewCall(Call call)
	    {
		    return await ResgridV3ApiClient.Post<Call>("Calls/AddCall", call);
	    }
    }
}
