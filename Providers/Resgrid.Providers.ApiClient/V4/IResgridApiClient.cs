using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Resgrid.Providers.ApiClient.V4
{
	public interface IResgridApiClient
	{
		string CurrentUserId { get; }

		/// <summary>
		/// True for a system caller (same as <see cref="IsSystemCaller"/>): the system API key, or a system service-account token,
		/// rather than a user/department token. Resgrid treats that caller differently:
		/// it can act for any department (calls carry a DepartmentId), it never pages a whole department on an immediate call, and
		/// the OpenIddict-only endpoints (Voice, Messages, CallNotes, CallPriorities...) refuse it.
		/// </summary>
		bool UsesSystemApiKey { get; }

		/// <summary>
		/// True when Resgrid treats this caller as a system caller: the system API key, or a client_credentials token issued to a
		/// system-level service account (<see cref="ResgridApiClientOptions.ServiceAccount"/>; Resgrid marks it with a
		/// ServiceAccount claim). Everything <see cref="UsesSystemApiKey"/> describes applies to both; only the
		/// X-Resgrid-SystemApiKey header itself is limited to the system-key grant. <see cref="UsesSystemApiKey"/> returns the
		/// same value so existing callers get these semantics.
		/// </summary>
		bool IsSystemCaller { get; }

		/// <summary>
		/// A short opaque value naming the credential the client acts with, so a cache shared between relays (Redis) keeps what
		/// one department's token can see apart from another's. The same for every client using the same credential.
		/// </summary>
		string CredentialScope { get; }
		Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default);
		Task<T> GetAsync<T>(string url, CancellationToken cancellationToken = default) where T : class;
		Task<T> PostAsync<T>(string url, object data, CancellationToken cancellationToken = default) where T : class;

		/// <summary>Posts with extra request headers, such as an Idempotency-Key; blank values are not sent.</summary>
		Task<T> PostAsync<T>(string url, object data, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken = default) where T : class;
	}
}
