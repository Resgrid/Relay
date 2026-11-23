using Resgrid.Providers.ApiClient.V4;
using Resgrid.Providers.ApiClient.V4.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Resgrid.Relay.Engine.Smtp
{
	/// <summary>
	/// Wraps <see cref="LookupsApi"/> with a cache-first strategy.
	/// On cache hit, the cached result is returned immediately.
	/// On cache miss, the live API is called and the result is stored
	/// in the cache for subsequent lookups.
	/// 
	/// This is the single entry point all dispatch resolution code should
	/// use instead of calling <see cref="LookupsApi"/> directly.
	/// </summary>
	internal sealed class CachedLookupsService
	{
		private readonly IDispatchLookupCache _cache;
		private readonly IResgridLookupsApi _lookupsApi;

		public CachedLookupsService(IDispatchLookupCache cache, IResgridLookupsApi lookupsApi)
		{
			_cache = cache ?? throw new ArgumentNullException(nameof(cache));
			_lookupsApi = lookupsApi;
		}

		/// <summary>True when there is an API to resolve codes against (there is none in some unit tests).</summary>
		public bool CanLookup => _lookupsApi != null;

		private IResgridLookupsApi LookupsApi =>
			_lookupsApi ?? throw new InvalidOperationException("No Resgrid lookups API is configured to resolve dispatch codes.");

		/// <summary>
		/// Resolves a department dispatch email code to its department, or null when no department (visible to this relay's
		/// credentials) has it.
		/// </summary>
		public async Task<DepartmentLookupResult> LookupDepartmentByDispatchCodeAsync(string code, CancellationToken cancellationToken = default)
		{
			if (String.IsNullOrWhiteSpace(code))
				return null;

			var cached = await _cache.GetDepartmentByDispatchCodeAsync(code).ConfigureAwait(false);
			if (cached != null)
				return cached;

			var result = await LookupsApi.LookupDepartmentByDispatchCodeAsync(code, cancellationToken).ConfigureAwait(false);
			if (result != null)
				await _cache.SetDepartmentByDispatchCodeAsync(code, result).ConfigureAwait(false);

			return result;
		}

		/// <summary>
		/// Resolves a group dispatch email code to a <see cref="GroupLookupResult"/>.
		/// </summary>
		public async Task<GroupLookupResult> LookupGroupByDispatchCodeAsync(
			string code,
			string departmentId,
			CancellationToken cancellationToken = default)
		{
			if (String.IsNullOrWhiteSpace(code))
				return null;

			var cached = await _cache.GetGroupByDispatchCodeAsync(code, departmentId).ConfigureAwait(false);
			if (cached != null)
				return cached;

			var result = await LookupsApi.LookupGroupByDispatchCodeAsync(code, departmentId, cancellationToken).ConfigureAwait(false);
			if (result != null)
				await _cache.SetGroupByDispatchCodeAsync(code, departmentId, result).ConfigureAwait(false);

			return result;
		}

		/// <summary>
		/// Resolves a group message email code to a <see cref="GroupLookupResult"/>.
		/// </summary>
		public async Task<GroupLookupResult> LookupGroupByMessageCodeAsync(
			string code,
			string departmentId,
			CancellationToken cancellationToken = default)
		{
			if (String.IsNullOrWhiteSpace(code))
				return null;

			var cached = await _cache.GetGroupByMessageCodeAsync(code, departmentId).ConfigureAwait(false);
			if (cached != null)
				return cached;

			var result = await LookupsApi.LookupGroupByMessageCodeAsync(code, departmentId, cancellationToken).ConfigureAwait(false);
			if (result != null)
				await _cache.SetGroupByMessageCodeAsync(code, departmentId, result).ConfigureAwait(false);

			return result;
		}

		/// <summary>
		/// Resolves a unit name to a <see cref="UnitLookupResult"/>.
		/// </summary>
		public async Task<UnitLookupResult> LookupUnitByNameAsync(
			string name,
			string departmentId,
			CancellationToken cancellationToken = default)
		{
			if (String.IsNullOrWhiteSpace(name))
				return null;

			var cached = await _cache.GetUnitByNameAsync(name, departmentId).ConfigureAwait(false);
			if (cached != null)
				return cached;

			var result = await LookupsApi.LookupUnitByNameAsync(name, departmentId, cancellationToken).ConfigureAwait(false);
			if (result != null)
				await _cache.SetUnitByNameAsync(name, departmentId, result).ConfigureAwait(false);

			return result;
		}

		/// <summary>
		/// Resolves a role name to a <see cref="RoleLookupResult"/>.
		/// </summary>
		public async Task<RoleLookupResult> LookupRoleByNameAsync(
			string name,
			string departmentId,
			CancellationToken cancellationToken = default)
		{
			if (String.IsNullOrWhiteSpace(name))
				return null;

			var cached = await _cache.GetRoleByNameAsync(name, departmentId).ConfigureAwait(false);
			if (cached != null)
				return cached;

			var result = await LookupsApi.LookupRoleByNameAsync(name, departmentId, cancellationToken).ConfigureAwait(false);
			if (result != null)
				await _cache.SetRoleByNameAsync(name, departmentId, result).ConfigureAwait(false);

			return result;
		}
	}
}
