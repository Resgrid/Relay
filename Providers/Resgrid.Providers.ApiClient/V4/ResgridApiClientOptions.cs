using System;

namespace Resgrid.Providers.ApiClient.V4
{
	public enum ResgridAuthGrantType
	{
		/// <summary>
		/// Standard OAuth 2.0 refresh_token grant.
		/// Requires ClientId, ClientSecret, and RefreshToken.
		/// Used when the application runs on behalf of a specific user/department.
		/// </summary>
		RefreshToken = 1,

		/// <summary>
		/// OAuth 2.0 client_credentials grant.
		/// Requires only ClientId and ClientSecret — no refresh token.
		/// The application exchanges its own credentials for an access token.
		/// Used for single-department SMTP/Docker deployments where a
		/// refresh token is not provisioned.
		/// </summary>
		ClientCredentials = 2,

		/// <summary>
		/// System-level API key authentication that bypasses the standard
		/// OAuth 2.0 / OIDC token flow entirely.
		/// Requires ClientId and ClientSecret, plus a SystemApiKey.
		/// Used in Resgrid Hosted (multi-department) mode where the relay
		/// must create calls and upload files for any department.
		/// </summary>
		SystemApiKey = 3
	}

	public sealed class ResgridApiClientOptions
	{
		public string BaseUrl { get; set; } = "https://api.resgrid.com";
		public string ApiVersion { get; set; } = "4";
		public string ClientId { get; set; }
		public string ClientSecret { get; set; }
		public string RefreshToken { get; set; }
		public string Scope { get; set; } = "openid profile email offline_access mobile";
		public string TokenCachePath { get; set; }

		/// <summary>
		/// The authentication grant type to use when connecting to the Resgrid API.
		/// Defaults to <see cref="ResgridAuthGrantType.RefreshToken"/> for backward compatibility.
		/// </summary>
		public ResgridAuthGrantType GrantType { get; set; } = ResgridAuthGrantType.RefreshToken;

		/// <summary>
		/// A system-level API key used when <see cref="GrantType"/> is
		/// <see cref="ResgridAuthGrantType.SystemApiKey"/>. This key is
		/// sent as an HTTP header on every request and bypasses the
		/// standard OAuth 2.0 token flow entirely.
		/// </summary>
		public string SystemApiKey { get; set; }

		/// <summary>
		/// When set, all API calls are scoped to this department.
		/// In hosted (multi-department) mode this identifies which
		/// department a call or file upload belongs to. When unset
		/// the department is inferred from the access token or
		/// dispatch address.
		/// </summary>
		public string DepartmentId { get; set; }

		public void Validate()
		{
			if (String.IsNullOrWhiteSpace(BaseUrl))
				throw new InvalidOperationException("A Resgrid API base URL is required.");

			if (String.IsNullOrWhiteSpace(ApiVersion))
				throw new InvalidOperationException("A Resgrid API version is required.");

			if (String.IsNullOrWhiteSpace(ClientId))
				throw new InvalidOperationException("A Resgrid API client id is required.");

			if (String.IsNullOrWhiteSpace(ClientSecret))
				throw new InvalidOperationException("A Resgrid API client secret is required.");

			switch (GrantType)
			{
				case ResgridAuthGrantType.RefreshToken:
					if (String.IsNullOrWhiteSpace(RefreshToken) && String.IsNullOrWhiteSpace(TokenCachePath))
						throw new InvalidOperationException(
							"A refresh token or token cache path is required when using the refresh_token grant type.");
					break;

				case ResgridAuthGrantType.ClientCredentials:
					// Only ClientId and ClientSecret are required — already validated above.
					break;

				case ResgridAuthGrantType.SystemApiKey:
					if (String.IsNullOrWhiteSpace(SystemApiKey))
						throw new InvalidOperationException(
							"A system API key is required when using the SystemApiKey grant type.");
					break;

				default:
					throw new InvalidOperationException($"Unsupported Resgrid authentication grant type '{GrantType}'.");
			}
		}
	}
}
