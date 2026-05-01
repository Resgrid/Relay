using Resgrid.Providers.ApiClient.V4.Models;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Resgrid.Providers.ApiClient.V4
{
	public static class ResgridV4ApiClient
	{
		private const string SystemApiKeyHeaderName = "X-Resgrid-SystemApiKey";

		private static readonly SemaphoreSlim AuthLock = new SemaphoreSlim(1, 1);
		private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions()
		{
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
			PropertyNameCaseInsensitive = true,
			WriteIndented = true
		};

		private static HttpClient _client = CreateClient("https://api.resgrid.com");
		private static OpenIdConfiguration _openIdConfiguration;
		private static ResgridApiClientOptions _options = new ResgridApiClientOptions();
		private static ResgridApiTokenState _tokenState = new ResgridApiTokenState();

		public static string CurrentUserId
		{
			get
			{
				if (_options.GrantType == ResgridAuthGrantType.SystemApiKey && !String.IsNullOrWhiteSpace(_options.DepartmentId))
					return _options.DepartmentId;

				return _tokenState?.UserId;
			}
		}

		public static void Init(ResgridApiClientOptions options)
		{
			if (options == null)
				throw new ArgumentNullException(nameof(options));

			options.Validate();

			_options = options;
			_openIdConfiguration = null;
			_tokenState = LoadTokenState(options);

			_client.Dispose();
			_client = CreateClient(options.BaseUrl);
		}

		public static async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
		{
			using var response = await SendRawAsync(HttpMethod.Get, "Health/GetCurrent", null, cancellationToken).ConfigureAwait(false);
			return response.IsSuccessStatusCode;
		}

		public static async Task<T> GetAsync<T>(string url, CancellationToken cancellationToken = default) where T : class
		{
			using var response = await SendRawAsync(HttpMethod.Get, url, null, cancellationToken).ConfigureAwait(false);
			return await ReadResponseAsync<T>(response, cancellationToken).ConfigureAwait(false);
		}

		public static async Task<T> PostAsync<T>(string url, object data, CancellationToken cancellationToken = default) where T : class
		{
			using var response = await SendRawAsync(HttpMethod.Post, url, data, cancellationToken).ConfigureAwait(false);
			return await ReadResponseAsync<T>(response, cancellationToken).ConfigureAwait(false);
		}

		private static HttpClient CreateClient(string baseUrl)
		{
			var client = new HttpClient();
			client.BaseAddress = new Uri(NormalizeBaseUrl(baseUrl), UriKind.Absolute);
			client.DefaultRequestHeaders.Accept.Clear();
			client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

			return client;
		}

		private static async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string url, object data, CancellationToken cancellationToken)
		{
			await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);

			var relativeUrl = BuildRelativeApiPath(url);
			var response = await SendAuthorizedRequestAsync(method, relativeUrl, data, _tokenState.AccessToken, cancellationToken).ConfigureAwait(false);

			// 401 retry is only meaningful for token-based auth flows.
			// SystemApiKey mode uses a static key — retrying would just fail again.
			if (response.StatusCode == HttpStatusCode.Unauthorized && _options.GrantType != ResgridAuthGrantType.SystemApiKey)
			{
				response.Dispose();
				_tokenState.AccessToken = null;
				_tokenState.ExpiresAtUtc = DateTimeOffset.MinValue;

				await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);
				return await SendAuthorizedRequestAsync(method, relativeUrl, data, _tokenState.AccessToken, cancellationToken).ConfigureAwait(false);
			}

			return response;
		}

		private static async Task<HttpResponseMessage> SendAuthorizedRequestAsync(HttpMethod method, string url, object data, string accessToken, CancellationToken cancellationToken)
		{
			var request = new HttpRequestMessage(method, url);

			if (_options.GrantType == ResgridAuthGrantType.SystemApiKey)
			{
				request.Headers.Add(SystemApiKeyHeaderName, _options.SystemApiKey);
			}
			else
			{
				request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
			}

			if (data != null)
			{
				var payload = JsonSerializer.Serialize(data, JsonOptions);
				request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
			}

			return await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
		}

		private static async Task<T> ReadResponseAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken) where T : class
		{
			var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				throw new HttpRequestException(
					$"The Resgrid API request failed with status code {(int)response.StatusCode} ({response.ReasonPhrase}). Response: {payload}",
					null,
					response.StatusCode);
			}

			var result = JsonSerializer.Deserialize<T>(payload, JsonOptions);
			if (result == null)
				throw new InvalidOperationException($"The Resgrid API returned an empty {typeof(T).Name} payload.");

			return result;
		}

		private static async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
		{
			// SystemApiKey mode does not use OAuth tokens — the key is
			// attached directly to every request.
			if (_options.GrantType == ResgridAuthGrantType.SystemApiKey)
				return;

			if (HasValidAccessToken())
				return;

			await AuthLock.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				if (HasValidAccessToken())
					return;

				_openIdConfiguration ??= await GetOpenIdConfigurationAsync(cancellationToken).ConfigureAwait(false);

				var formData = new Dictionary<string, string>()
				{
					["client_id"] = _options.ClientId,
					["client_secret"] = _options.ClientSecret
				};

				switch (_options.GrantType)
				{
					case ResgridAuthGrantType.RefreshToken:
					{
						var refreshToken = ResolveRefreshToken();
						if (String.IsNullOrWhiteSpace(refreshToken))
							throw new InvalidOperationException("No Resgrid refresh token is available.");

						formData["grant_type"] = "refresh_token";
						formData["refresh_token"] = refreshToken;
						break;
					}

					case ResgridAuthGrantType.ClientCredentials:
					{
						formData["grant_type"] = "client_credentials";
						break;
					}

					default:
						throw new InvalidOperationException(
							$"The Resgrid authentication grant type '{_options.GrantType}' requires an OAuth token but no supported grant flow is defined.");
				}

				if (!String.IsNullOrWhiteSpace(_options.Scope))
					formData["scope"] = _options.Scope;

				using var response = await _client.PostAsync(_openIdConfiguration.TokenEndpoint, new FormUrlEncodedContent(formData), cancellationToken).ConfigureAwait(false);
				var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
				if (!response.IsSuccessStatusCode)
				{
					throw new HttpRequestException(
						$"The Resgrid token request failed with status code {(int)response.StatusCode} ({response.ReasonPhrase}). Response: {payload}",
						null,
						response.StatusCode);
				}

				var tokenResponse = JsonSerializer.Deserialize<OAuthTokenResponse>(payload, JsonOptions);
				if (tokenResponse == null || String.IsNullOrWhiteSpace(tokenResponse.AccessToken))
					throw new InvalidOperationException("The Resgrid token response did not include an access token.");

				_tokenState.AccessToken = tokenResponse.AccessToken;

				if (_options.GrantType == ResgridAuthGrantType.RefreshToken)
				{
					// Refresh token rotation: use the new refresh token if provided,
					// otherwise retain the existing one.
					_tokenState.RefreshToken = String.IsNullOrWhiteSpace(tokenResponse.RefreshToken)
						? ResolveRefreshToken()
						: tokenResponse.RefreshToken;
				}
				else
				{
					// client_credentials typically does not return a refresh token.
					_tokenState.RefreshToken = tokenResponse.RefreshToken;
				}

				_tokenState.ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn > 0 ? tokenResponse.ExpiresIn : 300);
				_tokenState.UserId = TryReadUserId(tokenResponse.AccessToken);

				await PersistTokenStateAsync(_tokenState, cancellationToken).ConfigureAwait(false);
			}
			finally
			{
				AuthLock.Release();
			}
		}

		private static bool HasValidAccessToken()
		{
			return !String.IsNullOrWhiteSpace(_tokenState?.AccessToken) &&
				   _tokenState.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1);
		}

		private static async Task<OpenIdConfiguration> GetOpenIdConfigurationAsync(CancellationToken cancellationToken)
		{
			var discoveryUri = new Uri(_client.BaseAddress, ".well-known/openid-configuration");
			using var response = await _client.GetAsync(discoveryUri, cancellationToken).ConfigureAwait(false);
			var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				throw new HttpRequestException(
					$"The Resgrid OIDC discovery request failed with status code {(int)response.StatusCode} ({response.ReasonPhrase}). Response: {payload}",
					null,
					response.StatusCode);
			}

			var configuration = JsonSerializer.Deserialize<OpenIdConfiguration>(payload, JsonOptions);
			if (configuration == null || String.IsNullOrWhiteSpace(configuration.TokenEndpoint))
				throw new InvalidOperationException("The Resgrid OIDC discovery document did not contain a token endpoint.");

			return configuration;
		}

		private static string BuildRelativeApiPath(string url)
		{
			var relativeUrl = url?.TrimStart('/') ?? String.Empty;
			return $"api/v{_options.ApiVersion}/{relativeUrl}";
		}

		private static string NormalizeBaseUrl(string baseUrl)
		{
			return baseUrl.TrimEnd('/') + "/";
		}

		private static string ResolveRefreshToken()
		{
			if (!String.IsNullOrWhiteSpace(_tokenState?.RefreshToken))
				return _tokenState.RefreshToken;

			if (!String.IsNullOrWhiteSpace(_options.RefreshToken))
				return _options.RefreshToken;

			return null;
		}

		private static ResgridApiTokenState LoadTokenState(ResgridApiClientOptions options)
		{
			var state = new ResgridApiTokenState
			{
				RefreshToken = options.RefreshToken
			};

			if (String.IsNullOrWhiteSpace(options.TokenCachePath))
				return state;

			var cachePath = Path.GetFullPath(options.TokenCachePath);
			if (!File.Exists(cachePath))
				return state;

			var payload = File.ReadAllText(cachePath);
			if (String.IsNullOrWhiteSpace(payload))
				return state;

			var cachedState = JsonSerializer.Deserialize<ResgridApiTokenState>(payload, JsonOptions);
			if (cachedState == null)
				return state;

			if (String.IsNullOrWhiteSpace(cachedState.RefreshToken))
				cachedState.RefreshToken = options.RefreshToken;

			return cachedState;
		}

		private static async Task PersistTokenStateAsync(ResgridApiTokenState tokenState, CancellationToken cancellationToken)
		{
			if (String.IsNullOrWhiteSpace(_options.TokenCachePath))
				return;

			var cachePath = Path.GetFullPath(_options.TokenCachePath);
			var cacheDirectory = Path.GetDirectoryName(cachePath);
			if (!String.IsNullOrWhiteSpace(cacheDirectory))
				Directory.CreateDirectory(cacheDirectory);

			var payload = JsonSerializer.Serialize(tokenState, JsonOptions);
			await File.WriteAllTextAsync(cachePath, payload, cancellationToken).ConfigureAwait(false);
		}

		private static string TryReadUserId(string accessToken)
		{
			if (String.IsNullOrWhiteSpace(accessToken))
				return null;

			var handler = new JwtSecurityTokenHandler();
			if (!handler.CanReadToken(accessToken))
				return null;

			var token = handler.ReadJwtToken(accessToken);
			return token.Claims.FirstOrDefault(x => String.Equals(x.Type, "sub", StringComparison.OrdinalIgnoreCase))?.Value;
		}
	}
}
