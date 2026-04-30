using Resgrid.Audio.Relay.Console.Configuration;
using Resgrid.Providers.ApiClient.V4.Models;
using StackExchange.Redis;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Resgrid.Audio.Relay.Console.Smtp
{
	/// <summary>
	/// Redis-backed implementation of <see cref="IDispatchLookupCache"/>.
	/// Stores lookup results as JSON with a configurable TTL.
	/// 
	/// Cache key format:  relay:lookup:{entityType}:{lookupType}:{departmentId}:{code}
	/// 
	/// Example:  relay:lookup:group:dispatch:dept123:station5
	///           → {"GroupId":"42","Name":"Station 5","DepartmentId":"123","Type":0}
	/// </summary>
	internal sealed class RedisDispatchLookupCache : IDispatchLookupCache
	{
		private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
		{
			PropertyNameCaseInsensitive = true,
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase
		};

		private readonly RedisCacheOptions _options;
		private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
		private ConnectionMultiplexer _redis;
		private IDatabase _db;

		public RedisDispatchLookupCache(RedisCacheOptions options)
		{
			_options = options ?? throw new ArgumentNullException(nameof(options));
		}

		private async Task<IDatabase> GetDatabaseAsync()
		{
			if (_db != null)
				return _db;

			await _lock.WaitAsync().ConfigureAwait(false);
			try
			{
				if (_db != null)
					return _db;

				_redis = await ConnectionMultiplexer.ConnectAsync(_options.ConnectionString).ConfigureAwait(false);
				_db = _redis.GetDatabase();
				return _db;
			}
			finally
			{
				_lock.Release();
			}
		}

		public async ValueTask DisposeAsync()
		{
			if (_redis != null)
			{
				await _redis.CloseAsync().ConfigureAwait(false);
				_redis.Dispose();
				_redis = null;
				_db = null;
			}
			_lock.Dispose();
		}

		private TimeSpan Ttl => TimeSpan.FromMinutes(_options.TtlMinutes > 0 ? _options.TtlMinutes : 60);

		// ─── Key helpers ───────────────────────────────────────────────────

		private static string Key(string entityType, string lookupType, string departmentId, string code) =>
			$"relay:lookup:{entityType}:{lookupType}:{departmentId ?? "__"}:{code}";

		// ─── Generic read/write ────────────────────────────────────────────

		private async Task<T> GetAsync<T>(string key) where T : class
		{
			try
			{
				var db = await GetDatabaseAsync().ConfigureAwait(false);
				var json = await db.StringGetAsync(key).ConfigureAwait(false);
				if (json.IsNullOrEmpty)
					return null;

				return JsonSerializer.Deserialize<T>(json.ToString(), SerializerOptions);
			}
			catch (Exception)
			{
				// Redis failure must not take down the relay — treat as cache miss.
				return null;
			}
		}

		private async Task SetAsync<T>(string key, T value) where T : class
		{
			try
			{
				var db = await GetDatabaseAsync().ConfigureAwait(false);
				var json = JsonSerializer.Serialize(value, SerializerOptions);
				await db.StringSetAsync(key, json, Ttl).ConfigureAwait(false);
			}
			catch
			{
				// Redis failure is non-fatal — the lookup still succeeded via
				// the live API; we just won't have it cached for next time.
			}
		}

		// ─── Group dispatch code ──────────────────────────────────────────

		public Task<GroupLookupResult> GetGroupByDispatchCodeAsync(string code, string departmentId) =>
			GetAsync<GroupLookupResult>(Key("group", "dispatch", departmentId, code));

		public Task SetGroupByDispatchCodeAsync(string code, string departmentId, GroupLookupResult result) =>
			SetAsync(Key("group", "dispatch", departmentId, code), result);

		// ─── Group message code ───────────────────────────────────────────

		public Task<GroupLookupResult> GetGroupByMessageCodeAsync(string code, string departmentId) =>
			GetAsync<GroupLookupResult>(Key("group", "message", departmentId, code));

		public Task SetGroupByMessageCodeAsync(string code, string departmentId, GroupLookupResult result) =>
			SetAsync(Key("group", "message", departmentId, code), result);

		// ─── Unit name ────────────────────────────────────────────────────

		public Task<UnitLookupResult> GetUnitByNameAsync(string name, string departmentId) =>
			GetAsync<UnitLookupResult>(Key("unit", "name", departmentId, name));

		public Task SetUnitByNameAsync(string name, string departmentId, UnitLookupResult result) =>
			SetAsync(Key("unit", "name", departmentId, name), result);

		// ─── Role name ────────────────────────────────────────────────────

		public Task<RoleLookupResult> GetRoleByNameAsync(string name, string departmentId) =>
			GetAsync<RoleLookupResult>(Key("role", "name", departmentId, name));

		public Task SetRoleByNameAsync(string name, string departmentId, RoleLookupResult result) =>
			SetAsync(Key("role", "name", departmentId, name), result);
	}
}
