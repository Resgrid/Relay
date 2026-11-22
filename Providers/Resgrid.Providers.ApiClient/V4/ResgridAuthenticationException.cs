using System;
using System.Net;

namespace Resgrid.Providers.ApiClient.V4
{
	/// <summary>
	/// The relay could not obtain an access token (OIDC discovery or the token request failed). It is deliberately not an
	/// <see cref="System.Net.Http.HttpRequestException"/>: the status code belongs to the token endpoint, not to the API call that
	/// needed the token, so a 400 <c>invalid_grant</c> or a 404 discovery document must never be read as "Resgrid rejected this
	/// request" (an SMTP dispatch would be bounced) or "this entity does not exist" (a lookup would silently fall back).
	/// Credentials are fixable by an operator, so callers should treat this as retryable.
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
	}
}
