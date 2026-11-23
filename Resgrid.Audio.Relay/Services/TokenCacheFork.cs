using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Resgrid.Providers.ApiClient.V4.Models;

namespace Resgrid.Audio.Relay.Services
{
	/// <summary>
	/// Lets the Configuration screen's connection test check an edited client secret on the relay's own refresh-token chain.
	/// Signing in on the relay's token cache "passed" on its live access token without the edited secret ever being sent, so
	/// the test signs in on a copy without the access token (<see cref="Fork"/>), which has to redeem the relay's current
	/// refresh token with the edited secret. Resgrid rotates the refresh token on that redemption, spending the relay's copy,
	/// so the rotated chain is handed straight back to the relay's cache (<see cref="HandBack"/>). A refresh token belongs to
	/// the client id, not to the secret it was redeemed with, so the relay keeps using it. The copy does not share the API
	/// client's per-cache sign-in lock: a relay mode redeeming the same token in that moment still races it, and
	/// <see cref="HandBack"/> then keeps the relay's chain.
	/// </summary>
	public static class TokenCacheFork
	{
		// As the API client writes its cache.
		private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
		{
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
			PropertyNameCaseInsensitive = true,
			WriteIndented = true
		};

		/// <summary>
		/// Writes the token state in <paramref name="relayCachePath"/> to <paramref name="forkPath"/> without its access token, so a
		/// sign-in on the fork redeems the relay's refresh token. Returns that refresh token. Null when the relay has no usable
		/// cache: no fork is left then, and a sign-in redeems the configured refresh token, as the relay's would.
		/// </summary>
		public static string Fork(string relayCachePath, string forkPath)
		{
			var state = Read(relayCachePath);
			if (state == null)
			{
				if (File.Exists(forkPath))
					File.Delete(forkPath);
				return null;
			}

			state.AccessToken = null;
			state.ExpiresAtUtc = DateTimeOffset.MinValue;
			CreateDirectoryFor(forkPath);
			File.WriteAllText(forkPath, JsonSerializer.Serialize(state, JsonOptions));
			return state.RefreshToken;
		}

		/// <summary>
		/// After a sign-in on a <see cref="Fork"/> that rotated the refresh token it was given (<paramref name="forkedRefreshToken"/>,
		/// now spent), moves the fork into <paramref name="relayCachePath"/> and returns true. Returns false, removing the fork,
		/// when there is nothing to hand back: the sign-in redeemed nothing (the edited secret was refused) or got no new token,
		/// or the relay's cache no longer holds the forked token (the relay redeemed it itself, and keeps its own chain). Throws
		/// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>, leaving the fork in place, when it cannot be moved.
		/// </summary>
		public static bool HandBack(string forkPath, string relayCachePath, string forkedRefreshToken)
		{
			var tested = Read(forkPath);
			var rotated = tested != null && !string.IsNullOrWhiteSpace(tested.RefreshToken)
				&& !string.Equals(tested.RefreshToken, forkedRefreshToken, StringComparison.Ordinal);
			if (!rotated || !string.Equals(Read(relayCachePath)?.RefreshToken, forkedRefreshToken, StringComparison.Ordinal))
			{
				try { File.Delete(forkPath); }
				catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { /* only holds an unused copy */ }
				return false;
			}

			CreateDirectoryFor(relayCachePath);
			File.Move(forkPath, relayCachePath, overwrite: true);
			return true;
		}

		private static ResgridApiTokenState Read(string path)
		{
			if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
				return null;

			try
			{
				var payload = File.ReadAllText(path);
				return string.IsNullOrWhiteSpace(payload) ? null : JsonSerializer.Deserialize<ResgridApiTokenState>(payload, JsonOptions);
			}
			catch (JsonException)
			{
				// Damaged: the API client ignores it too, and starts from the configured refresh token.
				return null;
			}
		}

		private static void CreateDirectoryFor(string path)
		{
			var directory = Path.GetDirectoryName(Path.GetFullPath(path));
			if (!string.IsNullOrWhiteSpace(directory))
				Directory.CreateDirectory(directory);
		}
	}
}
