#region Related components
using System;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Configuration;
using System.Collections.Generic;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CacheUtils;
#endregion

#if !SIGN
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("VIEApps.Components.XUnitTests")]
#endif

namespace net.vieapps.Components.Caching
{
	/// <summary>
	/// Manipulates objects in isolated regions with distributed cache servers (support Redis &amp; Memcached)
	/// </summary>
	[DebuggerDisplay("{Name} ({ExpirationTime} minutes)")]
	public sealed class Cache : IDistributedCache, ICache
	{
		internal readonly ICache _distributedCache;
		internal readonly MemoryCache _L1Cache;

		/// <summary>
		/// Create a new instance of distributed cache with isolated region
		/// </summary>
		/// <param name="name">The string that presents name of isolated region</param>
		/// <param name="loggerFactory">The logger factory for working with logs</param>
		/// <param name="useL1Cache">true to use L-1 Cache (in-process memory)</param>
		public Cache(string name = null, ILoggerFactory loggerFactory = null, bool useL1Cache = false)
			: this(name, Cache.Configuration, loggerFactory, useL1Cache) { }

		/// <summary>
		/// Create a new instance of distributed cache with isolated region
		/// </summary>
		/// <param name="name">The string that presents name of isolated region</param>
		/// <param name="configuration">The cache configuration</param>
		/// <param name="loggerFactory">The logger factory for working with logs</param>
		/// <param name="useL1Cache">true to use L-1 Cache (in-process memory)</param>
		public Cache(string name, ICacheConfiguration configuration, ILoggerFactory loggerFactory, bool useL1Cache = false)
			: this(name ?? configuration?.RegionName, configuration != null ? configuration.ExpirationTime : 25, configuration?.Provider, useL1Cache || (configuration != null && configuration.UseL1Cache), configuration?.ModeL1CacheExpires, configuration != null && configuration.UseL1Cache && configuration.PrefetchL1Cache, configuration != null ? configuration.PrefetchL1CacheDelay : 0, loggerFactory) { }

		/// <summary>
		/// Create a new instance of distributed cache with isolated region
		/// </summary>
		/// <param name="name">The string that presents name of isolated region</param>
		/// <param name="expirationTime">Time for caching an item (in minutes)</param>
		/// <param name="provider">The string that presents the caching provider ('Redis' or 'Memcached') - the default provider is 'Redis'</param>
		/// <param name="useL1Cache">true to use L-1 Cache (in-process memory)</param>
		/// <param name="storeKeys">true to active store all keys of the region (to clear or use with other purposes further)</param>
		public Cache(string name, int expirationTime, string provider, bool useL1Cache, string modeL1CacheExpires, bool prefetchL1Cache, int prefetchL1CacheDelay, ILoggerFactory loggerFactory = null, bool storeKeys = false)
		{
			this._distributedCache = (string.IsNullOrWhiteSpace(provider) ? "Redis" : provider).Trim().ToLower().Equals("memcached")
				? new Memcached(name, expirationTime, storeKeys)
				: new Redis(name, expirationTime, storeKeys) as ICache;

			this.UseL1Cache = useL1Cache;
			this.ModeL1CacheExpires = modeL1CacheExpires;
			this.PrefetchL1Cache = prefetchL1Cache;
			this.PrefetchL1CacheDelay = prefetchL1CacheDelay > 0 ? prefetchL1CacheDelay : 2345;

			if (useL1Cache)
				this._L1Cache = new MemoryCache(key => this.SendL1CacheRequest?.Invoke(key, "update"), key => this.SendL1CacheRequest?.Invoke(key, "remove"));

			(loggerFactory ?? Enyim.Caching.Logger.GetLoggerFactory()).CreateLogger<Cache>().LogInformation($"A new instance of caching was created [{this.Provider}: {this.Name} ({this.ExpirationTime} minutes) - L1-Cache: {this.UseL1Cache}/{this.PrefetchL1Cache}]");
		}

		public void Dispose()
		{
			GC.SuppressFinalize(this);
			this._distributedCache.Dispose();
			this._L1Cache?.Dispose();
		}

		~Cache()
			=> this.Dispose();

		#region Singleton
		internal static Cache _Instance { get; set; }

		static ICacheConfiguration _Configuration { get; set; }

		/// <summary>
		/// Gets the global settings of the caching component
		/// </summary>
		public static ICacheConfiguration Configuration => Cache._Configuration ?? (Cache._Configuration = new CacheConfiguration(ConfigurationManager.GetSection("net.vieapps.cache") is CacheConfigurationSectionHandler config ? config : ConfigurationManager.GetSection("cache") as CacheConfigurationSectionHandler));

		/// <summary>
		/// Creates new an instance of caching component
		/// </summary>
		/// <param name="name">The string that presents name of isolated region</param>
		/// <param name="configuration">The caching configuration</param>
		/// <param name="loggerFactory">The logger factory</param>
		/// <param name="useL1Cache">true to use L-1 Cache (in-process memory)</param>
		/// <returns></returns>
		public static Cache CreateInstance(string name, ICacheConfiguration configuration, ILoggerFactory loggerFactory = null, bool useL1Cache = false)
		{
			loggerFactory = loggerFactory ?? Enyim.Caching.Logger.GetLoggerFactory();
			if (configuration != null && configuration.Servers != null)
			{
				if (configuration.Servers.Where(server => server.Type.ToLower().Equals("redis")).Any())
					Redis.GetClient(configuration.GetRedisConfiguration(), loggerFactory);
				if (configuration.Servers.Where(server => server.Type.ToLower().Equals("memcached")).Any())
					Memcached.GetClient(configuration.GetMemcachedConfiguration(loggerFactory), loggerFactory);
			}
			return new Cache(name, configuration, loggerFactory, useL1Cache);
		}

		/// <summary>
		/// Creates new an instance of caching component
		/// </summary>
		/// <param name="name">The string that presents name of isolated region</param>
		/// <param name="loggerFactory">The logger factory</param>
		/// <param name="useL1Cache">true to use L-1 Cache (in-process memory)</param>
		/// <returns></returns>
		public static Cache CreateInstance(string name, ILoggerFactory loggerFactory = null, bool useL1Cache = false)
			=> Cache.CreateInstance(name, Cache.Configuration, loggerFactory, useL1Cache);

