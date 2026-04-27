using System;

namespace Resgrid.Providers.ApiClient.V4
{
	public sealed class ResgridApiClientOptions
	{
		public string BaseUrl { get; set; } = "https://api.resgrid.com";
		public string ApiVersion { get; set; } = "4";
		public string ClientId { get; set; }
		public string ClientSecret { get; set; }
		public string RefreshToken { get; set; }
		public string Scope { get; set; } = "openid profile email offline_access mobile";
		public string TokenCachePath { get; set; }

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

			if (String.IsNullOrWhiteSpace(RefreshToken) && String.IsNullOrWhiteSpace(TokenCachePath))
				throw new InvalidOperationException("A refresh token or token cache path is required.");
		}
	}
}
