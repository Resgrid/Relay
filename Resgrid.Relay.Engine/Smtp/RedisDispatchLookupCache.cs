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
	/// holding up a dispatch: the connection is opened in the background (lookups skip the cache until it is up), commands fail
	/// fast rather than queueing, and after a connect that fails outright the cache is not tried again for
	/// <see cref="ReconnectBackoff"/>.
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

		// Guards _redis/_db/_connecting/_retryConnectAfterTicks changes and _disposed's effect on them; never held across an await.
		private readonly object _gate = new object();
		private ConnectionMultiplexer _redis;
		private IDatabase _db;
		private Task _connecting;
		private long _retryConnectAfterTicks = DateTimeOffset.MinValue.UtcTicks;
		private volatile bool _disposed;

		public RedisDispatchLookupCache(RedisCacheOptions options, string credentialScope = null)
		{
			_options = options ?? throw new ArgumentNullException(nameof(options));
			_keyPrefix = String.IsNullOrWhiteSpace(credentialScope) ? "relay:lookup:" : $"relay:lookup:{credentialScope.Trim()}:";
		}

		private bool InReconnectBackoff => DateTimeOffset.UtcNow.UtcTicks < Interlocked.Read(ref _retryConnectAfterTicks);

		/// <summary>
		/// The database, or null when Redis cannot be used right now (the caller then skips the cache). Never waits: the first
		/// lookup starts the connect in the background and every lookup skips the cache until it is up, since StackExchange.Redis
		/// waits out its whole connect timeout before ConnectAsync returns (whatever the backlog policy), and an unreachable Redis
		/// must not hold up a dispatch.
		/// </summary>
		private IDatabase GetDatabase()
		{
			if (_disposed)
				return null;

			var redis = Volatile.Read(ref _redis);
			if (redis != null)
				// The multiplexer keeps reconnecting in the background (abortConnect=false); until it is back, skip the cache.
				return redis.IsConnected ? Volatile.Read(ref _db) : null;

			if (InReconnectBackoff)
				return null;

			lock (_gate)
			{
				// Checked again here: DisposeAsync may have run, or a connect may have failed and started the backoff, since the
				// checks above. One connect at a time.
				if (_disposed || _redis != null || _connecting != null || InReconnectBackoff)
					return null;

				_connecting = Task.Run(ConnectAsync);
			}

			return null;
		}

		/// <summary>Never throws: a connect that fails starts the backoff, one that finishes after disposal closes itself.</summary>
		private async Task ConnectAsync()
		{
			ConnectionMultiplexer redis;
			try
			{
				var configuration = ConfigurationOptions.Parse(_options.ConnectionString);
				configuration.AbortOnConnectFail = false;
				configuration.BacklogPolicy = BacklogPolicy.FailFast;

				redis = await ConnectionMultiplexer.ConnectAsync(configuration).ConfigureAwait(false);
			}
			catch (Exception)
			{
				redis = null;
			}

			lock (_gate)
			{
				_connecting = null;

				if (redis == null)
				{
					Interlocked.Exchange(ref _retryConnectAfterTicks, DateTimeOffset.UtcNow.Add(ReconnectBackoff).UtcTicks);
					return;
				}

				if (!_disposed)
				{
					// Kept even when this first attempt did not get through: the multiplexer retries on its own, and the cache is
					// skipped until it is connected.
					Volatile.Write(ref _db, redis.GetDatabase());
					Volatile.Write(ref _redis, redis);
					return;
				}
			}

			// Disposed while connecting: DisposeAsync did not see this connection, so it is closed here.
			await CloseAsync(redis).ConfigureAwait(false);
		}

		public async ValueTask DisposeAsync()
		{
			ConnectionMultiplexer redis;
			lock (_gate)
			{
				// A connect still running sees this once it finishes and closes its own connection.
				_disposed = true;
				redis = _redis;
				Volatile.Write(ref _redis, null);
				Volatile.Write(ref _db, null);
			}

			if (redis != null)
				await CloseAsync(redis).ConfigureAwait(false);
		}

		private static async Task CloseAsync(ConnectionMultiplexer redis)
		{
			try
			{
				await redis.CloseAsync().ConfigureAwait(false);
			}
			catch (Exception)
			{
				// Closing a connection that never came up must not fail the relay's shutdown.
			}

			redis.Dispose();
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
				var db = GetDatabase();
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
				var db = GetDatabase();
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
