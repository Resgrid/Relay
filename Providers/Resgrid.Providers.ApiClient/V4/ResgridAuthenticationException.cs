using System;
using System.Net;

namespace Resgrid.Providers.ApiClient.V4
{
	/// <summary>
	/// The relay could not obtain an access token (OIDC discovery or the token request failed). It is deliberately not an
	/// <see cref="System.Net.Http.HttpRequestException"/>: the status code belongs to the token endpoint, not to the API call that
	/// needed the token, so a 400 <c>invalid_grant</c> or a 404 discovery document must never be read as "Resgrid rejected this
	/// request" (an SMTP dispatch would be bounced) or "this entity does not exist" (a lookup would silently fall back).
	/// Credentials are fixable by an operator, so a sender may be asked to try again later; an immediate retry within the
	/// relay is only worth it when the failure <see cref="IsTransient"/>.
	/// </summary>
	public sealed class ResgridAuthenticationException : InvalidOperationException
	{
		public ResgridAuthenticationException(string message, HttpStatusCode? statusCode = null, Exception innerException = null)
			: base(message, innerException)
		{
			StatusCode = statusCode;
		}

		/// <summary>The status the discovery or token endpoint answered with, when it answered at all.</summary>
		public HttpStatusCode? StatusCode { get; }

		/// <summary>
		/// Whether the same sign-in may succeed when simply tried again shortly: the endpoint timed out, was rate limited or failed
		/// (408, 429, 5xx). Not when no refresh token is available, the endpoint rejected the credentials (400 <c>invalid_grant</c> or
		/// <c>invalid_client</c>, 401, 403) or answered with something unusable: only an operator fixes those, and retrying them
		/// just delays the report. (A network failure reaches callers as an <see cref="System.Net.Http.HttpRequestException"/>.)
		/// </summary>
		public bool IsTransient =>
			StatusCode is HttpStatusCode status && ((int)status == 408 || (int)status == 429 || (int)status >= 500);
	}
}
