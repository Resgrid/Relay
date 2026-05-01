using Resgrid.Providers.ApiClient.V4.Models;
using System.Threading.Tasks;

namespace Resgrid.Audio.Relay.Console.Smtp
{
	/// <summary>
	/// No-op cache implementation used when Redis caching is disabled.
	/// Always returns null (cache miss) and ignores all Set operations.
	/// </summary>
	internal sealed class NullDispatchLookupCache : IDispatchLookupCache
	{
		public ValueTask DisposeAsync() => default;

		public Task<GroupLookupResult> GetGroupByDispatchCodeAsync(string code, string departmentId) =>
			Task.FromResult<GroupLookupResult>(null);

		public Task<GroupLookupResult> GetGroupByMessageCodeAsync(string code, string departmentId) =>
			Task.FromResult<GroupLookupResult>(null);

		public Task<UnitLookupResult> GetUnitByNameAsync(string name, string departmentId) =>
			Task.FromResult<UnitLookupResult>(null);

		public Task<RoleLookupResult> GetRoleByNameAsync(string name, string departmentId) =>
			Task.FromResult<RoleLookupResult>(null);

		public Task SetGroupByDispatchCodeAsync(string code, string departmentId, GroupLookupResult result) =>
			Task.CompletedTask;

		public Task SetGroupByMessageCodeAsync(string code, string departmentId, GroupLookupResult result) =>
			Task.CompletedTask;

		public Task SetUnitByNameAsync(string name, string departmentId, UnitLookupResult result) =>
			Task.CompletedTask;

		public Task SetRoleByNameAsync(string name, string departmentId, RoleLookupResult result) =>
			Task.CompletedTask;
	}
}
