using Resgrid.Providers.ApiClient.V4.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Resgrid.Providers.ApiClient.V4
{
	public static class CallsApi
	{
		public static async Task<string> SaveCallAsync(NewCallInput call, CancellationToken cancellationToken = default)
		{
			var result = await ResgridV4ApiClient.PostAsync<SaveOperationResult>("Calls/SaveCall", call, cancellationToken).ConfigureAwait(false);
			if (String.IsNullOrWhiteSpace(result.Id))
				throw new InvalidOperationException("The Resgrid API did not return a call id for the saved call.");

			return result.Id;
		}

		public static async Task<GetCallResult> GetCallAsync(string callId, string departmentId = null, CancellationToken cancellationToken = default)
		{
			if (String.IsNullOrWhiteSpace(callId))
				throw new ArgumentException("A call id is required.", nameof(callId));

			var url = $"Calls/GetCall?callId={Uri.EscapeDataString(callId)}";
			if (!String.IsNullOrWhiteSpace(departmentId))
				url += $"&departmentId={Uri.EscapeDataString(departmentId)}";

			return await ResgridV4ApiClient.GetAsync<GetCallResult>(url, cancellationToken).ConfigureAwait(false);
		}

		public static async Task<string> SaveCallFileAsync(SaveCallFileInput file, CancellationToken cancellationToken = default)
		{
			var result = await ResgridV4ApiClient.PostAsync<SaveOperationResult>("CallFiles/SaveCallFile", file, cancellationToken).ConfigureAwait(false);
			return result?.Id;
		}
	}
}
