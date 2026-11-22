using Resgrid.Relay.Engine.Configuration;
using Resgrid.Providers.ApiClient.V4.Models;
using StackExchange.Redis;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Resgrid.Relay.Engine.Smtp
{
	/// <summary>
	/// Redis-backed implementation of <see cref="IDispatchLookupCache"/>.
	/// Stores lookup results as JSON with a configurable TTL.
	/// 
	/// Cache key format:  relay:lookup:{scope}:{entityType}:{lookupType}:{departmentId}:{code}
	///
	/// Example:  relay:lookup:3f2a9c0d11e4b7a8:group:dispatch:dept123:station5
	///           → {"GroupId":"42","Name":"Station 5","DepartmentId":"123","Type":0}
	///
	/// The scope names the credential the lookup was made with (<see cref="Resgrid.Providers.ApiClient.V4.IResgridApiClient.CredentialScope"/>):
	/// a department token resolves codes only within its own department and sends no department id, so without it two
	/// single-department relays sharing one Redis would read each other's group ids.
	///
	/// Redis is only an optimisation. While it is unreachable the cache is skipped (every call is a miss / no-op) instead of
	/// holding up a dispatch: commands fail fast rather than queueing, and after a failed connect the cache is not tried
	/// again for <see cref="ReconnectBackoff"/>.
	/// </summary>
	internal sealed class RedisDispatchLookupCache : IDispatchLookupCache
	{
		private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
		{
			PropertyNameCaseInsensitive = true,
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase
		};

		internal static readonly TimeSpan ReconnectBackoff = TimeSpan.FromSeconds(30);

		private readonly RedisCacheOptions _options;
		private readonly string _keyPrefix;
		private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
		private ConnectionMultiplexer _redis;
		private IDatabase _db;
		private DateTimeOffset _retryConnectAfterUtc = DateTimeOffset.MinValue;
		private bool _disposed;

		public RedisDispatchLookupCache(RedisCacheOptions options, string credentialScope = null)
		{
			_options = options ?? throw new ArgumentNullException(nameof(options));
			_keyPrefix = String.IsNullOrWhiteSpace(credentialScope) ? "relay:lookup:" : $"relay:lookup:{credentialScope.Trim()}:";
		}

		/// <summary>The database, or null when Redis cannot be used right now (the caller then skips the cache).</summary>
		private async Task<IDatabase> GetDatabaseAsync()
		{
			if (_disposed)
				return null;

			if (_db != null)
				// The multiplexer keeps reconnecting in the background (abortConnect=false); until it is back, skip the cache.
				return _redis.IsConnected ? _db : null;

			if (DateTimeOffset.UtcNow < _retryConnectAfterUtc)
				return null;

			// One connect at a time, and nobody waits behind it: a lookup that finds a connect in progress just skips the cache.
			if (!await _lock.WaitAsync(0).ConfigureAwait(false))
				return null;

			try
			{
				if (_db != null)
					return _redis.IsConnected ? _db : null;

				try
				{
					var configuration = ConfigurationOptions.Parse(_options.ConnectionString);
					configuration.AbortOnConnectFail = false;
					configuration.BacklogPolicy = BacklogPolicy.FailFast;

					_redis = await ConnectionMultiplexer.ConnectAsync(configuration).ConfigureAwait(false);
				}
				catch (Exception)
				{
					_retryConnectAfterUtc = DateTimeOffset.UtcNow.Add(ReconnectBackoff);
					return null;
				}

				_db = _redis.GetDatabase();
				if (!_redis.IsConnected)
				{
					// The first attempt failed; the multiplexer retries on its own, and the cache is skipped until it succeeds.
					_retryConnectAfterUtc = DateTimeOffset.UtcNow.Add(ReconnectBackoff);
					return null;
				}

				return _db;
			}
			finally
			{
				_lock.Release();
			}
		}

		public async ValueTask DisposeAsync()
		{
			_disposed = true;
			await _lock.WaitAsync().ConfigureAwait(false);
			try
			{
				if (_redis != null)
				{
					try
					{
						await _redis.CloseAsync().ConfigureAwait(false);
					}
					catch (Exception)
					{
						// Closing a connection that never came up must not fail the relay's shutdown.
					}

					_redis.Dispose();
					_redis = null;
					_db = null;
				}
			}
			finally
			{
				_lock.Release();
			}
		}

		private TimeSpan Ttl => TimeSpan.FromMinutes(_options.TtlMinutes > 0 ? _options.TtlMinutes : 60);

		// ─── Key helpers ───────────────────────────────────────────────────

		internal string Key(string entityType, string lookupType, string departmentId, string code) =>
			$"{_keyPrefix}{entityType}:{lookupType}:{departmentId ?? "__"}:{code}";

		// ─── Generic read/write ────────────────────────────────────────────

		private async Task<T> GetAsync<T>(string key) where T : class
		{
			try
			{
				var db = await GetDatabaseAsync().ConfigureAwait(false);
				if (db == null)
					return null;

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
				if (db == null)
					return;

				var json = JsonSerializer.Serialize(value, SerializerOptions);
				await db.StringSetAsync(key, json, Ttl).ConfigureAwait(false);
			}
			catch
			{
				// Redis failure is non-fatal — the lookup still succeeded via
				// the live API; we just won't have it cached for next time.
			}
		}

		// ─── Department dispatch code ─────────────────────────────────────

		public Task<DepartmentLookupResult> GetDepartmentByDispatchCodeAsync(string code) =>
			GetAsync<DepartmentLookupResult>(Key("department", "dispatch", null, code));

		public Task SetDepartmentByDispatchCodeAsync(string code, DepartmentLookupResult result) =>
			SetAsync(Key("department", "dispatch", null, code), result);

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
