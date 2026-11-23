using FluentAssertions;
using NUnit.Framework;
using Resgrid.Providers.ApiClient.V4;
using Resgrid.Providers.ApiClient.V4.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Resgrid.Audio.Tests
{
	[TestFixture]
	public class ResgridApiClientTests
	{
		private const string BaseUrl = "https://api.resgrid.test";

		// ─── System callers (finding 3) ───

		[TestCase(ResgridAuthGrantType.SystemApiKey, false, true)]
		[TestCase(ResgridAuthGrantType.ClientCredentials, true, true)]
		[TestCase(ResgridAuthGrantType.ClientCredentials, false, false)]
		[TestCase(ResgridAuthGrantType.RefreshToken, true, false)]
		[TestCase(ResgridAuthGrantType.RefreshToken, false, false)]
		public void IsSystemCaller_Should_Cover_The_System_Key_And_Service_Account_Tokens(ResgridAuthGrantType grantType, bool serviceAccount, bool expected)
		{
			using var client = new ResgridV4ApiClient(CreateOptions(grantType, serviceAccount));

			client.IsSystemCaller.Should().Be(expected);
			client.UsesSystemApiKey.Should().Be(expected, "every caller of UsesSystemApiKey means Resgrid's system-caller rules");
		}

		[Test]
		public async Task A_Service_Account_Should_Still_Authenticate_With_Its_Token_And_Not_The_System_Key_Header()
		{
			var handler = new StubHandler(request => request.RequestUri.AbsolutePath.Contains("Health")
				? Json(HttpStatusCode.OK, "{}")
				: null);
			using var client = new ResgridV4ApiClient(CreateOptions(ResgridAuthGrantType.ClientCredentials, serviceAccount: true), handler);

			(await client.IsHealthyAsync()).Should().BeTrue();

			var apiRequest = handler.ApiRequests.Should().ContainSingle().Subject;
			apiRequest.Headers.Authorization.Should().NotBeNull();
			apiRequest.Headers.Authorization.Scheme.Should().Be("Bearer");
			apiRequest.Headers.Contains("X-Resgrid-SystemApiKey").Should().BeFalse();
		}

		[Test]
		public void WithIsolatedTokenCache_Should_Keep_The_Service_Account_Flag()
		{
			var options = CreateOptions(ResgridAuthGrantType.ClientCredentials, serviceAccount: true);
			options.TokenCachePath = "data/resgrid-token.json";

			options.WithIsolatedTokenCache("smtp").ServiceAccount.Should().BeTrue();
		}

		// ─── 401 straight after a token was issued (finding 10) ───

		[Test]
		public async Task A_401_Right_After_A_New_Token_Should_Not_Redeem_Another_Token()
		{
			// Resgrid answers 401 when a department token looks up another department's dispatch code; that is not an expired token.
			var handler = new StubHandler(_ => Json(HttpStatusCode.Unauthorized, "{}"));
			using var client = new ResgridV4ApiClient(CreateOptions(ResgridAuthGrantType.ClientCredentials, serviceAccount: false), handler);
			var lookups = new LookupsApi(client);

			(await lookups.LookupDepartmentByDispatchCodeAsync("other-department")).Should().BeNull();
			(await lookups.LookupDepartmentByDispatchCodeAsync("another-department")).Should().BeNull();

			handler.TokenRequests.Should().Be(1, "each such message used to rotate the token");
			handler.ApiRequests.Should().HaveCount(2);
		}

		[Test]
		public async Task A_401_For_A_Token_Another_Request_Already_Replaced_Should_Be_Retried_With_The_New_Token()
		{
			// Two requests send the same expired token. The first 401 redeems a new token; the second 401 answers the old token,
			// not the new one, so the new token being fresh must not turn it into "another department's code".
			var directory = Path.Combine(Path.GetTempPath(), $"resgrid-api-tests-{Guid.NewGuid():N}");
			Directory.CreateDirectory(directory);
			try
			{
				var cachePath = Path.Combine(directory, "resgrid-token.json");
				File.WriteAllText(cachePath, "{\"AccessToken\":\"expired-token\",\"ExpiresAtUtc\":\"2099-01-01T00:00:00+00:00\"}");
				var options = CreateOptions(ResgridAuthGrantType.ClientCredentials, serviceAccount: false);
				options.TokenCachePath = cachePath;

				var oldTokenRequests = 0;
				var otherCredentials = new List<string>();
				var bothSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
				var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
				var releaseSecond = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
				var handler = new StubHandler(async request =>
				{
					var sentToken = request.Headers.Authorization?.Parameter;
					if (sentToken == "opaque-1")
						return Json(HttpStatusCode.OK, "{}");

					if (sentToken != "expired-token")
					{
						// A retry that sent anything but the one replacement token (no token at all included) must not pass.
						lock (otherCredentials)
							otherCredentials.Add(sentToken ?? "<none>");
						return Json(HttpStatusCode.Forbidden, "{}");
					}

					var arrival = Interlocked.Increment(ref oldTokenRequests);
					if (arrival == 2)
						bothSent.TrySetResult(true);

					await (arrival == 1 ? releaseFirst.Task : releaseSecond.Task).ConfigureAwait(false);
					return Json(HttpStatusCode.Unauthorized, "{}");
				});
				using var client = new ResgridV4ApiClient(options, handler);

				var first = client.IsHealthyAsync();
				var second = client.IsHealthyAsync();
				try
				{
					(await Task.WhenAny(bothSent.Task, Task.Delay(TimeSpan.FromSeconds(30)))).Should().BeSameAs(bothSent.Task);

					releaseFirst.SetResult(true);
					(await first).Should().BeTrue("the first 401 redeems a new token and the request is sent again with it");

					releaseSecond.SetResult(true);
					(await second).Should().BeTrue("its 401 was for the token the first request had already replaced");
					handler.TokenRequests.Should().Be(1, "the token already replaced is not redeemed again");
					otherCredentials.Should().BeEmpty();
					handler.ApiRequests.Count(x => x.Headers.Authorization?.Parameter == "opaque-1")
						.Should().Be(2, "both retries send the one replacement token");
				}
				finally
				{
					releaseFirst.TrySetResult(true);
					releaseSecond.TrySetResult(true);
					await Task.WhenAny(Task.WhenAll(first, second), Task.Delay(TimeSpan.FromSeconds(30)));
				}
			}
			finally
			{
				Directory.Delete(directory, recursive: true);
			}
		}

		[Test]
		public async Task Lookups_Should_Let_A_403_Surface_For_A_Token_Caller_Instead_Of_Reporting_The_Code_Unknown()
		{
			// Only a 401 means "another department's code"; a 403 is a key, permission or gateway problem worth retrying.
			var forbidden = new LookupsApi(new FakeApiClient { Exception = new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden) });
			var unauthorized = new LookupsApi(new FakeApiClient { Exception = new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized) });

			Func<Task> department = () => forbidden.LookupDepartmentByDispatchCodeAsync("abc123");
			Func<Task> group = () => forbidden.LookupGroupByDispatchCodeAsync("station5", null);
			Func<Task> messageGroup = () => forbidden.LookupGroupByMessageCodeAsync("crew", null);

			(await department.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
			await group.Should().ThrowAsync<HttpRequestException>();
			await messageGroup.Should().ThrowAsync<HttpRequestException>();
			(await unauthorized.LookupDepartmentByDispatchCodeAsync("abc123")).Should().BeNull();
		}

		// ─── Credential scope for the shared lookup cache (finding 11) ───

		[Test]
		public void CredentialScope_Should_Differ_Between_Department_Credentials_And_Match_For_The_Same_Credential()
		{
			using var departmentA = new ResgridV4ApiClient(CreateOptions(ResgridAuthGrantType.ClientCredentials, false, clientId: "DepartmentA"));
			using var departmentA2 = new ResgridV4ApiClient(CreateOptions(ResgridAuthGrantType.ClientCredentials, false, clientId: "DepartmentA"));
			using var departmentB = new ResgridV4ApiClient(CreateOptions(ResgridAuthGrantType.ClientCredentials, false, clientId: "DepartmentB"));
			using var refreshA = new ResgridV4ApiClient(CreateOptions(ResgridAuthGrantType.RefreshToken, false, clientId: "RelayApp", refreshToken: "token-a"));
			using var refreshB = new ResgridV4ApiClient(CreateOptions(ResgridAuthGrantType.RefreshToken, false, clientId: "RelayApp", refreshToken: "token-b"));
			using var systemKey = new ResgridV4ApiClient(CreateOptions(ResgridAuthGrantType.SystemApiKey, false));
			using var serviceAccount = new ResgridV4ApiClient(CreateOptions(ResgridAuthGrantType.ClientCredentials, true, clientId: "system"));

			departmentA.CredentialScope.Should().Be(departmentA2.CredentialScope);
			new[] { departmentA.CredentialScope, departmentB.CredentialScope, refreshA.CredentialScope, refreshB.CredentialScope, systemKey.CredentialScope }
				.Should().OnlyHaveUniqueItems("relays of different departments sharing a Redis must not read each other's lookups");
			serviceAccount.CredentialScope.Should().Be(systemKey.CredentialScope, "system callers all see every department");
			departmentA.CredentialScope.Should().NotContain("DepartmentA");
		}

		[Test]
		public async Task CredentialScope_Should_Tell_Apart_Relays_That_Share_Only_Their_Settings_And_Token_Cache_Path()
		{
			// No refresh token configured: each relay's token cache holds its department's credential. Containers commonly share
			// the same client id and cache path, so the path alone must not put two departments in one lookup cache namespace.
			var directory = Path.Combine(Path.GetTempPath(), $"resgrid-api-tests-{Guid.NewGuid():N}");
			Directory.CreateDirectory(directory);
			try
			{
				var cachePath = Path.Combine(directory, "resgrid-token.json");
				var options = CreateOptions(ResgridAuthGrantType.RefreshToken, serviceAccount: false, clientId: "RelayApp", refreshToken: null);
				options.TokenCachePath = cachePath;
				var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "{}"));

				File.WriteAllText(cachePath, "{\"RefreshToken\":\"department-a-chain\"}");
				string scopeA;
				using (var hostA = new ResgridV4ApiClient(options, handler))
				{
					scopeA = hostA.CredentialScope;
					(await hostA.IsHealthyAsync()).Should().BeTrue("the refresh token is redeemed, and the rotated one written back");
				}

				using (var hostARestarted = new ResgridV4ApiClient(options, handler))
					hostARestarted.CredentialScope.Should().Be(scopeA, "the cache keeps its id across the rotation and the restart");

				// Another host: the same settings and the same path, but its own cache for another department.
				File.WriteAllText(cachePath, "{\"RefreshToken\":\"department-b-chain\"}");
				using (var hostB = new ResgridV4ApiClient(options, handler))
					hostB.CredentialScope.Should().NotBe(scopeA);
			}
			finally
			{
				Directory.Delete(directory, recursive: true);
			}
		}

		[TestCase(",\"CredentialId\":\"another-credential\"")]
		[TestCase("")]
		public async Task A_Reloaded_Token_Cache_Holding_Another_Credential_Should_Not_Be_Adopted(string credentialId)
		{
			// The SMTP relay's Redis lookup cache takes CredentialScope as its prefix once. Adopting another department's cache
			// while running would store that department's lookups under this one's namespace.
			var directory = Path.Combine(Path.GetTempPath(), $"resgrid-api-tests-{Guid.NewGuid():N}");
			Directory.CreateDirectory(directory);
			try
			{
				var cachePath = Path.Combine(directory, "resgrid-token.json");
				var options = CreateOptions(ResgridAuthGrantType.RefreshToken, serviceAccount: false, clientId: "RelayApp", refreshToken: null);
				options.TokenCachePath = cachePath;
				// Shorter than the one-minute margin: every request redeems the refresh token again.
				var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "{}")) { ExpiresIn = 30 };
				var warnings = new List<string>();

				File.WriteAllText(cachePath, "{\"RefreshToken\":\"department-a-chain\"}");
				using var client = new ResgridV4ApiClient(options, handler);
				client.Warning += warnings.Add;
				var scope = client.CredentialScope;
				(await client.IsHealthyAsync()).Should().BeTrue();

				var replacement = "{\"RefreshToken\":\"department-b-chain\",\"ExpiresAtUtc\":\"2099-01-01T00:00:00+00:00\"" + credentialId + "}";
				File.WriteAllText(cachePath, replacement);
				(await client.IsHealthyAsync()).Should().BeTrue();

				handler.RedeemedRefreshTokens.Should().Equal(new[] { "department-a-chain", "rotated-1" }, "the client keeps its own chain");
				client.CredentialScope.Should().Be(scope);
				warnings.Should().ContainSingle().Which.Should().Contain("different credential").And.NotContain("department-");
				File.ReadAllText(cachePath).Should().Be(replacement, "the credential put in the file is left for the next start");

				using var restarted = new ResgridV4ApiClient(options, handler);
				restarted.CredentialScope.Should().NotBe(scope, "after a restart the file's credential is used, under its own scope");
			}
			finally
			{
				Directory.Delete(directory, recursive: true);
			}
		}

		[Test]
		public async Task A_Client_Started_Before_Its_Token_Cache_Existed_Should_Adopt_The_Cache_That_Appears()
		{
			// It held no credential yet, so its scope named none: taking the file's credential mixes nothing.
			var directory = Path.Combine(Path.GetTempPath(), $"resgrid-api-tests-{Guid.NewGuid():N}");
			Directory.CreateDirectory(directory);
			try
			{
				var cachePath = Path.Combine(directory, "resgrid-token.json");
				var options = CreateOptions(ResgridAuthGrantType.RefreshToken, serviceAccount: false, clientId: "RelayApp", refreshToken: null);
				options.TokenCachePath = cachePath;
				var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "{}"));
				var warnings = new List<string>();

				using var client = new ResgridV4ApiClient(options, handler);
				client.Warning += warnings.Add;
				var scope = client.CredentialScope;
				File.WriteAllText(cachePath, "{\"RefreshToken\":\"dropped-in-chain\",\"CredentialId\":\"dropped-in\"}");

				(await client.IsHealthyAsync()).Should().BeTrue();

				handler.RedeemedRefreshTokens.Should().Equal("dropped-in-chain");
				client.CredentialScope.Should().Be(scope);
				warnings.Should().BeEmpty();
				File.ReadAllText(cachePath).Should().Contain("rotated-1").And.Contain("\"dropped-in\"", "the chain keeps the id the file gave it");
			}
			finally
			{
				Directory.Delete(directory, recursive: true);
			}
		}

		[Test]
		public async Task Clients_Loading_The_Same_Id_Less_Token_Cache_Should_Share_Its_Scope_And_Its_Rotation()
		{
			// Two relay modes in one process on one cache written before the id existed: each used to give it its own random id,
			// so the one that did not rotate first took the other's rotation for another credential.
			var directory = Path.Combine(Path.GetTempPath(), $"resgrid-api-tests-{Guid.NewGuid():N}");
			Directory.CreateDirectory(directory);
			try
			{
				var cachePath = Path.Combine(directory, "resgrid-token.json");
				var options = CreateOptions(ResgridAuthGrantType.RefreshToken, serviceAccount: false, clientId: "RelayApp", refreshToken: null);
				options.TokenCachePath = cachePath;
				var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "{}"));
				var warnings = new List<string>();

				File.WriteAllText(cachePath, "{\"RefreshToken\":\"chain-0\"}");
				using var smtp = new ResgridV4ApiClient(options, handler);
				using var audio = new ResgridV4ApiClient(options, handler);
				smtp.Warning += warnings.Add;
				audio.Warning += warnings.Add;

				audio.CredentialScope.Should().Be(smtp.CredentialScope);
				(await smtp.IsHealthyAsync()).Should().BeTrue();
				(await audio.IsHealthyAsync()).Should().BeTrue();

				handler.RedeemedRefreshTokens.Should().Equal(new[] { "chain-0" }, "the second client adopts the first one's rotation");
				warnings.Should().BeEmpty();
			}
			finally
			{
				Directory.Delete(directory, recursive: true);
			}
		}

		// ─── 404 from a lookup endpoint (finding 9) ───

		[Test]
		public async Task Lookups_Should_Throw_On_404_Instead_Of_Reporting_The_Code_Unknown()
		{
			var lookups = new LookupsApi(new FakeApiClient { Exception = new HttpRequestException("not found", null, HttpStatusCode.NotFound) });

			Func<Task> department = () => lookups.LookupDepartmentByDispatchCodeAsync("abc123");
			Func<Task> group = () => lookups.LookupGroupByDispatchCodeAsync("station5", "42");
			Func<Task> messageGroup = () => lookups.LookupGroupByMessageCodeAsync("crew", "42");

			(await department.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(HttpStatusCode.NotFound);
			await group.Should().ThrowAsync<HttpRequestException>();
			await messageGroup.Should().ThrowAsync<HttpRequestException>();
		}

		[Test]
		public async Task Lookups_Should_Report_A_Not_Found_Status_As_Unknown()
		{
			var lookups = new LookupsApi(new FakeApiClient { Response = new LookupResponse<DepartmentLookupResult> { Status = "not_found", Data = new DepartmentLookupResult() } });

			(await lookups.LookupDepartmentByDispatchCodeAsync("abc123")).Should().BeNull();
		}

		private static ResgridApiClientOptions CreateOptions(ResgridAuthGrantType grantType, bool serviceAccount, string clientId = "relay", string refreshToken = "refresh")
		{
			return new ResgridApiClientOptions
			{
				BaseUrl = BaseUrl,
				ClientId = clientId,
				ClientSecret = "secret",
				GrantType = grantType,
				RefreshToken = grantType == ResgridAuthGrantType.RefreshToken ? refreshToken : null,
				SystemApiKey = grantType == ResgridAuthGrantType.SystemApiKey ? "system-key" : null,
				ServiceAccount = serviceAccount
			};
		}

		private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
			new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

		/// <summary>Answers OIDC discovery and the token endpoint itself; every other request goes to the callback.</summary>
		private sealed class StubHandler : HttpMessageHandler
		{
			private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _api;
			private int _tokenRequests;

			public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> api)
				: this(request => Task.FromResult(api(request)))
			{
			}

			public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> api)
			{
				_api = api;
			}

			public int TokenRequests => Volatile.Read(ref _tokenRequests);
			public List<HttpRequestMessage> ApiRequests { get; } = new List<HttpRequestMessage>();
			public List<string> RedeemedRefreshTokens { get; } = new List<string>();
			public int ExpiresIn { get; set; } = 3600;

			protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
			{
				var path = request.RequestUri.AbsolutePath;
				if (path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
					return Json(HttpStatusCode.OK, $"{{\"issuer\":\"{BaseUrl}/\",\"token_endpoint\":\"{BaseUrl}/connect/token\"}}");

				if (path.EndsWith("/connect/token", StringComparison.Ordinal))
				{
					var form = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
					var refreshToken = form.Split('&')
						.Select(x => x.Split('=', 2))
						.Where(x => x.Length == 2 && x[0] == "refresh_token")
						.Select(x => Uri.UnescapeDataString(x[1]))
						.FirstOrDefault();
					if (refreshToken != null)
					{
						lock (RedeemedRefreshTokens)
							RedeemedRefreshTokens.Add(refreshToken);
					}

					var count = Interlocked.Increment(ref _tokenRequests);
					// Resgrid's access tokens are encrypted: nothing in them can be read by the relay. It rotates refresh tokens.
					return Json(HttpStatusCode.OK,
						$"{{\"access_token\":\"opaque-{count}\",\"refresh_token\":\"rotated-{count}\",\"expires_in\":{ExpiresIn},\"token_type\":\"Bearer\"}}");
				}

				lock (ApiRequests)
					ApiRequests.Add(request);

				return await _api(request).ConfigureAwait(false) ?? Json(HttpStatusCode.OK, "{}");
			}
		}

		private sealed class FakeApiClient : IResgridApiClient
		{
			public Exception Exception { get; set; }
			public object Response { get; set; }

			public string CurrentUserId => null;
			public bool UsesSystemApiKey => false;
			public bool IsSystemCaller => false;
			public string CredentialScope => "test";

			public Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

			public Task<T> GetAsync<T>(string url, CancellationToken cancellationToken = default) where T : class
			{
				if (Exception != null)
					throw Exception;

				return Task.FromResult(Response as T);
			}

			public Task<T> PostAsync<T>(string url, object data, CancellationToken cancellationToken = default) where T : class =>
				throw new NotSupportedException();

			public Task<T> PostAsync<T>(string url, object data, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken = default) where T : class =>
				throw new NotSupportedException();
		}
	}
}
