using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Resgrid.Audio.Relay.Services;
using Resgrid.Providers.ApiClient.V4;
using Resgrid.Providers.ApiClient.V4.Models;

namespace Resgrid.Audio.Relay.Tests
{
	/// <summary>
	/// The connection test of an edited client secret (Configuration): it must send the edited secret to Resgrid instead of
	/// passing on the relay's cached access token, and give the relay back the refresh-token chain it rotated.
	/// </summary>
	[TestFixture]
	public class TokenCacheForkTests
	{
		private const string BaseUrl = "https://resgrid.test";

		private string _directory;
		private string _relayCache;
		private string _fork;

		[SetUp]
		public void SetUp()
		{
			_directory = Path.Combine(Path.GetTempPath(), $"resgrid-fork-tests-{Guid.NewGuid():N}");
			Directory.CreateDirectory(_directory);
			_relayCache = Path.Combine(_directory, "resgrid-token.json");
			_fork = Path.Combine(_directory, "resgrid-token.pending.json");
		}

		[TearDown]
		public void TearDown()
		{
			try { Directory.Delete(_directory, recursive: true); }
			catch (IOException) { /* best effort */ }
		}

		private void WriteRelayCache(string refreshToken, string accessToken = "relay-live-access-token") =>
			File.WriteAllText(_relayCache, JsonSerializer.Serialize(new ResgridApiTokenState
			{
				AccessToken = accessToken,
				RefreshToken = refreshToken,
				ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
				CredentialId = "relay-chain"
			}));

		private static ResgridApiTokenState ReadState(string path) =>
			JsonSerializer.Deserialize<ResgridApiTokenState>(File.ReadAllText(path));

		[Test]
		public async Task An_Edited_Secret_Is_Sent_To_Resgrid_And_The_Rotated_Chain_Goes_Back_To_The_Relay()
		{
			WriteRelayCache("relay-refresh-1");

			var forked = TokenCacheFork.Fork(_relayCache, _fork);

			forked.Should().Be("relay-refresh-1");
			var copy = ReadState(_fork);
			copy.AccessToken.Should().BeNull("a live access token would pass without the edited secret ever being sent");
			copy.RefreshToken.Should().Be("relay-refresh-1");
			copy.CredentialId.Should().Be("relay-chain");

			var handler = new TokenEndpointStub();
			var options = new ResgridApiClientOptions
			{
				BaseUrl = BaseUrl,
				ClientId = "relay-app",
				ClientSecret = "edited-secret",
				RefreshToken = "configured-and-spent",
				TokenCachePath = _fork
			};
			using (var client = new ResgridV4ApiClient(options, handler))
				(await client.IsHealthyAsync()).Should().BeTrue();

			handler.TokenRequests.Should().ContainSingle();
			handler.TokenRequests[0]["client_secret"].Should().Be("edited-secret");
			handler.TokenRequests[0]["refresh_token"].Should().Be("relay-refresh-1", "the relay's current token, not the spent configured one");

			TokenCacheFork.HandBack(_fork, _relayCache, forked).Should().BeTrue();
			ReadState(_relayCache).RefreshToken.Should().Be("rotated-1", "Resgrid spent the relay's token; the relay continues the chain");
			ReadState(_relayCache).CredentialId.Should().Be("relay-chain");
			File.Exists(_fork).Should().BeFalse();
		}

		[Test]
		public void A_Refused_Secret_Leaves_The_Relay_Cache_Alone()
		{
			WriteRelayCache("relay-refresh-1");
			var before = File.ReadAllText(_relayCache);
			var forked = TokenCacheFork.Fork(_relayCache, _fork);

			// The sign-in failed: the fork still holds the token it was given.
			TokenCacheFork.HandBack(_fork, _relayCache, forked).Should().BeFalse();

			File.ReadAllText(_relayCache).Should().Be(before);
			File.Exists(_fork).Should().BeFalse();
		}

		[Test]
		public void A_Relay_That_Redeemed_Its_Token_Meanwhile_Keeps_Its_Own_Chain()
		{
			WriteRelayCache("relay-refresh-1");
			var forked = TokenCacheFork.Fork(_relayCache, _fork);
			File.WriteAllText(_fork, JsonSerializer.Serialize(new ResgridApiTokenState { AccessToken = "test", RefreshToken = "test-rotated" }));
			WriteRelayCache("relay-rotated");

			TokenCacheFork.HandBack(_fork, _relayCache, forked).Should().BeFalse();

			ReadState(_relayCache).RefreshToken.Should().Be("relay-rotated");
			File.Exists(_fork).Should().BeFalse();
		}

		[Test]
		public void Without_A_Relay_Cache_The_Configured_Token_Is_Redeemed_And_Its_Rotation_Handed_Back()
		{
			File.WriteAllText(_fork, "{\"RefreshToken\":\"stale-test\"}");

			TokenCacheFork.Fork(_relayCache, _fork).Should().BeNull();
			File.Exists(_fork).Should().BeFalse("a sign-in then starts from the configured refresh token, as the relay's would");

			// The sign-in redeemed the configured token (which the relay would otherwise present, spent, at its next start).
			File.WriteAllText(_fork, JsonSerializer.Serialize(new ResgridApiTokenState { AccessToken = "test", RefreshToken = "rotated-1" }));

			TokenCacheFork.HandBack(_fork, _relayCache, null).Should().BeTrue();
			ReadState(_relayCache).RefreshToken.Should().Be("rotated-1");
		}

		/// <summary>Answers OIDC discovery, the token endpoint (rotating the refresh token) and every API call with 200.</summary>
		private sealed class TokenEndpointStub : HttpMessageHandler
		{
			public List<Dictionary<string, string>> TokenRequests { get; } = new List<Dictionary<string, string>>();

			protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
			{
				var path = request.RequestUri.AbsolutePath;
				if (path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
					return Json($"{{\"issuer\":\"{BaseUrl}/\",\"token_endpoint\":\"{BaseUrl}/connect/token\"}}");

				if (path.EndsWith("/connect/token", StringComparison.Ordinal))
				{
					var form = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
					TokenRequests.Add(form.Split('&')
						.Select(pair => pair.Split('=', 2))
						.Where(pair => pair.Length == 2)
						.ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1].Replace('+', ' '))));
					return Json($"{{\"access_token\":\"opaque-{TokenRequests.Count}\",\"refresh_token\":\"rotated-{TokenRequests.Count}\",\"expires_in\":3600,\"token_type\":\"Bearer\"}}");
				}

				return Json("{}");
			}

			private static HttpResponseMessage Json(string body) =>
				new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
		}
	}
}