		/// <summary>
		/// Gets the singleton instance of caching component
		/// </summary>
		/// <param name="configuration">The caching configuration</param>
		/// <param name="loggerFactory">The logger factory</param>
		/// <param name="useL1Cache">true to use L-1 Cache (in-process memory)</param>
		/// <returns></returns>
		public static Cache GetInstance(ICacheConfiguration configuration, ILoggerFactory loggerFactory = null, bool useL1Cache = false)
		{
			if (Cache._Instance == null)
			{
				Cache._Configuration = configuration;
				Cache._Instance = Cache.CreateInstance(null, loggerFactory, useL1Cache);
			}
			return Cache._Instance;
		}

		/// <summary>
		/// Gets the singleton instance of caching component
		/// </summary>
		/// <param name="configurationSection"></param>
		/// <param name="loggerFactory"></param>
		/// <returns></returns>
		public static Cache GetInstance(CacheConfigurationSectionHandler configurationSection, ILoggerFactory loggerFactory = null)
			=> Cache.GetInstance(new CacheConfiguration(configurationSection), loggerFactory);

		/// <summary>
		/// Gets the singleton instance of caching componentGets the singleton instance of caching component
		/// </summary>
		/// <param name="svcProvider"></param>
		/// <returns></returns>
		public static Cache GetInstance(IServiceProvider svcProvider)
			=> Cache.GetInstance(svcProvider.GetService<ICacheConfiguration>(), svcProvider.GetService<ILoggerFactory>());
		#endregion

		#region Properties
		/// <summary>
		/// Gets the name of the isolated region
		/// </summary>
		public string Name => this._distributedCache.Name;

		/// <summary>
		/// Gets the name of the cache provider
		/// </summary>
		public string Provider => this._distributedCache.GetType().ToString().Split('.').Last();

		/// <summary>
		/// Gets the expiration time (in minutes)
		/// </summary>
		public int ExpirationTime => this._distributedCache.ExpirationTime;

		/// <summary>
		/// Gets the collection of keys
		/// </summary>
		public HashSet<string> Keys => this._distributedCache.Keys;
		#endregion

		#region L1-Cache
		/// <summary>
		/// Gets or Sets state to use MemoryCache as L1-Cache
		/// </summary>
		public bool UseL1Cache { get; set; } = false;

		/// <summary>
		/// Gets or Sets expires mode of L1-Cache items
		/// </summary>
		public string ModeL1CacheExpires { get; set; } = "auto";

		/// <summary>
		/// Gets or Sets state to pre-fetch L1-Cache item
		/// </summary>
		public bool PrefetchL1Cache { get; set; } = false;

		/// <summary>
		/// Gets or Sets delaying times (miliseconds) before pre-fetching L1-Cache item
		/// </summary>
		public int PrefetchL1CacheDelay { get; set; } = 0;

		/// <summary>
		/// Sends the request for invalidating a L1-Cache itemm
		/// </summary>
		public Action<string, string> SendL1CacheRequest { get; set; }

		/// <summary>
		/// Does the action to process the request for invalidating a L1-Cache item
		/// </summary>
		public async Task ProcessL1CacheRequestAsync(string key, string reason = null)
		{
			if (!this.UseL1Cache || string.IsNullOrWhiteSpace(key))
				return;

			if ("clear" == key.ToLower())
				this._L1Cache.Clear();

			else
			{
				this._L1Cache.Remove(key, false);
				if (this.PrefetchL1Cache && "remove" != reason)
				{
					if (this.PrefetchL1CacheDelay > 0)
						await Task.Delay(this.PrefetchL1CacheDelay).ConfigureAwait(false);
					if (!this._L1Cache.Exists(key))
						this._L1Cache.Set(key, await this._distributedCache.GetAsync(key).ConfigureAwait(false), this.GetExpiresAt(), false);
				}
			}
		}

		bool SendL1CacheRequests(IEnumerable<string> keys, string keyPrefix = null, string reason = "update")
		{
			keys?.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => (string.IsNullOrWhiteSpace(keyPrefix) ? "" : keyPrefix) + key).ToList().ForEach(key => this.SendL1CacheRequest?.Invoke(key, reason));
			return keys != null && keys.Any();
		}

		bool SendL1CacheRequests(string key, string reason = "update")
			=> this.SendL1CacheRequests(new[] { key }, null, reason);

		DateTime GetExpiresAt(DateTime expiresAt)
		{
			var minutes = (expiresAt - DateTime.Now).TotalMinutes;
			return DateTime.Now.AddMinutes("original" == this.ModeL1CacheExpires?.ToLower() ? minutes - 2 : minutes > 2 && minutes < 13 ? minutes / 2 : 3);
		}

		DateTime GetExpiresAt(TimeSpan validFor)
			=> this.GetExpiresAt(DateTime.Now.AddSeconds(validFor.TotalSeconds));

