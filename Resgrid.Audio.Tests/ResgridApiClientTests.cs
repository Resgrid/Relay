using FluentAssertions;
using NUnit.Framework;
using Resgrid.Providers.ApiClient.V4;
using Resgrid.Providers.ApiClient.V4.Models;
using System;
using System.Collections.Generic;
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
			private readonly Func<HttpRequestMessage, HttpResponseMessage> _api;

			public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> api)
			{
				_api = api;
			}

			public int TokenRequests { get; private set; }
			public List<HttpRequestMessage> ApiRequests { get; } = new List<HttpRequestMessage>();

			protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
			{
				var path = request.RequestUri.AbsolutePath;
				if (path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
					return Task.FromResult(Json(HttpStatusCode.OK, $"{{\"issuer\":\"{BaseUrl}/\",\"token_endpoint\":\"{BaseUrl}/connect/token\"}}"));

				if (path.EndsWith("/connect/token", StringComparison.Ordinal))
				{
					TokenRequests++;
					// Resgrid's access tokens are encrypted: nothing in them can be read by the relay.
					return Task.FromResult(Json(HttpStatusCode.OK, $"{{\"access_token\":\"opaque-{TokenRequests}\",\"expires_in\":3600,\"token_type\":\"Bearer\"}}"));
				}

				ApiRequests.Add(request);
				return Task.FromResult(_api(request) ?? Json(HttpStatusCode.OK, "{}"));
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
