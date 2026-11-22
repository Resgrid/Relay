using Resgrid.Providers.ApiClient.V4.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Resgrid.Providers.ApiClient.V4
{
	public interface IResgridCallsApi
	{
		Task<string> SaveCallAsync(NewCallInput call, CancellationToken cancellationToken = default);

		/// <summary>
		/// Saves a call the caller may send again (a retried SMTP message): Resgrid returns the first call for a repeated
		/// <paramref name="idempotencyKey"/> instead of creating another, and answers 409 while the first is still in flight.
		/// </summary>
		Task<string> SaveCallAsync(NewCallInput call, string idempotencyKey, CancellationToken cancellationToken = default);
		Task<GetCallResult> GetCallAsync(string callId, string departmentId = null, CancellationToken cancellationToken = default);
		Task<string> SaveCallFileAsync(SaveCallFileInput file, CancellationToken cancellationToken = default);

		/// <summary>Saves a call file the caller may send again; a repeated <paramref name="idempotencyKey"/> returns the first file.</summary>
		Task<string> SaveCallFileAsync(SaveCallFileInput file, string idempotencyKey, CancellationToken cancellationToken = default);
		Task<List<CallResultData>> GetActiveCallsAsync(string departmentId = null, CancellationToken cancellationToken = default);
	}

	public sealed class CallsApi : IResgridCallsApi
	{
		/// <summary>Honored by the Resgrid v4 SaveCall and SaveCallFile endpoints.</summary>
		public const string IdempotencyKeyHeader = "Idempotency-Key";

		private const int MaxIdempotencyKeyLength = 200;

		private readonly IResgridApiClient _client;

		public CallsApi(IResgridApiClient client)
		{
			_client = client ?? throw new ArgumentNullException(nameof(client));
		}

		public Task<string> SaveCallAsync(NewCallInput call, CancellationToken cancellationToken = default)
		{
			return SaveCallAsync(call, null, cancellationToken);
		}

		public async Task<string> SaveCallAsync(NewCallInput call, string idempotencyKey, CancellationToken cancellationToken = default)
		{
			var result = await _client.PostAsync<SaveOperationResult>("Calls/SaveCall", call, IdempotencyHeaders(idempotencyKey), cancellationToken).ConfigureAwait(false);
			if (String.IsNullOrWhiteSpace(result.Id))
				throw new InvalidOperationException("The Resgrid API did not return a call id for the saved call.");

			return result.Id;
		}

		/// <summary>
		/// The header for a key, or null for none. A key that is not plain printable ASCII (an internationalized Message-Id) or is
		/// very long is sent as its SHA-256, which is just as stable across retries and always a valid header value.
		/// </summary>
		public static IReadOnlyDictionary<string, string> IdempotencyHeaders(string idempotencyKey)
		{
			var key = idempotencyKey?.Trim();
			if (String.IsNullOrEmpty(key))
				return null;

			if (key.Length > MaxIdempotencyKeyLength || key.Any(c => c < 0x21 || c > 0x7E))
				key = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

			return new Dictionary<string, string> { [IdempotencyKeyHeader] = key };
		}

		public async Task<GetCallResult> GetCallAsync(string callId, string departmentId = null, CancellationToken cancellationToken = default)
		{
			if (String.IsNullOrWhiteSpace(callId))
				throw new ArgumentException("A call id is required.", nameof(callId));

			var url = $"Calls/GetCall?callId={Uri.EscapeDataString(callId)}";
			if (!String.IsNullOrWhiteSpace(departmentId))
				url += $"&departmentId={Uri.EscapeDataString(departmentId)}";

			return await _client.GetAsync<GetCallResult>(url, cancellationToken).ConfigureAwait(false);
		}

		public Task<string> SaveCallFileAsync(SaveCallFileInput file, CancellationToken cancellationToken = default)
		{
			return SaveCallFileAsync(file, null, cancellationToken);
		}

		public async Task<string> SaveCallFileAsync(SaveCallFileInput file, string idempotencyKey, CancellationToken cancellationToken = default)
		{
			var result = await _client.PostAsync<SaveOperationResult>("CallFiles/SaveCallFile", file, IdempotencyHeaders(idempotencyKey), cancellationToken).ConfigureAwait(false);
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
		public async Task<List<CallResultData>> GetActiveCallsAsync(string departmentId = null, CancellationToken cancellationToken = default)
		{
			var url = "Calls/GetActiveCalls";
			if (!String.IsNullOrWhiteSpace(departmentId))
				url += $"?departmentId={Uri.EscapeDataString(departmentId)}";

			try
			{
				var result = await _client.GetAsync<ActiveCallsResult>(url, cancellationToken).ConfigureAwait(false);
				return result?.Data ?? new List<CallResultData>();
			}
			catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
			{
				return new List<CallResultData>();
			}
		}
	}
}
