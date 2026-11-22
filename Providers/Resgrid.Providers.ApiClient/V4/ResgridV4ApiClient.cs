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
	public sealed class ResgridV4ApiClient : IResgridApiClient, IDisposable
	{
		private const string SystemApiKeyHeaderName = "X-Resgrid-SystemApiKey";

		// Every client sharing a token cache file shares one lock: two relay modes running side by side in one process (the
		// desktop app) must not both redeem the same refresh token, because Resgrid rotates it and the loser's redemption fails.
		private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> SharedAuthLocks =
			new System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

		private readonly SemaphoreSlim AuthLock;
		private readonly bool _ownsAuthLock;
		private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions()
		{
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
			PropertyNameCaseInsensitive = true,
			WriteIndented = true
		};

		private HttpClient _client;
		private OpenIdConfiguration _openIdConfiguration;
		private ResgridApiClientOptions _options;
		private ResgridApiTokenState _tokenState;
		private string _credentialScope;

		// When this client last obtained an access token from the token endpoint (MinValue: never, or loaded from the cache).
		private DateTimeOffset _accessTokenIssuedAtUtc = DateTimeOffset.MinValue;

		public ResgridV4ApiClient(ResgridApiClientOptions options)
			: this(options, null)
		{
		}

		/// <summary>Sends every request through <paramref name="handler"/> (a proxy, or a stub in tests); null uses the default.</summary>
		public ResgridV4ApiClient(ResgridApiClientOptions options, HttpMessageHandler handler)
		{
			if (options == null)
				throw new ArgumentNullException(nameof(options));

			options.Validate();

			_options = options;
			_openIdConfiguration = null;

			if (String.IsNullOrWhiteSpace(options.TokenCachePath))
			{
				AuthLock = new SemaphoreSlim(1, 1);
				_ownsAuthLock = true;
			}
			else
			{
				AuthLock = SharedAuthLocks.GetOrAdd(Path.GetFullPath(options.TokenCachePath), _ => new SemaphoreSlim(1, 1));
			}
			_tokenState = LoadTokenState(options);

			_client = CreateClient(options.BaseUrl, handler);
		}

		public string CurrentUserId
		{
			get
			{
				if (IsSystemCaller && !String.IsNullOrWhiteSpace(_options.DepartmentId))
					return _options.DepartmentId;

				return _tokenState?.UserId;
			}
		}

		/// <summary>Same as <see cref="IsSystemCaller"/>; the system-key header itself is sent only with the SystemApiKey grant.</summary>
		public bool UsesSystemApiKey => IsSystemCaller;

		public bool IsSystemCaller => _options.IsSystemCaller();

		public string CredentialScope => _credentialScope ??= BuildCredentialScope(_options);

		/// <summary>
		/// A system caller sees every department, so all system callers share one scope. A token sees only its own department:
		/// a client_credentials client is the department itself, and refresh-token relays usually share one app client id, so the
		/// configured refresh token (or, without one, the token cache) tells them apart. Hashed: the scope ends up in cache keys.
		/// </summary>
		private static string BuildCredentialScope(ResgridApiClientOptions options)
		{
			string identity;
			if (options.IsSystemCaller())
				identity = "system";
			else if (options.GrantType == ResgridAuthGrantType.ClientCredentials)
				identity = $"client_credentials|{options.ClientId?.Trim()}";
			else if (!String.IsNullOrWhiteSpace(options.RefreshToken))
				identity = $"refresh_token|{options.ClientId?.Trim()}|{options.RefreshToken.Trim()}";
			else
				identity = $"refresh_token|{options.ClientId?.Trim()}|{Path.GetFullPath(options.TokenCachePath ?? String.Empty)}";

			var identityBaseUrl = NormalizeBaseUrl(options.BaseUrl ?? String.Empty).ToLowerInvariant();
			return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"{identityBaseUrl}|{identity}")))
				.Substring(0, 16)
				.ToLowerInvariant();
		}

		/// <summary>
		/// A 401 straight after a new token was issued is not an expired token: Resgrid also answers 401 when a token caller asks
		/// about another department (a dispatch code that is not its own). Refreshing then would rotate the refresh token for every
		/// such message, so a token younger than this is trusted and the 401 is passed back as it is.
		/// </summary>
		internal static readonly TimeSpan FreshTokenWindow = TimeSpan.FromSeconds(60);

		public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
		{
			using var response = await SendRawAsync(HttpMethod.Get, "Health/GetCurrent", null, cancellationToken).ConfigureAwait(false);
			return response.IsSuccessStatusCode;
		}

		public async Task<T> GetAsync<T>(string url, CancellationToken cancellationToken = default) where T : class
		{
			using var response = await SendRawAsync(HttpMethod.Get, url, null, cancellationToken).ConfigureAwait(false);
			return await ReadResponseAsync<T>(response, cancellationToken).ConfigureAwait(false);
		}

		public Task<T> PostAsync<T>(string url, object data, CancellationToken cancellationToken = default) where T : class
		{
			return PostAsync<T>(url, data, null, cancellationToken);
		}

		public async Task<T> PostAsync<T>(string url, object data, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken = default) where T : class
		{
			using var response = await SendRawAsync(HttpMethod.Post, url, data, cancellationToken, headers).ConfigureAwait(false);
			return await ReadResponseAsync<T>(response, cancellationToken).ConfigureAwait(false);
		}

		public void Dispose()
		{
			_client?.Dispose();
			if (_ownsAuthLock)
				AuthLock?.Dispose();
		}

		private static HttpClient CreateClient(string baseUrl, HttpMessageHandler handler)
		{
			var client = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
			client.BaseAddress = new Uri(NormalizeBaseUrl(baseUrl), UriKind.Absolute);
			client.DefaultRequestHeaders.Accept.Clear();
			client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

			return client;
		}

		private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string url, object data, CancellationToken cancellationToken,
			IReadOnlyDictionary<string, string> headers = null)
		{
			await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);

			var relativeUrl = BuildRelativeApiPath(url);
			var response = await SendAuthorizedRequestAsync(method, relativeUrl, data, _tokenState.AccessToken, cancellationToken, headers).ConfigureAwait(false);

			// 401 retry is only meaningful for token-based auth flows.
			// SystemApiKey mode uses a static key — retrying would just fail again.
			if (response.StatusCode == HttpStatusCode.Unauthorized && _options.GrantType != ResgridAuthGrantType.SystemApiKey &&
				!IsFreshToken(DateTimeOffset.UtcNow))
			{
				response.Dispose();
				_tokenState.AccessToken = null;
				_tokenState.ExpiresAtUtc = DateTimeOffset.MinValue;

				await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);
				return await SendAuthorizedRequestAsync(method, relativeUrl, data, _tokenState.AccessToken, cancellationToken, headers).ConfigureAwait(false);
			}

			return response;
		}

		private async Task<HttpResponseMessage> SendAuthorizedRequestAsync(HttpMethod method, string url, object data, string accessToken, CancellationToken cancellationToken,
			IReadOnlyDictionary<string, string> headers = null)
		{
			var request = new HttpRequestMessage(method, url);

			if (headers != null)
			{
				foreach (var header in headers)
				{
					if (!String.IsNullOrWhiteSpace(header.Value))
						request.Headers.Add(header.Key, header.Value);
				}
			}

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

		private async Task<T> ReadResponseAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken) where T : class
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

		private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
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

				// Another client on the same cache (a second relay mode, or the previous run of this one) may have rotated the
				// refresh token since this client loaded it, in which case the one held here is already spent. Pick up the newer
				// state first; when it still carries a live access token there is nothing to redeem at all.
				if (_options.GrantType == ResgridAuthGrantType.RefreshToken)
				{
					var latest = LoadTokenState(_options);
					if (!String.IsNullOrWhiteSpace(latest.RefreshToken) &&
						!String.Equals(latest.RefreshToken, _tokenState.RefreshToken, StringComparison.Ordinal) &&
						latest.ExpiresAtUtc >= _tokenState.ExpiresAtUtc)
					{
						_tokenState = latest;
						if (HasValidAccessToken())
							return;
					}
				}

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
							throw new ResgridAuthenticationException("No Resgrid refresh token is available.");

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
					throw new ResgridAuthenticationException(
						$"The Resgrid token request failed with status code {(int)response.StatusCode} ({response.ReasonPhrase}). Response: {payload}",
						response.StatusCode);
				}

				var tokenResponse = JsonSerializer.Deserialize<OAuthTokenResponse>(payload, JsonOptions);
				if (tokenResponse == null || String.IsNullOrWhiteSpace(tokenResponse.AccessToken))
					throw new ResgridAuthenticationException("The Resgrid token response did not include an access token.");

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
				_accessTokenIssuedAtUtc = DateTimeOffset.UtcNow;

				// Only readable when Resgrid issues plain JWTs; its access tokens are normally encrypted, so this is usually null and
				// nothing may depend on it (Resgrid takes the author of a token caller's upload from the token itself).
				_tokenState.UserId = TryReadUserId(tokenResponse.AccessToken);

				// Not the caller's token: once Resgrid has rotated the refresh token, the new one must reach disk even if the
				// request that triggered the refresh is being cancelled, or the next start has only a dead token.
				await PersistTokenStateAsync(_tokenState, CancellationToken.None).ConfigureAwait(false);
			}
			finally
			{
				AuthLock.Release();
			}
		}

		internal bool IsFreshToken(DateTimeOffset nowUtc)
		{
			return _accessTokenIssuedAtUtc != DateTimeOffset.MinValue && nowUtc - _accessTokenIssuedAtUtc < FreshTokenWindow;
		}

		private bool HasValidAccessToken()
		{
			return !String.IsNullOrWhiteSpace(_tokenState?.AccessToken) &&
				   _tokenState.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1);
		}

		private async Task<OpenIdConfiguration> GetOpenIdConfigurationAsync(CancellationToken cancellationToken)
		{
			var discoveryUri = new Uri(_client.BaseAddress, ".well-known/openid-configuration");
			using var response = await _client.GetAsync(discoveryUri, cancellationToken).ConfigureAwait(false);
			var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				throw new ResgridAuthenticationException(
					$"The Resgrid OIDC discovery request failed with status code {(int)response.StatusCode} ({response.ReasonPhrase}). Response: {payload}",
					response.StatusCode);
			}

			var configuration = JsonSerializer.Deserialize<OpenIdConfiguration>(payload, JsonOptions);
			if (configuration == null || String.IsNullOrWhiteSpace(configuration.TokenEndpoint))
				throw new ResgridAuthenticationException("The Resgrid OIDC discovery document did not contain a token endpoint.");

			return configuration;
		}

		private string BuildRelativeApiPath(string url)
		{
			var relativeUrl = url?.TrimStart('/') ?? String.Empty;
			return $"api/v{NormalizeApiVersion(_options.ApiVersion)}/{relativeUrl}";
		}

		/// <summary>"4", "v4" and "V4" all mean v4; without this a "v4" setting builds <c>api/vv4/...</c> and every call 404s.</summary>
		public static string NormalizeApiVersion(string apiVersion)
		{
			var version = apiVersion?.Trim() ?? String.Empty;
			if (version.StartsWith("v", StringComparison.OrdinalIgnoreCase))
				version = version.Substring(1);

			return version;
		}

		private static string NormalizeBaseUrl(string baseUrl)
		{
			return baseUrl.TrimEnd('/') + "/";
		}

		private string ResolveRefreshToken()
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

			string payload;
			try
			{
				payload = File.ReadAllText(cachePath);
			}
			catch (IOException)
			{
				// Being swapped in by another client right now; the configured token (or the state already held) still works.
				return state;
			}

			if (String.IsNullOrWhiteSpace(payload))
				return state;

			ResgridApiTokenState cachedState;
			try
			{
				cachedState = JsonSerializer.Deserialize<ResgridApiTokenState>(payload, JsonOptions);
			}
			catch (JsonException)
			{
				// A damaged cache must not stop the relay from starting; fall back to the configured refresh token.
				return state;
			}

			if (cachedState == null)
				return state;

			// The cached (rotated) token normally wins over the configured one, which Resgrid has already retired. But when the
			// operator pastes in a NEW refresh token (the old chain was revoked), the cache must not keep shadowing it forever.
			var configuredHash = HashRefreshToken(options.RefreshToken);
			if (configuredHash != null && cachedState.ConfiguredRefreshTokenHash != null &&
				!String.Equals(configuredHash, cachedState.ConfiguredRefreshTokenHash, StringComparison.Ordinal))
				return state;

			if (String.IsNullOrWhiteSpace(cachedState.RefreshToken))
				cachedState.RefreshToken = options.RefreshToken;

			return cachedState;
		}

		private static string HashRefreshToken(string refreshToken)
		{
			if (String.IsNullOrWhiteSpace(refreshToken))
				return null;

			return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken.Trim())));
		}

		private async Task PersistTokenStateAsync(ResgridApiTokenState tokenState, CancellationToken cancellationToken)
		{
			if (String.IsNullOrWhiteSpace(_options.TokenCachePath))
				return;

			var cachePath = Path.GetFullPath(_options.TokenCachePath);
			var cacheDirectory = Path.GetDirectoryName(cachePath);
			if (!String.IsNullOrWhiteSpace(cacheDirectory))
				Directory.CreateDirectory(cacheDirectory);

			tokenState.ConfiguredRefreshTokenHash = HashRefreshToken(_options.RefreshToken);
			var payload = JsonSerializer.Serialize(tokenState, JsonOptions);

			// Resgrid rotates refresh tokens, so the previous one is already dead by the time this runs: a cache file torn by a
			// crash mid-write would leave the relay unable to authenticate ever again. Write a sibling temp file and swap it in.
			// The file holds live credentials, so on Unix it is created owner-only rather than with the umask's world-read bit.
			var tempPath = cachePath + ".tmp";
			var fileOptions = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
			if (!OperatingSystem.IsWindows())
				fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

			await using (var stream = new FileStream(tempPath, fileOptions))
			await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
			{
				await writer.WriteAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
				await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
				stream.Flush(flushToDisk: true);
			}

			// A reader holding the cache open on Windows can briefly block the replace; retry rather than lose the rotated token.
			for (var attempt = 1; ; attempt++)
			{
				try
				{
					File.Move(tempPath, cachePath, overwrite: true);
					return;
				}
				catch (IOException) when (attempt < 5)
				{
					await Task.Delay(50 * attempt).ConfigureAwait(false);
				}
				catch (UnauthorizedAccessException) when (attempt < 5)
				{
					await Task.Delay(50 * attempt).ConfigureAwait(false);
				}
			}
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
