using System;
using System.Text.Json.Serialization;

namespace Resgrid.Providers.ApiClient.V4.Models
{
	public sealed class OpenIdConfiguration
	{
		[JsonPropertyName("issuer")]
		public string Issuer { get; set; }

		[JsonPropertyName("token_endpoint")]
		public string TokenEndpoint { get; set; }
	}

	public sealed class OAuthTokenResponse
	{
		[JsonPropertyName("access_token")]
		public string AccessToken { get; set; }

		[JsonPropertyName("refresh_token")]
		public string RefreshToken { get; set; }

		[JsonPropertyName("expires_in")]
		public int ExpiresIn { get; set; }

		[JsonPropertyName("token_type")]
		public string TokenType { get; set; }

		[JsonPropertyName("scope")]
		public string Scope { get; set; }
	}

	public sealed class ResgridApiTokenState
	{
		public string AccessToken { get; set; }
		public string RefreshToken { get; set; }
		public DateTimeOffset ExpiresAtUtc { get; set; }
		public string UserId { get; set; }
	}
}
