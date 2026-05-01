using Resgrid.Providers.ApiClient.V4.Models;
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Resgrid.Providers.ApiClient.V4
{
	/// <summary>
	/// Provides v4 API methods to resolve dispatch codes (names like "STATION5")
	/// into numeric IDs required by the DispatchList parameter in SaveCall.
	/// 
	/// These endpoints correspond to the new lookup APIs that need to be built
	/// in the Resgrid API project. Until they exist, callers should handle the
	/// expected 404 gracefully and fall back to code-based DispatchList values.
	/// </summary>
	public static class LookupsApi
	{
		/// <summary>
		/// Resolves a group dispatch email code to its numeric group ID.
		/// Maps to: GET /api/v4/Groups/GetGroupByDispatchCode?code={code}&amp;departmentId={departmentId}
		/// 
		/// Behind the scenes this queries DepartmentGroups.DispatchEmail.
		/// Returns null when the endpoint returns 404 (group not found or API
		/// endpoint not yet deployed).
		/// </summary>
		public static async Task<GroupLookupResult> LookupGroupByDispatchCodeAsync(
			string code,
			string departmentId,
			CancellationToken cancellationToken = default)
		{
			if (String.IsNullOrWhiteSpace(code))
				return null;

			var url = $"Groups/GetGroupByDispatchCode?code={Uri.EscapeDataString(code)}";

			if (!String.IsNullOrWhiteSpace(departmentId))
				url += $"&departmentId={Uri.EscapeDataString(departmentId)}";

			return await TryGetAsync<GroupLookupResult>(url, cancellationToken).ConfigureAwait(false);
		}

		/// <summary>
		/// Resolves a group message email code to its numeric group ID.
		/// Maps to: GET /api/v4/Groups/GetGroupByMessageCode?code={code}&amp;departmentId={departmentId}
		/// 
		/// Behind the scenes this queries DepartmentGroups.MessageEmail.
		/// Returns null when the endpoint returns 404.
		/// </summary>
		public static async Task<GroupLookupResult> LookupGroupByMessageCodeAsync(
			string code,
			string departmentId,
			CancellationToken cancellationToken = default)
		{
			if (String.IsNullOrWhiteSpace(code))
				return null;

			var url = $"Groups/GetGroupByMessageCode?code={Uri.EscapeDataString(code)}";

			if (!String.IsNullOrWhiteSpace(departmentId))
				url += $"&departmentId={Uri.EscapeDataString(departmentId)}";

			return await TryGetAsync<GroupLookupResult>(url, cancellationToken).ConfigureAwait(false);
		}

		/// <summary>
		/// Resolves a unit name to its numeric unit ID.
		/// Maps to: GET /api/v4/Units/GetUnitByName?name={name}&amp;departmentId={departmentId}
		/// 
		/// Behind the scenes this queries Units.UnitName.
		/// Returns null when the endpoint returns 404.
		/// </summary>
		public static async Task<UnitLookupResult> LookupUnitByNameAsync(
			string name,
			string departmentId,
			CancellationToken cancellationToken = default)
		{
			if (String.IsNullOrWhiteSpace(name))
				return null;

			var url = $"Units/GetUnitByName?name={Uri.EscapeDataString(name)}";

			if (!String.IsNullOrWhiteSpace(departmentId))
				url += $"&departmentId={Uri.EscapeDataString(departmentId)}";

			return await TryGetAsync<UnitLookupResult>(url, cancellationToken).ConfigureAwait(false);
		}

		/// <summary>
		/// Resolves a role name to its numeric role ID.
		/// Maps to: GET /api/v4/Roles/GetRoleByName?name={name}&amp;departmentId={departmentId}
		/// 
		/// Behind the scenes this queries PersonnelRoles.Name.
		/// Returns null when the endpoint returns 404.
		/// </summary>
		public static async Task<RoleLookupResult> LookupRoleByNameAsync(
			string name,
			string departmentId,
			CancellationToken cancellationToken = default)
		{
			if (String.IsNullOrWhiteSpace(name))
				return null;

			var url = $"Roles/GetRoleByName?name={Uri.EscapeDataString(name)}";

			if (!String.IsNullOrWhiteSpace(departmentId))
				url += $"&departmentId={Uri.EscapeDataString(departmentId)}";

			return await TryGetAsync<RoleLookupResult>(url, cancellationToken).ConfigureAwait(false);
		}

		private static async Task<T> TryGetAsync<T>(string url, CancellationToken cancellationToken) where T : class
		{
			try
			{
				var response = await ResgridV4ApiClient.GetAsync<LookupResponse<T>>(url, cancellationToken).ConfigureAwait(false);
				return response?.Data;
			}
			catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
			{
				// Entity not found, or the API endpoint doesn't exist yet.
				return null;
			}
		}
	}
}
