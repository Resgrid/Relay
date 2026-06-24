using Resgrid.Providers.ApiClient.V4.Models;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
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

		/// <summary>
		/// Returns the currently active (open) calls for the department, used by the
		/// dispatch tone-out mode to detect new calls to announce on a PTT channel.
		/// Maps to: GET /api/v4/Calls/GetActiveCalls[?departmentId={departmentId}].
		///
		/// Returns an empty list if the endpoint is unavailable (404) so callers can
		/// poll safely without special-casing API availability.
		/// </summary>
		public static async Task<List<CallResultData>> GetActiveCallsAsync(string departmentId = null, CancellationToken cancellationToken = default)
		{
			var url = "Calls/GetActiveCalls";
			if (!String.IsNullOrWhiteSpace(departmentId))
				url += $"?departmentId={Uri.EscapeDataString(departmentId)}";

			try
			{
				var result = await ResgridV4ApiClient.GetAsync<ActiveCallsResult>(url, cancellationToken).ConfigureAwait(false);
				return result?.Data ?? new List<CallResultData>();
			}
			catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
			{
				return new List<CallResultData>();
			}
		}
	}
}
