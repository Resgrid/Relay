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

		/// <summary>
		/// SHA-256 of the refresh token configured when this cache was written, so a newly configured token can be told apart
		/// from the one the cache already rotated past.
		/// </summary>
		public string ConfiguredRefreshTokenHash { get; set; }

		/// <summary>
		/// An id given to the credential chain this cache holds (derived from the refresh token the cache first held), kept across
		/// every refresh-token rotation. Without a configured refresh token it is what tells relays apart that are configured alike
		/// (same client id, same cache path) but authenticate as different departments, so they never share a lookup cache
		/// namespace; a running client never adopts a reloaded cache carrying another id.
		/// </summary>
		public string CredentialId { get; set; }

		/// <summary>
		/// How many times the clients that wrote this cache have redeemed the chain's refresh token: of two states of one chain,
		/// the one with the higher count continues the other (whose refresh token is spent). Access-token expiry cannot order
		/// them, since lifetimes differ between redemptions; it only says whether an access token can still be sent. 0 in a
		/// cache written before the count existed.
		/// </summary>
		public long Rotation { get; set; }
	}
}