		DateTime GetExpiresAt(int expirationTime = 0)
			=> this.GetExpiresAt(DateTime.Now.AddMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime));

		/// <summary>
		/// Gets the collection of L1-Cache keys
		/// </summary>
		public HashSet<string> GetL1CacheKeys()
			=> new HashSet<string>(this._L1Cache?._items.Keys ?? Array.Empty<string>());

		/// <summary>
		/// Clears L1-Cache
		/// </summary>
		public void ClearL1Cache()
		{
			this._L1Cache?.Clear();
			this.SendL1CacheRequests("clear", "remove");
		}
		#endregion

		#region Keys
		/// <summary>
		/// Gets the collection of keys
		/// </summary>
		public HashSet<string> GetKeys()
			=> this._distributedCache.GetKeys();

		/// <summary>
		/// Gets the collection of keys
		/// </summary>
		public Task<HashSet<string>> GetKeysAsync(CancellationToken cancellationToken = default)
			=> this._distributedCache.GetKeysAsync(cancellationToken);
		#endregion

		#region Set
		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Set(string key, object value, int expirationTime = 0)
			=> this._distributedCache.Set(key, value, expirationTime) && (this.UseL1Cache ? this._L1Cache.Set(key, value, this.GetExpiresAt(expirationTime)) : this.SendL1CacheRequests(key));

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Set(string key, object value, TimeSpan validFor)
			=> this._distributedCache.Set(key, value, validFor) && (this.UseL1Cache ? this._L1Cache.Set(key, value, this.GetExpiresAt(validFor)) : this.SendL1CacheRequests(key));

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Set(string key, object value, DateTime expiresAt)
			=> this._distributedCache.Set(key, value, expiresAt) && (this.UseL1Cache ? this._L1Cache.Set(key, value, this.GetExpiresAt(expiresAt)) : this.SendL1CacheRequests(key));

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetAsync(string key, object value, int expirationTime = 0, CancellationToken cancellationToken = default)
			=> this._distributedCache.SetAsync(key, value, expirationTime, cancellationToken)
				.ContinueWith(task => task.Result && (this.UseL1Cache ? this._L1Cache.Set(key, value, this.GetExpiresAt(expirationTime)) : this.SendL1CacheRequests(key)), TaskContinuationOptions.OnlyOnRanToCompletion);

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetAsync(string key, object value, CancellationToken cancellationToken)
			=> this.SetAsync(key, value, 0, cancellationToken);

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetAsync(string key, object value, TimeSpan validFor, CancellationToken cancellationToken = default)
			=> this._distributedCache.SetAsync(key, value, validFor, cancellationToken)
				.ContinueWith(task => task.Result && (this.UseL1Cache ? this._L1Cache.Set(key, value, this.GetExpiresAt(validFor)) : this.SendL1CacheRequests(key)), TaskContinuationOptions.OnlyOnRanToCompletion);

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetAsync(string key, object value, DateTime expiresAt, CancellationToken cancellationToken = default)
			=> this._distributedCache.SetAsync(key, value, expiresAt, cancellationToken)
				.ContinueWith(task => task.Result && (this.UseL1Cache ? this._L1Cache.Set(key, value, this.GetExpiresAt(expiresAt)) : this.SendL1CacheRequests(key)), TaskContinuationOptions.OnlyOnRanToCompletion);
		#endregion

		#region Set (Multiple)
		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		public void Set(IDictionary<string, object> items, string keyPrefix, DateTime? expiresAt)
		{
			this._distributedCache.Set(items, keyPrefix, expiresAt != null ? (int)expiresAt.Value.ToTimeSpan().TotalMinutes : this.ExpirationTime);
			if (this.UseL1Cache)
				this._L1Cache.Set(items, keyPrefix, expiresAt != null ? this.GetExpiresAt(expiresAt.Value) : this.GetExpiresAt(this.ExpirationTime));
			else
				this.SendL1CacheRequests(items?.Select(kvp => kvp.Key), keyPrefix);
		}

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		public void Set(IDictionary<string, object> items, string keyPrefix, TimeSpan validFor)
		{
			this._distributedCache.Set(items, keyPrefix, validFor != TimeSpan.Zero ? (int)validFor.TotalMinutes : this.ExpirationTime);
			if (this.UseL1Cache)
				this._L1Cache.Set(items, keyPrefix, this.GetExpiresAt(validFor != TimeSpan.Zero ? (int)validFor.TotalMinutes : this.ExpirationTime));
			else
				this.SendL1CacheRequests(items?.Select(kvp => kvp.Key), keyPrefix);
		}

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		public void Set(IDictionary<string, object> items, string keyPrefix, int expirationTime)
			=> this.Set(items, keyPrefix, DateTime.Now.AddMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime));

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <typeparam name="T">The type for casting the cached item</typeparam>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		public void Set<T>(IDictionary<string, T> items, string keyPrefix, DateTime? expiresAt)
		{
			this._distributedCache.Set(items, keyPrefix, expiresAt != null ? (int)expiresAt.Value.ToTimeSpan().TotalMinutes : this.ExpirationTime);
			if (this.UseL1Cache)
				this._L1Cache.Set(items, keyPrefix, expiresAt != null ? this.GetExpiresAt(expiresAt.Value) : this.GetExpiresAt(this.ExpirationTime));
			else
				this.SendL1CacheRequests(items?.Select(kvp => kvp.Key), keyPrefix);
		}

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <typeparam name="T">The type for casting the cached item</typeparam>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		public void Set<T>(IDictionary<string, T> items, string keyPrefix, TimeSpan validFor)
		{
			this._distributedCache.Set(items, keyPrefix, validFor != TimeSpan.Zero ? (int)validFor.TotalMinutes : this.ExpirationTime);
			if (this.UseL1Cache)
				this._L1Cache.Set(items, keyPrefix, this.GetExpiresAt(validFor != TimeSpan.Zero ? (int)validFor.TotalMinutes : this.ExpirationTime));
			else
				this.SendL1CacheRequests(items?.Select(kvp => kvp.Key), keyPrefix);
		}

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <typeparam name="T">The type for casting the cached item</typeparam>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		public void Set<T>(IDictionary<string, T> items, string keyPrefix, int expirationTime)
			=> this.Set(items, keyPrefix, DateTime.Now.AddMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime));

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <param name="cancellationToken">The cancellation token</param>
		public Task SetAsync(IDictionary<string, object> items, string keyPrefix, DateTime? expiresAt, CancellationToken cancellationToken = default)
			=> this._distributedCache.SetAsync(items, keyPrefix, expiresAt != null ? (int)expiresAt.Value.ToTimeSpan().TotalMinutes : this.ExpirationTime, cancellationToken)
				.ContinueWith(_ =>
				{
					if (this.UseL1Cache)
						this._L1Cache.Set(items, keyPrefix, expiresAt != null ? this.GetExpiresAt(expiresAt.Value) : this.GetExpiresAt(this.ExpirationTime));
					else
						this.SendL1CacheRequests(items?.Select(kvp => kvp.Key), keyPrefix);
				}, TaskContinuationOptions.OnlyOnRanToCompletion);

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <param name="cancellationToken">The cancellation token</param>
		public Task SetAsync(IDictionary<string, object> items, string keyPrefix, TimeSpan validFor, CancellationToken cancellationToken = default)
			=> this._distributedCache.SetAsync(items, keyPrefix, validFor != TimeSpan.Zero ? (int)validFor.TotalMinutes : this.ExpirationTime, cancellationToken)
				.ContinueWith(_ =>
				{
					if (this.UseL1Cache)
						this._L1Cache.Set(items, keyPrefix, this.GetExpiresAt(validFor != TimeSpan.Zero ? (int)validFor.TotalMinutes : this.ExpirationTime));
					else
						this.SendL1CacheRequests(items?.Select(kvp => kvp.Key), keyPrefix);
				}, TaskContinuationOptions.OnlyOnRanToCompletion);

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		public Task SetAsync(IDictionary<string, object> items, string keyPrefix, int expirationTime, CancellationToken cancellationToken = default)
			=> this.SetAsync(items, keyPrefix, DateTime.Now.AddMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime), cancellationToken);

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <typeparam name="T">The type for casting the cached item</typeparam>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <param name="cancellationToken">The cancellation token</param>
		public Task SetAsync<T>(IDictionary<string, T> items, string keyPrefix, DateTime? expiresAt, CancellationToken cancellationToken = default)
			=> this._distributedCache.SetAsync(items, keyPrefix, expiresAt != null ? (int)expiresAt.Value.ToTimeSpan().TotalMinutes : this.ExpirationTime, cancellationToken)
				.ContinueWith(_ =>
				{
					if (this.UseL1Cache)
						this._L1Cache.Set(items, keyPrefix, expiresAt != null ? this.GetExpiresAt(expiresAt.Value) : this.GetExpiresAt(this.ExpirationTime));
					else
						this.SendL1CacheRequests(items?.Select(kvp => kvp.Key), keyPrefix);
				}, TaskContinuationOptions.OnlyOnRanToCompletion);

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <typeparam name="T">The type for casting the cached item</typeparam>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <param name="cancellationToken">The cancellation token</param>
		public Task SetAsync<T>(IDictionary<string, T> items, string keyPrefix, TimeSpan validFor, CancellationToken cancellationToken = default)
			=> this._distributedCache.SetAsync(items, keyPrefix, validFor != TimeSpan.Zero ? (int)validFor.TotalMinutes : this.ExpirationTime, cancellationToken)
				.ContinueWith(_ =>
				{
					if (this.UseL1Cache)
						this._L1Cache.Set(items, keyPrefix, this.GetExpiresAt(validFor != TimeSpan.Zero ? (int)validFor.TotalMinutes : this.ExpirationTime));
					else
						this.SendL1CacheRequests(items?.Select(kvp => kvp.Key), keyPrefix);
				}, TaskContinuationOptions.OnlyOnRanToCompletion);

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <typeparam name="T">The type for casting the cached item</typeparam>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		public Task SetAsync<T>(IDictionary<string, T> items, string keyPrefix, int expirationTime, CancellationToken cancellationToken = default)
			=> this.SetAsync(items, keyPrefix, DateTime.Now.AddMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime), cancellationToken);
		#endregion

		#region Set (Fragment)
		/// <summary>
		/// Adds an item (as fragments) into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="fragments">The collection that contains all fragments (object that serialized as binary - array bytes)</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool SetFragments(string key, List<byte[]> fragments, int expirationTime = 0)
			=> this._distributedCache.SetFragments(key, fragments, expirationTime);

		/// <summary>
		/// Adds an item (as fragments) into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="fragments">The collection that contains all fragments (object that serialized as binary - array bytes)</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetFragmentsAsync(string key, List<byte[]> fragments, int expirationTime = 0, CancellationToken cancellationToken = default)
			=> this._distributedCache.SetFragmentsAsync(key, fragments, expirationTime, cancellationToken);

		/// <summary>
		/// Adds an item (as fragments) into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="fragments">The collection that contains all fragments (object that serialized as binary - array bytes)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetFragmentsAsync(string key, List<byte[]> fragments, CancellationToken cancellationToken)
			=> this.SetFragmentsAsync(key, fragments, 0, cancellationToken);

		/// <summary>
		/// Serializes object into array of bytes, splits into one or more fragments and updates into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool SetAsFragments(string key, object value, int expirationTime = 0)
			=> this._distributedCache.SetAsFragments(key, value, expirationTime) && (this.UseL1Cache ? this._L1Cache.Set(key, value, this.GetExpiresAt(expirationTime)) : this.SendL1CacheRequests(key));

		/// <summary>
		/// Serializes object into array of bytes, splits into one or more fragments and updates into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetAsFragmentsAsync(string key, object value, int expirationTime = 0, CancellationToken cancellationToken = default)
			=> this._distributedCache.SetAsFragmentsAsync(key, value, expirationTime, cancellationToken)
				.ContinueWith(task => task.Result && (this.UseL1Cache ? this._L1Cache.Set(key, value, this.GetExpiresAt(expirationTime)) : this.SendL1CacheRequests(key)), TaskContinuationOptions.OnlyOnRanToCompletion);

		/// <summary>
		/// Serializes object into array of bytes, splits into one or more fragments and updates into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetAsFragmentsAsync(string key, object value, CancellationToken cancellationToken)
			=> this.SetAsFragmentsAsync(key, value, 0, cancellationToken);
		#endregion

		#region Add
		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Add(string key, object value, int expirationTime = 0)
			=> this._distributedCache.Add(key, value, expirationTime) && (this.UseL1Cache ? this._L1Cache.Set(key, value, this.GetExpiresAt(expirationTime)) : this.SendL1CacheRequests(key));

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Add(string key, object value, TimeSpan validFor)
			=> this._distributedCache.Add(key, value, validFor) && (this.UseL1Cache ? this._L1Cache.Set(key, value, this.GetExpiresAt(validFor)) : this.SendL1CacheRequests(key));

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Add(string key, object value, DateTime expiresAt)
			=> this._distributedCache.Add(key, value, expiresAt) && (this.UseL1Cache ? this._L1Cache.Set(key, value, this.GetExpiresAt(expiresAt)) : this.SendL1CacheRequests(key));

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> AddAsync(string key, object value, int expirationTime = 0, CancellationToken cancellationToken = default)
			=> this._distributedCache.AddAsync(key, value, expirationTime, cancellationToken)
				.ContinueWith(task => task.Result && (this.UseL1Cache ? this._L1Cache.Set(key, value, this.GetExpiresAt(expirationTime)) : this.SendL1CacheRequests(key)), TaskContinuationOptions.OnlyOnRanToCompletion);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> AddAsync(string key, object value, CancellationToken cancellationToken)
			=> this.AddAsync(key, value, 0, cancellationToken);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> AddAsync(string key, object value, TimeSpan validFor, CancellationToken cancellationToken = default)
			=> this._distributedCache.AddAsync(key, value, validFor, cancellationToken)
				.ContinueWith(task => task.Result && (this.UseL1Cache ? this._L1Cache.Set(key, value, this.GetExpiresAt(validFor)) : this.SendL1CacheRequests(key)), TaskContinuationOptions.OnlyOnRanToCompletion);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> AddAsync(string key, object value, DateTime expiresAt, CancellationToken cancellationToken = default)
			=> this._distributedCache.AddAsync(key, value, expiresAt, cancellationToken)
				.ContinueWith(task => task.Result && (this.UseL1Cache ? this._L1Cache.Set(key, value, this.GetExpiresAt(expiresAt)) : this.SendL1CacheRequests(key)), TaskContinuationOptions.OnlyOnRanToCompletion);
		#endregion

		#region Replace
		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Replace(string key, object value, int expirationTime = 0)
			=> this._distributedCache.Replace(key, value, expirationTime) && (this.UseL1Cache ? this._L1Cache.Set(key, value, this.GetExpiresAt(expirationTime)) : this.SendL1CacheRequests(key));

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Replace(string key, object value, TimeSpan validFor)
			=> this._distributedCache.Replace(key, value, validFor) && (this.UseL1Cache ? this._L1Cache.Set(key, value, this.GetExpiresAt(validFor)) : this.SendL1CacheRequests(key));

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Replace(string key, object value, DateTime expiresAt)
			=> this._distributedCache.Replace(key, value, expiresAt) && (this.UseL1Cache ? this._L1Cache.Set(key, value, this.GetExpiresAt(expiresAt)) : this.SendL1CacheRequests(key));

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> ReplaceAsync(string key, object value, int expirationTime = 0, CancellationToken cancellationToken = default)
			=> this._distributedCache.ReplaceAsync(key, value, expirationTime, cancellationToken)
				.ContinueWith(task => task.Result && (this.UseL1Cache ? this._L1Cache.Set(key, value, this.GetExpiresAt(expirationTime)) : this.SendL1CacheRequests(key)), TaskContinuationOptions.OnlyOnRanToCompletion);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> ReplaceAsync(string key, object value, CancellationToken cancellationToken)
			=> this.ReplaceAsync(key, value, 0, cancellationToken);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> ReplaceAsync(string key, object value, TimeSpan validFor, CancellationToken cancellationToken = default)
			=> this._distributedCache.ReplaceAsync(key, value, validFor, cancellationToken)
				.ContinueWith(task => task.Result && (this.UseL1Cache ? this._L1Cache.Set(key, value, this.GetExpiresAt(validFor)) : this.SendL1CacheRequests(key)), TaskContinuationOptions.OnlyOnRanToCompletion);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> ReplaceAsync(string key, object value, DateTime expiresAt, CancellationToken cancellationToken = default)
			=> this._distributedCache.ReplaceAsync(key, value, expiresAt, cancellationToken)
				.ContinueWith(task => task.Result && (this.UseL1Cache ? this._L1Cache.Set(key, value, this.GetExpiresAt(expiresAt)) : this.SendL1CacheRequests(key)), TaskContinuationOptions.OnlyOnRanToCompletion);
		#endregion

		#region Refresh
		/// <summary>
		/// Refreshs an existed item
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <returns>Returns a boolean value indicating if the item is refreshed or not</returns>
		public bool Refresh(string key)
			=> this._distributedCache.Refresh(key) && (!this.UseL1Cache || this._L1Cache.Remove(key));

		/// <summary>
		/// Refreshs an existed item
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <returns>Returns a boolean value indicating if the item is refreshed or not</returns>
		public Task<bool> RefreshAsync(string key, CancellationToken cancellationToken = default)
			=> this._distributedCache.RefreshAsync(key, cancellationToken)
				.ContinueWith(task => task.Result && (!this.UseL1Cache || this._L1Cache.Remove(key)), TaskContinuationOptions.OnlyOnRanToCompletion);
		#endregion

		#region Get
		/// <summary>
		/// Retreives a cached item
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to retreive</param>
		/// <returns>The retrieved cache item, or a null reference if the key is not found</returns>
		public object Get(string key)
		{
			var value = this.UseL1Cache
				? this._L1Cache.Get(key)
				: this._distributedCache.Get(key);
			if (value == null && this.UseL1Cache)
				this._L1Cache.Set(key, value = this._distributedCache.Get(key), this.GetExpiresAt(), false);
			return value;
		}

		/// <summary>
		/// Retreives a cached item
		/// </summary>
		/// <typeparam name="T">The type for casting the cached item</typeparam>
		/// <param name="key">The string that presents key of cached item need to retreive</param>
		/// <returns>The retrieved cache item, or a null reference if the key is not found</returns>
		public T Get<T>(string key)
		{
			var value = this.UseL1Cache
				? this._L1Cache.Get<T>(key)
				: this._distributedCache.Get<T>(key);
			if (value == null && this.UseL1Cache)
				this._L1Cache.Set(key, value = this._distributedCache.Get<T>(key), this.GetExpiresAt(), false);
			return value;
		}

		/// <summary>
		/// Retreives a cached item
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to retreive</param>
		/// <returns>The retrieved cache item, or a null reference if the key is not found</returns>
		public async Task<object> GetAsync(string key, CancellationToken cancellationToken = default)
		{
			var value = this.UseL1Cache
				? this._L1Cache.Get(key)
				: await this._distributedCache.GetAsync(key, cancellationToken).ConfigureAwait(false);
			if (value == null && this.UseL1Cache)
				this._L1Cache.Set(key, value = await this._distributedCache.GetAsync(key, cancellationToken).ConfigureAwait(false), this.GetExpiresAt(), false);
			return value;
		}

		/// <summary>
		/// Retreives a cached item
		/// </summary>
		/// <typeparam name="T">The type for casting the cached item</typeparam>
		/// <param name="key">The string that presents key of cached item need to retreive</param>
		/// <returns>The retrieved cache item, or a null reference if the key is not found</returns>
		public async Task<T> GetAsync<T>(string key, CancellationToken cancellationToken = default)
		{
			var value = this.UseL1Cache
				? this._L1Cache.Get<T>(key)
				: await this._distributedCache.GetAsync<T>(key, cancellationToken).ConfigureAwait(false);
			if (value == null && this.UseL1Cache)
				this._L1Cache.Set(key, value = await this._distributedCache.GetAsync<T>(key, cancellationToken).ConfigureAwait(false), this.GetExpiresAt(), false);
			return value;
		}
		#endregion

		#region Get (Multiple)
		/// <summary>
		/// Retreives a collection of cached items
		/// </summary>
		/// <param name="keys">The collection of items' keys</param>
		/// <returns>The collection of cache items</returns>
		public IDictionary<string, object> Get(IEnumerable<string> keys)
		{
			var value = this.UseL1Cache
				? this._L1Cache.Get(keys)
				: keys == null ? null : this._distributedCache.Get(keys);
			if ((value == null || value.Count != keys.Count()) && this.UseL1Cache)
				this._L1Cache.Set(value = keys == null ? null : this._distributedCache.Get(keys), null, this.GetExpiresAt(), false);
			return value;
		}

		/// <summary>
		/// Retreives a collection of cached items
		/// </summary>
		/// <param name="keys">The collection of items' keys</param>
		/// <returns>The collection of cache items</returns>
		public IDictionary<string, T> Get<T>(IEnumerable<string> keys)
		{
			var value = this.UseL1Cache
				? this._L1Cache.Get<T>(keys)
				: keys == null ? null : this._distributedCache.Get<T>(keys);
			if ((value == null || value.Count != keys.Count()) && this.UseL1Cache)
				this._L1Cache.Set(value = keys == null ? null : this._distributedCache.Get<T>(keys), null, this.GetExpiresAt(), false);
			return value;
		}

		/// <summary>
		/// Retreives a collection of cached items
		/// </summary>
		/// <param name="keys">The collection of items' keys</param>
		/// <returns>The collection of cache items</returns>
		public async Task<IDictionary<string, object>> GetAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
		{
			var value = this.UseL1Cache
				? this._L1Cache.Get(keys)
				: keys == null ? null : await this._distributedCache.GetAsync(keys, cancellationToken).ConfigureAwait(false);
			if ((value == null || value.Count != keys.Count()) && this.UseL1Cache)
				this._L1Cache.Set(value = keys == null ? null : await this._distributedCache.GetAsync(keys, cancellationToken).ConfigureAwait(false), null, this.GetExpiresAt(), false);
			return value;
		}

		/// <summary>
		/// Retreives a collection of cached items
		/// </summary>
		/// <param name="keys">The collection of items' keys</param>
		/// <returns>The collection of cache items</returns>
		public async Task<IDictionary<string, T>> GetAsync<T>(IEnumerable<string> keys, CancellationToken cancellationToken = default)
		{
			var value = this.UseL1Cache
				? this._L1Cache.Get<T>(keys)
				: keys == null ? null : await this._distributedCache.GetAsync<T>(keys, cancellationToken).ConfigureAwait(false);
			if ((value == null || value.Count != keys.Count()) && this.UseL1Cache)
				this._L1Cache.Set(value = keys == null ? null : await this._distributedCache.GetAsync<T>(keys, cancellationToken).ConfigureAwait(false), null, this.GetExpiresAt(), false);
			return value;
		}
		#endregion

		#region Get (Fragment)
		/// <summary>
		/// Gets fragment information that associates with the key
		/// </summary>
		/// <param name="key">The string that presents key of fragment information</param>
		/// <returns>The information of fragments, first element is total number of fragments, second element is total length of data</returns>
		public (int Blocks, int Length) GetFragments(string key)
			=> this._distributedCache.GetFragments(key);

		/// <summary>
		/// Gets fragment information that associates with the key
		/// </summary>
		/// <param name="key">The string that presents key of fragment information</param>
		/// <returns>The information of fragments, first element is total number of fragments, second element is total length of data</returns>
		public Task<(int Blocks, int Length)> GetFragmentsAsync(string key, CancellationToken cancellationToken = default)
			=> this._distributedCache.GetFragmentsAsync(key, cancellationToken);

		/// <summary>
		/// Gets cached of fragmented items that associates with the key and indexes
		/// </summary>
		/// <param name="key">The string that presents key of all fragmented items</param>
		/// <param name="indexes">The collection that presents indexes of all fragmented items need to get</param>
		/// <returns>The collection of array of bytes that presents serialized information of fragmented items</returns>
		public List<byte[]> GetAsFragments(string key, List<int> indexes)
			=> this._distributedCache.GetAsFragments(key, indexes);

		/// <summary>
		/// Gets cached of fragmented items that associates with the key and indexes
		/// </summary>
		/// <param name="key">The string that presents key of all fragmented items</param>
		/// <param name="indexes">The collection that presents indexes of all fragmented items need to get</param>
		/// <returns>The collection of array of bytes that presents serialized information of fragmented items</returns>
		public List<byte[]> GetAsFragments(string key, params int[] indexes)
			=> this._distributedCache.GetAsFragments(key, indexes);

		/// <summary>
		/// Gets cached of fragmented items that associates with the key and indexes
		/// </summary>
		/// <param name="key">The string that presents key of all fragmented items</param>
		/// <param name="indexes">The collection that presents indexes of all fragmented items need to get</param>
		/// <returns>The collection of array of bytes that presents serialized information of fragmented items</returns>
		public Task<List<byte[]>> GetAsFragmentsAsync(string key, List<int> indexes, CancellationToken cancellationToken = default)
			=> this._distributedCache.GetAsFragmentsAsync(key, indexes, cancellationToken);

		/// <summary>
		/// Gets cached of fragmented items that associates with the key and indexes
		/// </summary>
		/// <param name="key">The string that presents key of all fragmented items</param>
		/// <param name="indexes">The collection that presents indexes of all fragmented items need to get</param>
		/// <returns>The collection of array of bytes that presents serialized information of fragmented items</returns>
		public Task<List<byte[]>> GetAsFragmentsAsync(string key, CancellationToken cancellationToken = default, params int[] indexes)
			=> this._distributedCache.GetAsFragmentsAsync(key, cancellationToken, indexes);
		#endregion

		#region Remove
		/// <summary>
		/// Removes a cached item
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to remove</param>
		/// <returns>Returns a boolean value indicating if the item is removed or not</returns>
		public bool Remove(string key)
			=> this._distributedCache.Remove(key) && (this.UseL1Cache ? this._L1Cache.Remove(key) : this.SendL1CacheRequests(key, "remove"));

		/// <summary>
		/// Removes a cached item
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to remove</param>
		/// <returns>Returns a boolean value indicating if the item is removed or not</returns>
		public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
			=> this._distributedCache.RemoveAsync(key, cancellationToken)
				.ContinueWith(task => task.Result && (this.UseL1Cache ? this._L1Cache.Remove(key) : this.SendL1CacheRequests(key, "remove")), TaskContinuationOptions.OnlyOnRanToCompletion);
		#endregion

		#region Remove (Multiple)
		/// <summary>
		/// Removes a collection of cached items
		/// </summary>
		/// <param name="keys">The collection that presents key of cached items need to remove</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		public void Remove(IEnumerable<string> keys, string keyPrefix = null)
		{
			this._distributedCache.Remove(keys, keyPrefix);
			if (this.UseL1Cache)
				this._L1Cache.Remove(keys, keyPrefix);
			else
				this.SendL1CacheRequests(keys, keyPrefix, "remove");
		}

		/// <summary>
		/// Removes a collection of cached items
		/// </summary>
		/// <param name="keys">The collection that presents key of cached items need to remove</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		public Task RemoveAsync(IEnumerable<string> keys, string keyPrefix = null, CancellationToken cancellationToken = default)
			=> this._distributedCache.RemoveAsync(keys, keyPrefix, cancellationToken)
				.ContinueWith(_ =>
				{
					if (this.UseL1Cache)
						this._L1Cache.Remove(keys, keyPrefix);
					else
						this.SendL1CacheRequests(keys, keyPrefix, "remove");
				}, TaskContinuationOptions.OnlyOnRanToCompletion);

		/// <summary>
		/// Removes a collection of cached items
		/// </summary>
		/// <param name="keys">The collection that presents key of cached items need to remove</param>
		public Task RemoveAsync(IEnumerable<string> keys, CancellationToken cancellationToken)
			=> this.RemoveAsync(keys, null, cancellationToken);
		#endregion

		#region Remove (Fragment)
		/// <summary>
		/// Removes a cached item (with first 100 fragments) from cache storage
		/// </summary>
		/// <param name="key">The string that presents key of fragmented items need to be removed</param>
		public void RemoveFragments(string key)
		{
			this._distributedCache.RemoveFragments(key);
			if (this.UseL1Cache)
				this._L1Cache.Remove(key);
			else
				this.SendL1CacheRequests(key, "remove");
		}

		/// <summary>
		/// Removes a cached item (with first 100 fragments) from cache storage
		/// </summary>
		/// <param name="key">The string that presents key of fragmented items need to be removed</param>
		public Task RemoveFragmentsAsync(string key, CancellationToken cancellationToken = default)
			=> this._distributedCache.RemoveFragmentsAsync(key, cancellationToken)
				.ContinueWith(_ =>
				{
					if (this.UseL1Cache)
						this._L1Cache.Remove(key);
					else
						this.SendL1CacheRequests(key, "remove");
				}, TaskContinuationOptions.OnlyOnRanToCompletion);
		#endregion

		#region Exists
		/// <summary>
		/// Determines whether an item exists in the cache
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to check</param>
		/// <returns>Returns a boolean value indicating if the object that associates with the key is cached or not</returns>
		public bool Exists(string key)
			=> (this.UseL1Cache && this._L1Cache.Exists(key)) || this._distributedCache.Exists(key);

		/// <summary>
		/// Determines whether an item exists in the cache
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to check</param>
		/// <returns>Returns a boolean value indicating if the object that associates with the key is cached or not</returns>
		public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
			=> (this.UseL1Cache && this._L1Cache.Exists(key)) || await this._distributedCache.ExistsAsync(key, cancellationToken).ConfigureAwait(false);
		#endregion

		#region Working with Set
		/// <summary>
		/// Gets a set
		/// </summary>
		/// <param name="key"></param>
		/// <returns></returns>
		public HashSet<string> GetSetMembers(string key)
		{
			var value = this.UseL1Cache
				? this._L1Cache.Get<HashSet<string>>(key)
				: this._distributedCache.GetSetMembers(key);
			if (value == null && this.UseL1Cache)
				this._L1Cache.Set(key, value = this._distributedCache.GetSetMembers(key), this.GetExpiresAt(), false);
			return value;
		}

		/// <summary>
		/// Gets a set
		/// </summary>
		/// <param name="key"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public async Task<HashSet<string>> GetSetMembersAsync(string key, CancellationToken cancellationToken = default)
		{
			var value = this.UseL1Cache
				? this._L1Cache.Get<HashSet<string>>(key)
				: await this._distributedCache.GetSetMembersAsync(key, cancellationToken).ConfigureAwait(false);
			if (value == null && this.UseL1Cache)
				this._L1Cache.Set(key, value = await this._distributedCache.GetSetMembersAsync(key, cancellationToken).ConfigureAwait(false), this.GetExpiresAt(), false);
			return value;
		}

		/// <summary>
		/// Adds a value into a set
		/// </summary>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <returns></returns>
		public bool AddSetMember(string key, string value)
			=> this._distributedCache.AddSetMember(key, value) && (this.UseL1Cache ? this._L1Cache.Remove(key) : this.SendL1CacheRequests(key, "remove"));

		/// <summary>
		/// Adds the values into a set
		/// </summary>
		/// <param name="key"></param>
		/// <param name="values"></param>
		/// <returns></returns>
		public bool AddSetMembers(string key, IEnumerable<string> values)
			=> this._distributedCache.AddSetMembers(key, values) && (this.UseL1Cache ? this._L1Cache.Remove(key) : this.SendL1CacheRequests(key, "remove"));

		/// <summary>
		/// Adds a value into a set
		/// </summary>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public Task<bool> AddSetMemberAsync(string key, string value, CancellationToken cancellationToken = default)
			=> this._distributedCache.AddSetMemberAsync(key, value, cancellationToken)
				.ContinueWith(task => task.Result && (this.UseL1Cache ? this._L1Cache.Remove(key) : this.SendL1CacheRequests(key, "remove")), TaskContinuationOptions.OnlyOnRanToCompletion);

		/// <summary>
		/// Adds the values into a set
		/// </summary>
		/// <param name="key"></param>
		/// <param name="values"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public Task<bool> AddSetMembersAsync(string key, IEnumerable<string> values, CancellationToken cancellationToken = default)
			=> this._distributedCache.AddSetMembersAsync(key, values, cancellationToken)
				.ContinueWith(task => task.Result && (this.UseL1Cache ? this._L1Cache.Remove(key) : this.SendL1CacheRequests(key, "remove")), TaskContinuationOptions.OnlyOnRanToCompletion);

		/// <summary>
		/// Removes a value from a set
		/// </summary>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <returns></returns>
		public bool RemoveSetMember(string key, string value)
			=> this._distributedCache.RemoveSetMember(key, value) && (this.UseL1Cache ? this._L1Cache.Remove(key) : this.SendL1CacheRequests(key, "remove"));

		/// <summary>
		/// Removes the values from a set
		/// </summary>
		/// <param name="key"></param>
		/// <param name="values"></param>
		/// <returns></returns>
		public bool RemoveSetMembers(string key, IEnumerable<string> values)
			=> this._distributedCache.RemoveSetMembers(key, values) && (this.UseL1Cache ? this._L1Cache.Remove(key) : this.SendL1CacheRequests(key, "remove"));

		/// <summary>
		/// Removes a value from a set
		/// </summary>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public Task<bool> RemoveSetMemberAsync(string key, string value, CancellationToken cancellationToken = default)
			=> this._distributedCache.RemoveSetMemberAsync(key, value, cancellationToken)
				.ContinueWith(task => task.Result && (this.UseL1Cache ? this._L1Cache.Remove(key) : this.SendL1CacheRequests(key, "remove")), TaskContinuationOptions.OnlyOnRanToCompletion);

		/// <summary>
		/// Removes the values from a set
		/// </summary>
		/// <param name="key"></param>
		/// <param name="values"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public Task<bool> RemoveSetMembersAsync(string key, IEnumerable<string> values, CancellationToken cancellationToken = default)
			=> this._distributedCache.RemoveSetMembersAsync(key, values, cancellationToken)
				.ContinueWith(task => task.Result && (this.UseL1Cache ? this._L1Cache.Remove(key) : this.SendL1CacheRequests(key, "remove")), TaskContinuationOptions.OnlyOnRanToCompletion);
		#endregion

		#region Clear
		/// <summary>
		/// Clears the cache storage of this isolated region
		/// </summary>
		public void Clear()
		{
			this._distributedCache.Clear();
			this.ClearL1Cache();
		}

		/// <summary>
		/// Clears the cache storage of this isolated region
		/// </summary>
		public Task ClearAsync(CancellationToken cancellationToken = default)
			=> this._distributedCache.ClearAsync(cancellationToken)
				.ContinueWith(_ => this.ClearL1Cache(), TaskContinuationOptions.OnlyOnRanToCompletion);
		#endregion

		#region Flush
		/// <summary>
		/// Removes all data from the cache
		/// </summary>
		public void FlushAll()
		{
			this._distributedCache.FlushAll();
			this.ClearL1Cache();
		}

		/// <summary>
		/// Removes all data from the cache
		/// </summary>
		public Task FlushAllAsync(CancellationToken cancellationToken = default)
			=> this._distributedCache.FlushAllAsync(cancellationToken)
				.ContinueWith(_ => this.ClearL1Cache(), TaskContinuationOptions.OnlyOnRanToCompletion);
		#endregion

		#region IDistributedCache
		void IDistributedCache.Set(string key, byte[] value, DistributedCacheEntryOptions options)
		{
			if (string.IsNullOrWhiteSpace(key))
				throw new ArgumentNullException(nameof(key));
			var expires = options == null ? TimeSpan.Zero : options.GetExpiration();
			var validFor = expires is TimeSpan timespan
				? timespan
				: CacheUtils.Helper.UnixEpoch.AddSeconds((long)expires).ToTimeSpan();
			if (this._distributedCache.Set(key, value, validFor) && expires is TimeSpan && validFor != TimeSpan.Zero)
				this._distributedCache.Set(key.GetIDistributedCacheExpirationKey(), expires, validFor);
		}

		async Task IDistributedCache.SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken cancellationToken = default)
		{
			if (string.IsNullOrWhiteSpace(key))
				throw new ArgumentNullException(nameof(key));
			var expires = options == null ? TimeSpan.Zero : options.GetExpiration();
			var validFor = expires is TimeSpan timespan
				? timespan
				: CacheUtils.Helper.UnixEpoch.AddSeconds((long)expires).ToTimeSpan();
			if (await this._distributedCache.SetAsync(key, value, validFor, cancellationToken).ConfigureAwait(false) && expires is TimeSpan && validFor != TimeSpan.Zero)
				await this._distributedCache.SetAsync(key.GetIDistributedCacheExpirationKey(), expires, validFor, cancellationToken).ConfigureAwait(false);
		}

		byte[] IDistributedCache.Get(string key)
			=> string.IsNullOrWhiteSpace(key)
				? throw new ArgumentNullException(nameof(key))
				: this._distributedCache.Get<byte[]>(key);

		Task<byte[]> IDistributedCache.GetAsync(string key, CancellationToken cancellationToken = default)
			=> string.IsNullOrWhiteSpace(key)
				? Task.FromException<byte[]>(new ArgumentNullException(nameof(key)))
				: this._distributedCache.GetAsync<byte[]>(key, cancellationToken);

		void IDistributedCache.Refresh(string key)
		{
			if (string.IsNullOrWhiteSpace(key))
				throw new ArgumentNullException(nameof(key));
			var value = this._distributedCache.Get<byte[]>(key);
			var expires = value != null ? this._distributedCache.Get(key.GetIDistributedCacheExpirationKey()) : null;
			if (value != null && expires != null && expires is TimeSpan timespan && this._distributedCache.Replace(key, value, timespan))
				this._distributedCache.Replace(key.GetIDistributedCacheExpirationKey(), expires, timespan);
		}

		async Task IDistributedCache.RefreshAsync(string key, CancellationToken cancellationToken = default)
		{
			if (string.IsNullOrWhiteSpace(key))
				throw new ArgumentNullException(nameof(key));
			var value = await this._distributedCache.GetAsync<byte[]>(key, cancellationToken).ConfigureAwait(false);
			var expires = value != null ? await this._distributedCache.GetAsync(key.GetIDistributedCacheExpirationKey(), cancellationToken).ConfigureAwait(false) : null;
			if (value != null && expires != null && expires is TimeSpan timespan && await this._distributedCache.ReplaceAsync(key, value, timespan, cancellationToken).ConfigureAwait(false))
				await this._distributedCache.ReplaceAsync(key.GetIDistributedCacheExpirationKey(), expires, timespan, cancellationToken).ConfigureAwait(false);
		}

		void IDistributedCache.Remove(string key)
		{
			if (string.IsNullOrWhiteSpace(key))
				throw new ArgumentNullException(nameof(key));
			this._distributedCache.Remove(new[] { key, key.GetIDistributedCacheExpirationKey() }, null);
		}

		Task IDistributedCache.RemoveAsync(string key, CancellationToken cancellationToken = default)
			=> string.IsNullOrWhiteSpace(key)
				? Task.FromException(new ArgumentNullException(nameof(key)))
				: this._distributedCache.RemoveAsync(new[] { key, key.GetIDistributedCacheExpirationKey() }, null, cancellationToken);
		#endregion

	}
}