using Resgrid.Providers.ApiClient.V4.Models;
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Resgrid.Providers.ApiClient.V4
{
	/// <summary>
	/// v4 API methods for the Resgrid voice (push-to-talk) subsystem. These return
	/// the LiveKit connection details (server URL + per-channel room id and access
	/// token) the relay needs to join a department's PTT channel, and the seat-limit
	/// check used before connecting.
	///
	/// Mirrors the Resgrid Core VoiceController:
	///   GET /api/v4/Voice/GetDepartmentVoiceSettings
	///   GET /api/v4/Voice/CanConnectToVoiceSession?token={token}
	/// </summary>
	public static class VoiceApi
	{
		/// <summary>
		/// Gets the calling user's department voice settings, including the LiveKit
		/// server URL and every voice channel with a pre-minted join token.
		///
		/// <paramref name="departmentId"/> is optional and only sent when supplied;
		/// it is used by hosted/system-key deployments that act on behalf of a
		/// specific department. In normal (per-user) mode the department is taken
		/// from the authenticated context.
		/// </summary>
		public static async Task<DepartmentVoiceResultData> GetDepartmentVoiceSettingsAsync(
			string departmentId = null,
			CancellationToken cancellationToken = default)
		{
			var url = "Voice/GetDepartmentVoiceSettings";
			if (!String.IsNullOrWhiteSpace(departmentId))
				url += $"?departmentId={Uri.EscapeDataString(departmentId)}";

			var response = await ResgridV4ApiClient
				.GetAsync<DepartmentVoiceResult>(url, cancellationToken)
				.ConfigureAwait(false);

			return response?.Data;
		}

		/// <summary>
		/// Checks whether another participant may connect to the department's voice
		/// session without exceeding the subscription seat limit. The token is the
		/// <see cref="DepartmentVoiceResultData.CanConnectApiToken"/> value returned by
		/// <see cref="GetDepartmentVoiceSettingsAsync"/>. Returns null if the endpoint
		/// is unavailable (treat as "unknown" and proceed per policy).
		/// </summary>
		public static async Task<CanConnectToVoiceSessionResultData> CanConnectToVoiceSessionAsync(
			string token,
			CancellationToken cancellationToken = default)
		{
			if (String.IsNullOrWhiteSpace(token))
				return null;

			var url = $"Voice/CanConnectToVoiceSession?token={Uri.EscapeDataString(token)}";

			try
			{
				var response = await ResgridV4ApiClient
					.GetAsync<CanConnectToVoiceSessionResult>(url, cancellationToken)
					.ConfigureAwait(false);

				return response?.Data;
			}
			catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
			{
				return null;
			}
		}
	}
}
