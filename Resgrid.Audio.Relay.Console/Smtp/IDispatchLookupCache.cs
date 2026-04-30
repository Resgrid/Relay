using Resgrid.Providers.ApiClient.V4.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Resgrid.Audio.Relay.Console.Smtp
{
	/// <summary>
	/// Caches dispatch code lookup results so that repeated lookups for the
	/// same department/group/unit/role codes don't hit the Resgrid API on
	/// every inbound email.
	/// 
	/// All methods return null on cache miss — the caller falls back to the
	/// live API and then stores the result via <see cref="SetAsync"/>.
	/// </summary>
	public interface IDispatchLookupCache : IAsyncDisposable
	{
		/// <summary>Retrieve a cached group lookup by dispatch email code.</summary>
		Task<GroupLookupResult> GetGroupByDispatchCodeAsync(string code, string departmentId);

		/// <summary>Retrieve a cached group lookup by message email code.</summary>
		Task<GroupLookupResult> GetGroupByMessageCodeAsync(string code, string departmentId);

		/// <summary>Retrieve a cached unit lookup by name.</summary>
		Task<UnitLookupResult> GetUnitByNameAsync(string name, string departmentId);

		/// <summary>Retrieve a cached role lookup by name.</summary>
		Task<RoleLookupResult> GetRoleByNameAsync(string name, string departmentId);

		/// <summary>Store a group dispatch code lookup result in the cache.</summary>
		Task SetGroupByDispatchCodeAsync(string code, string departmentId, GroupLookupResult result);

		/// <summary>Store a group message code lookup result in the cache.</summary>
		Task SetGroupByMessageCodeAsync(string code, string departmentId, GroupLookupResult result);

		/// <summary>Store a unit name lookup result in the cache.</summary>
		Task SetUnitByNameAsync(string name, string departmentId, UnitLookupResult result);

		/// <summary>Store a role name lookup result in the cache.</summary>
		Task SetRoleByNameAsync(string name, string departmentId, RoleLookupResult result);
	}
}
