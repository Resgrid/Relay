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
	/// Every lookup returns null for a code Resgrid does not know (HTTP 200, Status "not_found"). Any other failure, a 404
	/// included, throws: Resgrid has all of these endpoints, so a 404 is a routing or deployment problem worth retrying.
	/// </summary>
	public interface IResgridLookupsApi
	{
		Task<DepartmentLookupResult> LookupDepartmentByDispatchCodeAsync(string code, CancellationToken cancellationToken = default);
		Task<GroupLookupResult> LookupGroupByDispatchCodeAsync(string code, string departmentId, CancellationToken cancellationToken = default);
		Task<GroupLookupResult> LookupGroupByMessageCodeAsync(string code, string departmentId, CancellationToken cancellationToken = default);
		Task<UnitLookupResult> LookupUnitByNameAsync(string name, string departmentId, CancellationToken cancellationToken = default);
		Task<RoleLookupResult> LookupRoleByNameAsync(string name, string departmentId, CancellationToken cancellationToken = default);
	}

	public sealed class LookupsApi : IResgridLookupsApi
	{
		private readonly IResgridApiClient _client;

		public LookupsApi(IResgridApiClient client)
		{
			_client = client ?? throw new ArgumentNullException(nameof(client));
		}

		/// <summary>
		/// Resolves a department dispatch email code (the local part of code@dispatch-domain) to its department.
		/// Maps to: GET /api/v4/Departments/GetDepartmentByDispatchCode?code={code}
		///
		/// This is what makes the code a secret: without it any address at the dispatch domain would page the whole department.
		/// Returns null when no department has the code, and (for a token caller) when it belongs to another department.
		/// </summary>
		public async Task<DepartmentLookupResult> LookupDepartmentByDispatchCodeAsync(string code, CancellationToken cancellationToken = default)
		{
			if (String.IsNullOrWhiteSpace(code))
				return null;

			var result = await TryGetAsync<DepartmentLookupResult>($"Departments/GetDepartmentByDispatchCode?code={Uri.EscapeDataString(code)}",
				cancellationToken, otherDepartmentIsNotFound: true).ConfigureAwait(false);

			return String.IsNullOrWhiteSpace(result?.DepartmentId) ? null : result;
		}

		/// <summary>
		/// Resolves a group dispatch email code to its numeric group ID.
		/// Maps to: GET /api/v4/Groups/GetGroupByDispatchCode?code={code}&amp;departmentId={departmentId}
		///
		/// Behind the scenes this queries DepartmentGroups.DispatchEmail.
		/// Returns null when no group has the code.
		///
		/// </summary>
		public async Task<GroupLookupResult> LookupGroupByDispatchCodeAsync(
			string code,
			string departmentId,
			CancellationToken cancellationToken = default)
		{
			if (String.IsNullOrWhiteSpace(code))
				return null;

			var url = $"Groups/GetGroupByDispatchCode?code={Uri.EscapeDataString(code)}";

			if (!String.IsNullOrWhiteSpace(departmentId))
				url += $"&departmentId={Uri.EscapeDataString(departmentId)}";

			var result = await TryGetAsync<GroupLookupResult>(url, cancellationToken, otherDepartmentIsNotFound: true).ConfigureAwait(false);
			return String.IsNullOrWhiteSpace(result?.GroupId) ? null : result;
		}

		/// <summary>
		/// Resolves a group message email code to its numeric group ID.
		/// Maps to: GET /api/v4/Groups/GetGroupByMessageCode?code={code}&amp;departmentId={departmentId}
		/// 
		/// Behind the scenes this queries DepartmentGroups.MessageEmail.
		/// Returns null when nothing has that code or name.
		/// </summary>
		public async Task<GroupLookupResult> LookupGroupByMessageCodeAsync(
			string code,
			string departmentId,
			CancellationToken cancellationToken = default)
		{
			if (String.IsNullOrWhiteSpace(code))
				return null;

			var url = $"Groups/GetGroupByMessageCode?code={Uri.EscapeDataString(code)}";

			if (!String.IsNullOrWhiteSpace(departmentId))
				url += $"&departmentId={Uri.EscapeDataString(departmentId)}";

			var result = await TryGetAsync<GroupLookupResult>(url, cancellationToken, otherDepartmentIsNotFound: true).ConfigureAwait(false);
			return String.IsNullOrWhiteSpace(result?.GroupId) ? null : result;
		}

		/// <summary>
		/// Resolves a unit name to its numeric unit ID.
		/// Maps to: GET /api/v4/Units/GetUnitByName?name={name}&amp;departmentId={departmentId}
		/// 
		/// Behind the scenes this queries Units.UnitName.
		/// Returns null when nothing has that code or name.
		/// </summary>
		public async Task<UnitLookupResult> LookupUnitByNameAsync(
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
		/// Returns null when nothing has that code or name.
		/// </summary>
		public async Task<RoleLookupResult> LookupRoleByNameAsync(
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

		private async Task<T> TryGetAsync<T>(string url, CancellationToken cancellationToken, bool otherDepartmentIsNotFound = false) where T : class
		{
			try
			{
				var response = await _client.GetAsync<LookupResponse<T>>(url, cancellationToken).ConfigureAwait(false);

				// Resgrid answers a miss with HTTP 200, Status "not_found" and an empty Data object.
				if (String.Equals(response?.Status, "not_found", StringComparison.OrdinalIgnoreCase))
					return null;

				return response?.Data;
			}
			// No catch for 404: every lookup endpoint exists and reports a miss as 200 + "not_found", so a 404 means the request
			// never reached it (a proxy or deploy problem, a wrong base URL or API version). It must surface and be retried, not
			// read as "no such code", which would bounce real dispatches.
			catch (HttpRequestException ex) when (otherDepartmentIsNotFound && !_client.IsSystemCaller &&
				(ex.StatusCode == HttpStatusCode.Unauthorized || ex.StatusCode == HttpStatusCode.Forbidden))
			{
				// A token caller already holding a fresh token (the client retries a 401 once after refreshing) is told 401 when the
				// code belongs to another department: for this relay that code does not exist. With the system key a 401 means the
				// key itself is wrong, which must surface (and be retried) instead of silently dropping every dispatch.
				return null;
			}
		}
	}
}
