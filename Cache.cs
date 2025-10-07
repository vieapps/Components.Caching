#region Related components
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
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
		readonly ICache _distributedCache;
		readonly MemoryCache _memoryCache;

		/// <summary>
		/// Create a new instance of distributed cache with isolated region
		/// </summary>
		/// <param name="name">The string that presents name of isolated region</param>
		/// <param name="loggerFactory">The logger factory for working with logs</param>
		/// <param name="onCreated">The action to fire when a new instance was created</param>
		public Cache(string name = null, ILoggerFactory loggerFactory = null, Action<Cache> onCreated = null, string mode = null)
			: this(name, Cache.Configuration, loggerFactory, onCreated, mode) { }

		/// <summary>
		/// Create a new instance of distributed cache with isolated region
		/// </summary>
		/// <param name="name">The string that presents name of isolated region</param>
		/// <param name="configuration">The cache configuration</param>
		/// <param name="loggerFactory">The logger factory for working with logs</param>
		/// <param name="onCreated">The action to fire when a new instance was created</param>
		public Cache(string name, ICacheConfiguration configuration, ILoggerFactory loggerFactory, Action<Cache> onCreated, string mode)
			: this(name ?? configuration?.RegionName, configuration != null ? configuration.ExpirationTime : 25, configuration?.Provider, false, configuration != null && configuration.UseMemoryCacheAsL1Cache, configuration != null && configuration.UseMemoryCacheAsL1Cache && configuration.PrefetchL1Cache, configuration != null ? configuration.PrefetchDelay : 0, loggerFactory, onCreated, mode) { }

		/// <summary>
		/// Create a new instance of distributed cache with isolated region
		/// </summary>
		/// <param name="name">The string that presents name of isolated region</param>
		/// <param name="expirationTime">Time for caching an item (in minutes)</param>
		/// <param name="provider">The string that presents the caching provider ('Redis' or 'Memcached') - the default provider is 'Redis'</param>
		/// <param name="storeKeys">true to active store all keys of the region (to clear or use with other purposes further)</param>
		/// <param name="useMemoryCacheAsL1Cache">true to use MemoryCache as layer-1 cache</param>
		/// <param name="loggerFactory">The logger factory for working with logs</param>
		/// <param name="onCreated">The action to fire when a new instance was created</param>
		public Cache(string name, int expirationTime, string provider, bool storeKeys, bool useMemoryCacheAsL1Cache, bool prefetchL1Cache, int prefetchDelay, ILoggerFactory loggerFactory, Action<Cache> onCreated, string mode)
		{
			loggerFactory = loggerFactory ?? Enyim.Caching.Logger.GetLoggerFactory();
			Enyim.Caching.Logger.AssignLoggerFactory(loggerFactory);
			Helper.Logger = Helper.Logger ?? loggerFactory.CreateLogger<Cache>();

			this._distributedCache = (string.IsNullOrWhiteSpace(provider) ? "Redis" : provider).Trim().ToLower().Equals("memcached")
				? new Memcached(name, expirationTime, storeKeys)
				: new Redis(name, expirationTime, storeKeys) as ICache;

			this.UseMemoryCacheAsL1Cache = useMemoryCacheAsL1Cache;
			this.PrefetchL1Cache = prefetchL1Cache;
			this.PrefetchDelay = prefetchDelay > 0 ? prefetchDelay : 1234;
			this.ProcessCacheItemInvalidatingMessage = async key =>
			{
				if (this.UseMemoryCacheAsL1Cache && !string.IsNullOrWhiteSpace(key))
				{
					if (key == "clear")
						this._Clear(false);
					else
					{
						this._Remove(key, false);
						if (this.PrefetchL1Cache)
						{
							if (this.PrefetchDelay > 0)
								await Task.Delay(this.PrefetchDelay).ConfigureAwait(false);
							if (!this._Exists(key))
								this._Set(key, await this._distributedCache.GetAsync(key).ConfigureAwait(false), 0, false);
						}
					}
				}
			};

			if (useMemoryCacheAsL1Cache)
				this._memoryCache = new MemoryCache();

			onCreated?.Invoke(this);
			Helper.Logger.LogInformation($"A new instance of caching was created {mode ?? "(app.config)"} [{this.Provider}: {this.Name} ({this.ExpirationTime} minutes) :: L1-Cache: {this.UseMemoryCacheAsL1Cache}/{this.PrefetchL1Cache}]");
		}

		public void Dispose()
		{
			GC.SuppressFinalize(this);
			this._distributedCache.Dispose();
			this._memoryCache?.Dispose();
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
		/// <param name="onCreated">The action to fire when a new instance was created</param>
		/// <returns></returns>
		public static Cache CreateInstance(string name, ICacheConfiguration configuration, ILoggerFactory loggerFactory = null, Action<Cache> onCreated = null, string mode = null)
		{
			loggerFactory = loggerFactory ?? Enyim.Caching.Logger.GetLoggerFactory();
			Enyim.Caching.Logger.AssignLoggerFactory(loggerFactory);

			if (configuration == null)
			{
				var ex = new ConfigurationErrorsException("No configuration is found");
				Helper.Logger = loggerFactory.CreateLogger<Cache>();
				Helper.Logger.LogError(ex, "No configuration is found");
				throw ex;
			}

			if (configuration.Servers.Where(server => server.Type.ToLower().Equals("redis")).Any())
				Redis.GetClient(configuration.GetRedisConfiguration(), loggerFactory);

			if (configuration.Servers.Where(server => server.Type.ToLower().Equals("memcached")).Any())
				Memcached.GetClient(configuration.GetMemcachedConfiguration(loggerFactory), loggerFactory);

			return new Cache(name, configuration, loggerFactory, onCreated, mode);
		}

		/// <summary>
		/// Creates new an instance of caching component
		/// </summary>
		/// <param name="name">The string that presents name of isolated region</param>
		/// <param name="loggerFactory">The logger factory</param>
		/// <param name="onCreated">The action to fire when a new instance was created</param>
		/// <returns></returns>
		public static Cache CreateInstance(string name, ILoggerFactory loggerFactory = null, Action<Cache> onCreated = null, string mode = null)
			=> Cache.CreateInstance(name, Cache.Configuration, loggerFactory, onCreated, mode);

		/// <summary>
		/// Gets the singleton instance of caching component
		/// </summary>
		/// <param name="name">The string that presents name of isolated region</param>
		/// <param name="configuration">The caching configuration</param>
		/// <param name="loggerFactory">The logger factory</param>
		/// <returns></returns>
		public static Cache GetInstance(string name, ICacheConfiguration configuration, ILoggerFactory loggerFactory = null, Action<Cache> onCreated = null, string mode = null)
		{
			if (Cache._Instance == null)
			{
				Cache._Configuration = configuration;
				Cache._Instance = Cache.CreateInstance(name, loggerFactory, onCreated, mode);
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
			=> Cache.GetInstance(null, new CacheConfiguration(configurationSection), loggerFactory);

		/// <summary>
		/// Gets the singleton instance of caching componentGets the singleton instance of caching component
		/// </summary>
		/// <param name="svcProvider"></param>
		/// <returns></returns>
		public static Cache GetInstance(IServiceProvider svcProvider)
			=> Cache.GetInstance(null, svcProvider.GetService<ICacheConfiguration>(), svcProvider.GetService<ILoggerFactory>(), null, "(appsettings.json)");
		#endregion

		#region L1-Cache
		void _Send(string key, bool sendCacheItemInvalidatingMessage = true)
		{
			if (sendCacheItemInvalidatingMessage && this.UseMemoryCacheAsL1Cache && !string.IsNullOrWhiteSpace(key))
				this.SendCacheItemInvalidatingMessage?.Invoke(key);
		}

		bool _Set(string key, object value, TimeSpan validFor, bool sendCacheItemInvalidatingMessage = true)
		{
			this._Send(key, sendCacheItemInvalidatingMessage);
			return this.UseMemoryCacheAsL1Cache && this._memoryCache.Set(key, value, TimeSpan.FromMinutes(validFor.TotalMinutes > 2 && validFor.TotalMinutes < 13 ? validFor.TotalMinutes / 2 : 3));
		}

		bool _Set(string key, object value, int expirationTime = 0, bool sendCacheItemInvalidatingMessage = true)
			=> this._Set(key, value, TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : Helper.ExpirationTime), sendCacheItemInvalidatingMessage);

		bool _Set(string key, object value, DateTime expiresAt, bool sendCacheItemInvalidatingMessage = true)
			=> this._Set(key, value, (int)(expiresAt - DateTime.Now).TotalMinutes, sendCacheItemInvalidatingMessage);

		void _Set<T>(IDictionary<string, T> items, string keyPrefix = null, int expirationTime = 0, bool sendCacheItemInvalidatingMessage = true)
		{
			if (this.UseMemoryCacheAsL1Cache)
			{
				var dictionary = items != null
					? items.Where(kvp => kvp.Key != null).ToDictionary(kvp => (string.IsNullOrWhiteSpace(keyPrefix) ? "" : keyPrefix) + kvp.Key, kvp => kvp.Value)
					: new Dictionary<string, T>();
				foreach (var kvp in dictionary)
					this._Set(kvp.Key, kvp.Value, expirationTime, sendCacheItemInvalidatingMessage);
			}
		}

		bool _Set(string key, List<byte[]> fragments, int expirationTime = 0, bool sendCacheItemInvalidatingMessage = true)
			=> this.UseMemoryCacheAsL1Cache && this._Set(key, fragments.SelectMany(fragment => fragment).ToArray(), expirationTime, sendCacheItemInvalidatingMessage);

		object _Get(string key)
			=> this.UseMemoryCacheAsL1Cache	? this._memoryCache.Get(key) : null;

		T _Get<T>(string key)
		{
			var value = this._Get(key);
			return value != null && value is T tvalue ? tvalue : default;
		}

		IDictionary<string, object> _Get(IEnumerable<string> keys)
		{
			var dictionary = keys != null && keys.Any() ? new Dictionary<string, object>() : null;
			if (this.UseMemoryCacheAsL1Cache)
				foreach (var key in keys ?? Array.Empty<string>())
				{
					var value = this._Get(key);
					if (value != null)
						dictionary[key] = value;
				}
			return keys == null || dictionary == null || dictionary.Count < 1 || dictionary.Count != keys.Count() ? null : dictionary;
		}

		IDictionary<string, T> _Get<T>(IEnumerable<string> keys)
		{
			var dictionary = keys != null && keys.Any() ? new Dictionary<string, T>() : null;
			if (this.UseMemoryCacheAsL1Cache)
				foreach (var key in keys ?? Array.Empty<string>())
				{
					var value = this._Get<T>(key);
					if (value != null)
						dictionary[key] = value;
				}
			return keys == null || dictionary == null || dictionary.Count < 1 || dictionary.Count != keys.Count() ? null : dictionary;
		}

		HashSet<string> _GetSet(string key)
			=> this._Get<HashSet<string>>(key);

		bool _Remove(string key, bool sendCacheItemInvalidatingMessage = true)
		{
			this._Send(key, sendCacheItemInvalidatingMessage);
			return this.UseMemoryCacheAsL1Cache && this._memoryCache.Remove(key);
		}

		void _Remove(IEnumerable<string> keys, string keyPrefix = null, bool sendCacheItemInvalidatingMessage = true)
		{
			if (this.UseMemoryCacheAsL1Cache)
				foreach (var key in keys ?? Array.Empty<string>())
					this._Remove((string.IsNullOrWhiteSpace(keyPrefix) ? "" : keyPrefix) + key, sendCacheItemInvalidatingMessage);
		}

		bool _Exists(string key)
			=> this.UseMemoryCacheAsL1Cache && this._memoryCache.TryGetValue(key, out var _);

		void _Clear(bool sendCacheItemInvalidatingMessage = true)
		{
			this._Send("clear", sendCacheItemInvalidatingMessage);
			if (this.UseMemoryCacheAsL1Cache)
				this._memoryCache.Clear();
		}
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

		/// <summary>
		/// Gets or Sets the action that use to send message for invalidating an item in MemoryCache
		/// </summary>
		public Action<string> SendCacheItemInvalidatingMessage { get; set; }

		/// <summary>
		/// Gets the action that use to invalidate an item in MemoryCache
		/// </summary>
		public Action<string> ProcessCacheItemInvalidatingMessage { get; }

		/// <summary>
		/// Gets or Sets state to use MemoryCache as L1-Cache
		/// </summary>
		public bool UseMemoryCacheAsL1Cache { get; set; } = false;

		/// <summary>
		/// Gets or Sets state to pre-fetch L1-Cache
		/// </summary>
		public bool PrefetchL1Cache { get; set; } = false;

		/// <summary>
		/// Gets or Sets delaying times (miliseconds) before pre-fetching L1-Cache
		/// </summary>
		public int PrefetchDelay { get; set; } = 0;
		#endregion

		#region Keys
		/// <summary>
		/// Gets the collection of keys that associates with the cached items
		/// </summary>
		public HashSet<string> GetKeys()
			=> this._distributedCache.GetKeys();

		/// <summary>
		/// Gets the collection of keys that associates with the cached items
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
		{
			var result = this._distributedCache.Set(key, value, expirationTime);
			if (result)
				this._Set(key, value, expirationTime);
			return result;
		}

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Set(string key, object value, TimeSpan validFor)
		{
			var result = this._distributedCache.Set(key, value, validFor);
			if (result)
				this._Set(key, value, validFor);
			return result;
		}

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Set(string key, object value, DateTime expiresAt)
		{
			var result = this._distributedCache.Set(key, value, expiresAt);
			if (result)
				this._Set(key, value, expiresAt);
			return result;
		}

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public async Task<bool> SetAsync(string key, object value, int expirationTime = 0, CancellationToken cancellationToken = default)
		{
			var result = await this._distributedCache.SetAsync(key, value, expirationTime, cancellationToken).ConfigureAwait(false);
			if (result)
				this._Set(key, value, expirationTime);
			return result;
		}

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
		public async Task<bool> SetAsync(string key, object value, TimeSpan validFor, CancellationToken cancellationToken = default)
		{
			var result = await this._distributedCache.SetAsync(key, value, validFor, cancellationToken).ConfigureAwait(false);
			if (result)
				this._Set(key, value, validFor);
			return result;
		}

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public async Task<bool> SetAsync(string key, object value, DateTime expiresAt, CancellationToken cancellationToken = default)
		{
			var result = await this._distributedCache.SetAsync(key, value, expiresAt, cancellationToken).ConfigureAwait(false);
			if (result)
				this._Set(key, value, expiresAt);
			return result;
		}
		#endregion

		#region Set (Multiple)
		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		public void Set(IDictionary<string, object> items, string keyPrefix = null, int expirationTime = 0)
		{
			this._distributedCache.Set(items, keyPrefix, expirationTime);
			this._Set(items, keyPrefix, expirationTime);
		}

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <typeparam name="T">The type for casting the cached item</typeparam>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		public void Set<T>(IDictionary<string, T> items, string keyPrefix = null, int expirationTime = 0)
		{
			this._distributedCache.Set(items, keyPrefix, expirationTime);
			this._Set(items, keyPrefix, expirationTime);
		}

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		public Task SetAsync(IDictionary<string, object> items, string keyPrefix = null, int expirationTime = 0, CancellationToken cancellationToken = default)
		{
			this._Set(items, keyPrefix, expirationTime);
			return this._distributedCache.SetAsync(items, keyPrefix, expirationTime, cancellationToken);
		}

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <param name="items">The collection of items to add</param>
		public Task SetAsync(IDictionary<string, object> items, CancellationToken cancellationToken)
		{
			this._Set(items);
			return this._distributedCache.SetAsync(items, cancellationToken);
		}

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <typeparam name="T">The type for casting the cached item</typeparam>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		public Task SetAsync<T>(IDictionary<string, T> items, string keyPrefix = null, int expirationTime = 0, CancellationToken cancellationToken = default)
		{
			this._Set(items, keyPrefix, expirationTime);
			return this._distributedCache.SetAsync(items, keyPrefix, expirationTime, cancellationToken);
		}

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <typeparam name="T">The type for casting the cached item</typeparam>
		/// <param name="items">The collection of items to add</param>
		public Task SetAsync<T>(IDictionary<string, T> items, CancellationToken cancellationToken)
		{
			this._Set(items);
			return this._distributedCache.SetAsync(items, cancellationToken);
		}
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
		{
			var result = this._distributedCache.SetFragments(key, fragments, expirationTime);
			if (result)
				this._Set(key, fragments, expirationTime);
			return result;
		}

		/// <summary>
		/// Adds an item (as fragments) into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="fragments">The collection that contains all fragments (object that serialized as binary - array bytes)</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public async Task<bool> SetFragmentsAsync(string key, List<byte[]> fragments, int expirationTime = 0, CancellationToken cancellationToken = default)
		{
			var result = await this._distributedCache.SetFragmentsAsync(key, fragments, expirationTime, cancellationToken).ConfigureAwait(false);
			if (result)
				this._Set(key, fragments, expirationTime);
			return result;
		}

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
		{
			var result = this._distributedCache.SetAsFragments(key, value, expirationTime);
			if (result)
				this._Set(key, value, expirationTime);
			return result;
		}

		/// <summary>
		/// Serializes object into array of bytes, splits into one or more fragments and updates into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public async Task<bool> SetAsFragmentsAsync(string key, object value, int expirationTime = 0, CancellationToken cancellationToken = default)
		{
			var result = await this._distributedCache.SetAsFragmentsAsync(key, value, expirationTime, cancellationToken).ConfigureAwait(false);
			if (result)
				this._Set(key, value, expirationTime);
			return result;
		}

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
		{
			var result = this._distributedCache.Add(key, value, expirationTime);
			if (result)
				this._Set(key, value, expirationTime);
			return result;
		}

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Add(string key, object value, TimeSpan validFor)
		{
			var result = this._distributedCache.Add(key, value, validFor);
			if (result)
				this._Set(key, value, validFor);
			return result;
		}

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Add(string key, object value, DateTime expiresAt)
		{
			var result = this._distributedCache.Add(key, value, expiresAt);
			if (result)
				this._Set(key, value, expiresAt);
			return result;
		}

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public async Task<bool> AddAsync(string key, object value, int expirationTime = 0, CancellationToken cancellationToken = default)
		{
			var result = await this._distributedCache.AddAsync(key, value, expirationTime, cancellationToken).ConfigureAwait(false);
			if (result)
				this._Set(key, value, expirationTime);
			return result;
		}

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
		public async Task<bool> AddAsync(string key, object value, TimeSpan validFor, CancellationToken cancellationToken = default)
		{
			var result = await this._distributedCache.AddAsync(key, value, validFor, cancellationToken).ConfigureAwait(false);
			if (result)
				this._Set(key, value, validFor);
			return result;
		}

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public async Task<bool> AddAsync(string key, object value, DateTime expiresAt, CancellationToken cancellationToken = default)
		{
			var result = await this._distributedCache.AddAsync(key, value, expiresAt, cancellationToken).ConfigureAwait(false);
			if (result)
				this._Set(key, value, expiresAt);
			return result;
		}
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
		{
			var result = this._distributedCache.Replace(key, value, expirationTime);
			if (result)
				this._Set(key, value, expirationTime);
			return result;
		}

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Replace(string key, object value, TimeSpan validFor)
		{
			var result = this._distributedCache.Replace(key, value, validFor);
			if (result)
				this._Set(key, value, validFor);
			return result;
		}

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Replace(string key, object value, DateTime expiresAt)
		{
			var result = this._distributedCache.Replace(key, value, expiresAt);
			if (result)
				this._Set(key, value, expiresAt);
			return result;
		}

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public async Task<bool> ReplaceAsync(string key, object value, int expirationTime = 0, CancellationToken cancellationToken = default)
		{
			var result = await this._distributedCache.ReplaceAsync(key, value, expirationTime, cancellationToken).ConfigureAwait(false);
			if (result)
				this._Set(key, value, expirationTime);
			return result;
		}

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
		public async Task<bool> ReplaceAsync(string key, object value, TimeSpan validFor, CancellationToken cancellationToken = default)
		{
			var result = await this._distributedCache.ReplaceAsync(key, value, validFor, cancellationToken).ConfigureAwait(false);
			if (result)
				this._Set(key, value, validFor);
			return result;
		}

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public async Task<bool> ReplaceAsync(string key, object value, DateTime expiresAt, CancellationToken cancellationToken = default)
		{
			var result = await this._distributedCache.ReplaceAsync(key, value, expiresAt, cancellationToken).ConfigureAwait(false);
			if (result)
				this._Set(key, value, expiresAt);
			return result;
		}
		#endregion

		#region Refresh
		/// <summary>
		/// Refreshs an existed item
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <returns>Returns a boolean value indicating if the item is refreshed or not</returns>
		public bool Refresh(string key)
			=> this._distributedCache.Refresh(key);

		/// <summary>
		/// Refreshs an existed item
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <returns>Returns a boolean value indicating if the item is refreshed or not</returns>
		public Task<bool> RefreshAsync(string key, CancellationToken cancellationToken = default)
			=> this._distributedCache.RefreshAsync(key, cancellationToken);
		#endregion

		#region Get
		/// <summary>
		/// Retreives a cached item
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to retreive</param>
		/// <returns>The retrieved cache item, or a null reference if the key is not found</returns>
		public object Get(string key)
		{
			var value = this._Get(key);
			if (value == null)
			{
				value = this._distributedCache.Get(key);
				this._Set(key, value);
			}
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
			var value = this._Get<T>(key);
			if (value == null)
			{
				value = this._distributedCache.Get<T>(key);
				this._Set(key, value);
			}
			return value;
		}

		/// <summary>
		/// Retreives a cached item
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to retreive</param>
		/// <returns>The retrieved cache item, or a null reference if the key is not found</returns>
		public async Task<object> GetAsync(string key, CancellationToken cancellationToken = default)
		{
			var value = this._Get(key);
			if (value == null)
			{
				value = await this._distributedCache.GetAsync(key, cancellationToken).ConfigureAwait(false);
				this._Set(key, value);
			}
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
			var value = this._Get<T>(key);
			if (value == null)
			{
				value = await this._distributedCache.GetAsync<T>(key, cancellationToken).ConfigureAwait(false);
				this._Set(key, value);
			}
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
			var value = this._Get(keys);
			if (value == null)
			{
				value = this._distributedCache.Get(keys);
				if (value != null && value.Count > 0)
					this._Set(value);
			}
			return value;
		}

		/// <summary>
		/// Retreives a collection of cached items
		/// </summary>
		/// <param name="keys">The collection of items' keys</param>
		/// <returns>The collection of cache items</returns>
		public IDictionary<string, T> Get<T>(IEnumerable<string> keys)
		{
			var value = this._Get<T>(keys);
			if (value == null)
			{
				value = this._distributedCache.Get<T>(keys);
				if (value != null && value.Count > 0)
					this._Set(value);
			}
			return value;
		}

		/// <summary>
		/// Retreives a collection of cached items
		/// </summary>
		/// <param name="keys">The collection of items' keys</param>
		/// <returns>The collection of cache items</returns>
		public async Task<IDictionary<string, object>> GetAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
		{
			var value = this._Get(keys);
			if (value == null)
			{
				value = await this._distributedCache.GetAsync(keys, cancellationToken).ConfigureAwait(false);
				if (value != null && value.Count > 0)
					this._Set(value);
			}
			return value;
		}

		/// <summary>
		/// Retreives a collection of cached items
		/// </summary>
		/// <param name="keys">The collection of items' keys</param>
		/// <returns>The collection of cache items</returns>
		public async Task<IDictionary<string, T>> GetAsync<T>(IEnumerable<string> keys, CancellationToken cancellationToken = default)
		{
			var value = this._Get<T>(keys);
			if (value == null)
			{
				value = await this._distributedCache.GetAsync<T>(keys, cancellationToken).ConfigureAwait(false);
				if (value != null && value.Count > 0)
					this._Set(value);
			}
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
		{
			this._Remove(key);
			return this._distributedCache.Remove(key);
		}

		/// <summary>
		/// Removes a cached item
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to remove</param>
		/// <returns>Returns a boolean value indicating if the item is removed or not</returns>
		public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
		{
			this._Remove(key);
			return this._distributedCache.RemoveAsync(key, cancellationToken);
		}
		#endregion

		#region Remove (Multiple)
		/// <summary>
		/// Removes a collection of cached items
		/// </summary>
		/// <param name="keys">The collection that presents key of cached items need to remove</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		public void Remove(IEnumerable<string> keys, string keyPrefix = null)
		{
			this._Remove(keys, keyPrefix);
			this._distributedCache.Remove(keys, keyPrefix);
		}

		/// <summary>
		/// Removes a collection of cached items
		/// </summary>
		/// <param name="keys">The collection that presents key of cached items need to remove</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		public Task RemoveAsync(IEnumerable<string> keys, string keyPrefix = null, CancellationToken cancellationToken = default)
		{
			this._Remove(keys, keyPrefix);
			return this._distributedCache.RemoveAsync(keys, keyPrefix, cancellationToken);
		}

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
			=> this._distributedCache.RemoveFragments(key);

		/// <summary>
		/// Removes a cached item (with first 100 fragments) from cache storage
		/// </summary>
		/// <param name="key">The string that presents key of fragmented items need to be removed</param>
		public Task RemoveFragmentsAsync(string key, CancellationToken cancellationToken = default)
			=> this._distributedCache.RemoveFragmentsAsync(key, cancellationToken);
		#endregion

		#region Exists
		/// <summary>
		/// Determines whether an item exists in the cache
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to check</param>
		/// <returns>Returns a boolean value indicating if the object that associates with the key is cached or not</returns>
		public bool Exists(string key)
			=> this._Exists(key) || this._distributedCache.Exists(key);

		/// <summary>
		/// Determines whether an item exists in the cache
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to check</param>
		/// <returns>Returns a boolean value indicating if the object that associates with the key is cached or not</returns>
		public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
			=> this._Exists(key) || await this._distributedCache.ExistsAsync(key, cancellationToken).ConfigureAwait(false);
		#endregion

		#region Set Members
		/// <summary>
		/// Gets a set
		/// </summary>
		/// <param name="key"></param>
		/// <returns></returns>
		public HashSet<string> GetSetMembers(string key)
		{
			var value = this._GetSet(key);
			if (value == null)
			{
				value = this._distributedCache.GetSetMembers(key);
				if (value != null)
					this._Set(key, value);
			}
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
			var value = this._GetSet(key);
			if (value == null)
			{
				value = await this._distributedCache.GetSetMembersAsync(key, cancellationToken).ConfigureAwait(false);
				if (value != null)
					this._Set(key, value);
			}
			return value;
		}

		/// <summary>
		/// Adds a value into a set
		/// </summary>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <returns></returns>
		public bool AddSetMember(string key, string value)
		{
			this._Remove(key);
			return this._distributedCache.AddSetMember(key, value);
		}

		/// <summary>
		/// Adds the values into a set
		/// </summary>
		/// <param name="key"></param>
		/// <param name="values"></param>
		/// <returns></returns>
		public bool AddSetMembers(string key, IEnumerable<string> values)
		{
			this._Remove(key);
			return this._distributedCache.AddSetMembers(key, values);
		}

		/// <summary>
		/// Adds a value into a set
		/// </summary>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public Task<bool> AddSetMemberAsync(string key, string value, CancellationToken cancellationToken = default)
		{
			this._Remove(key);
			return this._distributedCache.AddSetMemberAsync(key, value, cancellationToken);
		}

		/// <summary>
		/// Adds the values into a set
		/// </summary>
		/// <param name="key"></param>
		/// <param name="values"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public Task<bool> AddSetMembersAsync(string key, IEnumerable<string> values, CancellationToken cancellationToken = default)
		{
			this._Remove(key);
			return this._distributedCache.AddSetMembersAsync(key, values, cancellationToken);
		}

		/// <summary>
		/// Removes a value from a set
		/// </summary>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <returns></returns>
		public bool RemoveSetMember(string key, string value)
		{
			this._Remove(key);
			return this._distributedCache.RemoveSetMember(key, value);
		}

		/// <summary>
		/// Removes the values from a set
		/// </summary>
		/// <param name="key"></param>
		/// <param name="values"></param>
		/// <returns></returns>
		public bool RemoveSetMembers(string key, IEnumerable<string> values)
		{
			this._Remove(key);
			return this._distributedCache.RemoveSetMembers(key, values);
		}

		/// <summary>
		/// Removes a value from a set
		/// </summary>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public Task<bool> RemoveSetMemberAsync(string key, string value, CancellationToken cancellationToken = default)
		{
			this._Remove(key);
			return this._distributedCache.RemoveSetMemberAsync(key, value, cancellationToken);
		}

		/// <summary>
		/// Removes the values from a set
		/// </summary>
		/// <param name="key"></param>
		/// <param name="values"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public Task<bool> RemoveSetMembersAsync(string key, IEnumerable<string> values, CancellationToken cancellationToken = default)
		{
			this._Remove(key);
			return this._distributedCache.RemoveSetMembersAsync(key, values, cancellationToken);
		}
		#endregion

		#region Clear
		/// <summary>
		/// Clears the cache storage of this isolated region
		/// </summary>
		public void Clear()
		{
			this._Clear();
			this._distributedCache.Clear();
		}

		/// <summary>
		/// Clears the cache storage of this isolated region
		/// </summary>
		public Task ClearAsync(CancellationToken cancellationToken = default)
		{
			this._Clear();
			return this._distributedCache.ClearAsync(cancellationToken);
		}
		#endregion

		#region Flush
		/// <summary>
		/// Removes all data from the cache
		/// </summary>
		public void FlushAll()
		{
			this._Clear();
			this._distributedCache.FlushAll();
		}

		/// <summary>
		/// Removes all data from the cache
		/// </summary>
		public Task FlushAllAsync(CancellationToken cancellationToken = default)
		{
			this._Clear();
			return this._distributedCache.FlushAllAsync(cancellationToken);
		}
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