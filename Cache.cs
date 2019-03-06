#region Related components
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Diagnostics;
using System.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

using CacheUtils;
#endregion

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("VIEApps.Components.XUnitTests")]

namespace net.vieapps.Components.Caching
{
	/// <summary>
	/// Manipulates objects in isolated regions with distributed cache servers (support Redis &amp; Memcached)
	/// </summary>
	[DebuggerDisplay("{Name} ({ExpirationTime} minutes)")]
	public sealed class Cache : IDistributedCache, ICache
	{
		readonly ICache _cache;

		/// <summary>
		/// Create a new instance of distributed cache with isolated region
		/// </summary>
		/// <param name="name">The string that presents name of isolated region</param>
		/// <param name="expirationTime">Time for caching an item (in minutes)</param>
		/// <param name="storeKeys">true to active store all keys of the region (to clear or use with other purposes further)</param>
		/// <param name="loggerFactory">The logger factory for working with logs</param>
		public Cache(string name = null, int expirationTime = 0, bool storeKeys = false, ILoggerFactory loggerFactory = null) : this(name, expirationTime, storeKeys, null, loggerFactory) { }

		/// <summary>
		/// Create a new instance of distributed cache with isolated region
		/// </summary>
		/// <param name="name">The string that presents name of isolated region</param>
		/// <param name="provider">The string that presents the caching provider ('Redis' or 'Memcached') - the default provider is 'Redis'</param>
		/// <param name="loggerFactory">The logger factory for working with logs</param>
		public Cache(string name, string provider, ILoggerFactory loggerFactory = null) : this(name, 0, false, provider, loggerFactory) { }

		/// <summary>
		/// Create a new instance of distributed cache with isolated region
		/// </summary>
		/// <param name="name">The string that presents name of isolated region</param>
		/// <param name="expirationTime">Time for caching an item (in minutes)</param>
		/// <param name="provider">The string that presents the caching provider ('Redis' or 'Memcached') - the default provider is 'Redis'</param>
		/// <param name="loggerFactory">The logger factory for working with logs</param>
		public Cache(string name, int expirationTime, string provider, ILoggerFactory loggerFactory = null) : this(name, expirationTime, false, provider, loggerFactory) { }

		/// <summary>
		/// Create a new instance of distributed cache with isolated region
		/// </summary>
		/// <param name="name">The string that presents name of isolated region</param>
		/// <param name="expirationTime">Time for caching an item (in minutes)</param>
		/// <param name="storeKeys">true to active store all keys of the region (to clear or use with other purposes further)</param>
		/// <param name="provider">The string that presents the caching provider ('Redis' or 'Memcached') - the default provider is 'Redis'</param>
		/// <param name="loggerFactory">The logger factory for working with logs</param>
		public Cache(string name, int expirationTime, bool storeKeys, string provider, ILoggerFactory loggerFactory = null)
		{
			this._cache = !string.IsNullOrWhiteSpace(provider) && provider.Trim().ToLower().Equals("memcached")
				? new Memcached(name, expirationTime, storeKeys) as ICache
				: new Redis(name, expirationTime, storeKeys) as ICache;

			Helper.Logger = (loggerFactory ?? Enyim.Caching.Logger.GetLoggerFactory()).CreateLogger<Cache>();
			if (Helper.Logger.IsEnabled(LogLevel.Debug))
				Helper.Logger.LogInformation($"The VIEApps NGX Caching's instance was created - {this._cache.GetType().ToString().Split('.').Last()}: {this._cache.Name} ({this._cache.ExpirationTime} minutes)");
		}

		#region Get instance (singleton)
		static ICacheConfiguration _Configuration = null;

		/// <summary>
		/// Gets the global settings of the caching component
		/// </summary>
		public static ICacheConfiguration Configuration
		{
			get
			{
				if (Cache._Configuration == null && ConfigurationManager.GetSection("cache") is CacheConfigurationSectionHandler configSection)
					Cache._Configuration = new CacheConfiguration(configSection);
				return Cache._Configuration;
			}
		}

		static Cache _Instance { get; set; } = null;

		/// <summary>
		/// Gets the instance of caching component
		/// </summary>
		/// <param name="configuration"></param>
		/// <param name="loggerFactory"></param>
		/// <returns></returns>
		public static Cache GetInstance(ICacheConfiguration configuration, ILoggerFactory loggerFactory = null)
		{
			if (Cache._Instance == null)
			{
				Cache._Configuration = configuration ?? throw new ConfigurationErrorsException($"No configuration is found [{nameof(configuration)}]");
				Cache._Instance = new Cache(configuration.RegionName, configuration.ExpirationTime, false, configuration.Provider, loggerFactory);

				if (configuration.Servers.Where(s => s.Type.ToLower().Equals("redis")).Count() > 0)
					Redis.GetClient(configuration, loggerFactory);

				if (configuration.Servers.Where(s => s.Type.ToLower().Equals("memcached")).Count() > 0)
					Memcached.GetClient(configuration, loggerFactory);

				if (loggerFactory != null)
					Helper.Logger = loggerFactory.CreateLogger<Cache>();
			}
			return Cache._Instance;
		}

		/// <summary>
		/// Gets the instance of caching component
		/// </summary>
		/// <param name="configurationSection"></param>
		/// <param name="loggerFactory"></param>
		/// <returns></returns>
		public static Cache GetInstance(CacheConfigurationSectionHandler configurationSection, ILoggerFactory loggerFactory = null)
		{
			if (Cache._Instance == null)
			{
				Cache.GetInstance(new CacheConfiguration(configurationSection), loggerFactory);
				var logger = loggerFactory?.CreateLogger<Cache>();
				if (logger != null && logger.IsEnabled(LogLevel.Debug))
					logger.LogInformation($"The VIEApps NGX Caching's instance was created with stand-alone configuration (app.config/web.config) - {Cache.Configuration.Provider}: {Cache.Configuration.RegionName} ({Cache.Configuration.ExpirationTime} minutes)");
			}
			return Cache._Instance;
		}

		internal static Cache GetInstance(IServiceProvider svcProvider)
		{
			if (Cache._Instance == null)
			{
				Cache.GetInstance(svcProvider.GetService<ICacheConfiguration>(), svcProvider.GetService<ILoggerFactory>());
				var logger = svcProvider.GetService<ILoggerFactory>().CreateLogger<Cache>();
				if (logger.IsEnabled(LogLevel.Debug))
					logger.LogInformation($"The VIEApps NGX Caching's instance was created with integrated configuration (appsettings.json) - {Cache.Configuration.Provider}: {Cache.Configuration.RegionName} ({Cache.Configuration.ExpirationTime} minutes)");
			}
			return Cache._Instance;
		}
		#endregion

		#region Properties
		/// <summary>
		/// Gets the name of the isolated region
		/// </summary>
		public string Name => this._cache.Name;

		/// <summary>
		/// Gets the expiration time (in minutes)
		/// </summary>
		public int ExpirationTime => this._cache.ExpirationTime;

		/// <summary>
		/// Gets the collection of keys
		/// </summary>
		public HashSet<string> Keys => this._cache.Keys;
		#endregion

		#region Keys
		/// <summary>
		/// Gets the collection of keys that associates with the cached items
		/// </summary>
		public HashSet<string> GetKeys() => this._cache.GetKeys();

		/// <summary>
		/// Gets the collection of keys that associates with the cached items
		/// </summary>
		public Task<HashSet<string>> GetKeysAsync(CancellationToken cancellationToken = default(CancellationToken)) => this._cache.GetKeysAsync(cancellationToken);
		#endregion

		#region Set
		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Set(string key, object value, int expirationTime = 0) => this._cache.Set(key, value, expirationTime);

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Set(string key, object value, TimeSpan validFor) => this._cache.Set(key, value, validFor);

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Set(string key, object value, DateTime expiresAt) => this._cache.Set(key, value, expiresAt);

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetAsync(string key, object value, int expirationTime = 0, CancellationToken cancellationToken = default(CancellationToken)) => this._cache.SetAsync(key, value, expirationTime, cancellationToken);

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetAsync(string key, object value, TimeSpan validFor, CancellationToken cancellationToken = default(CancellationToken)) => this._cache.SetAsync(key, value, validFor, cancellationToken);

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetAsync(string key, object value, DateTime expiresAt, CancellationToken cancellationToken = default(CancellationToken)) => this._cache.SetAsync(key, value, expiresAt, cancellationToken);
		#endregion

		#region Set (Multiple)
		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		public void Set(IDictionary<string, object> items, string keyPrefix = null, int expirationTime = 0) => this._cache.Set(items, keyPrefix, expirationTime);

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <typeparam name="T">The type for casting the cached item</typeparam>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		public void Set<T>(IDictionary<string, T> items, string keyPrefix = null, int expirationTime = 0) => this._cache.Set<T>(items, keyPrefix, expirationTime);

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		public Task SetAsync(IDictionary<string, object> items, string keyPrefix = null, int expirationTime = 0, CancellationToken cancellationToken = default(CancellationToken)) => this._cache.SetAsync(items, keyPrefix, expirationTime, cancellationToken);

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <typeparam name="T">The type for casting the cached item</typeparam>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		public Task SetAsync<T>(IDictionary<string, T> items, string keyPrefix = null, int expirationTime = 0, CancellationToken cancellationToken = default(CancellationToken)) => this._cache.SetAsync<T>(items, keyPrefix, expirationTime, cancellationToken);
		#endregion

		#region Set (Fragment)
		/// <summary>
		/// Adds an item (as fragments) into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="fragments">The collection that contains all fragments (object that serialized as binary - array bytes)</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool SetFragments(string key, List<byte[]> fragments, int expirationTime = 0) => this._cache.SetFragments(key, fragments, expirationTime);

		/// <summary>
		/// Adds an item (as fragments) into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="fragments">The collection that contains all fragments (object that serialized as binary - array bytes)</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetFragmentsAsync(string key, List<byte[]> fragments, int expirationTime = 0, CancellationToken cancellationToken = default(CancellationToken)) => this._cache.SetFragmentsAsync(key, fragments, expirationTime, cancellationToken);

		/// <summary>
		/// Serializes object into array of bytes, splits into one or more fragments and updates into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool SetAsFragments(string key, object value, int expirationTime = 0) => this._cache.SetAsFragments(key, value, expirationTime);

		/// <summary>
		/// Serializes object into array of bytes, splits into one or more fragments and updates into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetAsFragmentsAsync(string key, object value, int expirationTime = 0, CancellationToken cancellationToken = default(CancellationToken)) => this._cache.SetAsFragmentsAsync(key, value, expirationTime, cancellationToken);
		#endregion

		#region Add
		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Add(string key, object value, int expirationTime = 0) => this._cache.Add(key, value, expirationTime);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Add(string key, object value, TimeSpan validFor) => this._cache.Add(key, value, validFor);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Add(string key, object value, DateTime expiresAt) => this._cache.Add(key, value, expiresAt);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> AddAsync(string key, object value, int expirationTime = 0, CancellationToken cancellationToken = default(CancellationToken)) => this._cache.AddAsync(key, value, expirationTime, cancellationToken);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> AddAsync(string key, object value, TimeSpan validFor, CancellationToken cancellationToken = default(CancellationToken)) => this._cache.AddAsync(key, value, validFor, cancellationToken);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> AddAsync(string key, object value, DateTime expiresAt, CancellationToken cancellationToken = default(CancellationToken)) => this._cache.AddAsync(key, value, expiresAt, cancellationToken);
		#endregion

		#region Replace
		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Replace(string key, object value, int expirationTime = 0) => this._cache.Replace(key, value, expirationTime);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Replace(string key, object value, TimeSpan validFor) => this._cache.Replace(key, value, validFor);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Replace(string key, object value, DateTime expiresAt) => this._cache.Replace(key, value, expiresAt);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> ReplaceAsync(string key, object value, int expirationTime = 0, CancellationToken cancellationToken = default(CancellationToken)) => this._cache.ReplaceAsync(key, value, expirationTime, cancellationToken);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> ReplaceAsync(string key, object value, TimeSpan validFor, CancellationToken cancellationToken = default(CancellationToken)) => this._cache.ReplaceAsync(key, value, validFor, cancellationToken);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> ReplaceAsync(string key, object value, DateTime expiresAt, CancellationToken cancellationToken = default(CancellationToken)) => this._cache.ReplaceAsync(key, value, expiresAt, cancellationToken);
		#endregion

		#region Get
		/// <summary>
		/// Retreives a cached item
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to retreive</param>
		/// <returns>The retrieved cache item, or a null reference if the key is not found</returns>
		public object Get(string key) => this._cache.Get(key);

		/// <summary>
		/// Retreives a cached item
		/// </summary>
		/// <typeparam name="T">The type for casting the cached item</typeparam>
		/// <param name="key">The string that presents key of cached item need to retreive</param>
		/// <returns>The retrieved cache item, or a null reference if the key is not found</returns>
		public T Get<T>(string key) => this._cache.Get<T>(key);

		/// <summary>
		/// Retreives a cached item
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to retreive</param>
		/// <returns>The retrieved cache item, or a null reference if the key is not found</returns>
		public Task<object> GetAsync(string key, CancellationToken cancellationToken = default(CancellationToken)) => this._cache.GetAsync(key, cancellationToken);

		/// <summary>
		/// Retreives a cached item
		/// </summary>
		/// <typeparam name="T">The type for casting the cached item</typeparam>
		/// <param name="key">The string that presents key of cached item need to retreive</param>
		/// <returns>The retrieved cache item, or a null reference if the key is not found</returns>
		public Task<T> GetAsync<T>(string key, CancellationToken cancellationToken = default(CancellationToken)) => this._cache.GetAsync<T>(key, cancellationToken);
		#endregion

		#region Get (Multiple)
		/// <summary>
		/// Retreives a collection of cached items
		/// </summary>
		/// <param name="keys">The collection of items' keys</param>
		/// <returns>The collection of cache items</returns>
		public IDictionary<string, object> Get(IEnumerable<string> keys) => this._cache.Get(keys);

		/// <summary>
		/// Retreives a collection of cached items
		/// </summary>
		/// <param name="keys">The collection of items' keys</param>
		/// <returns>The collection of cache items</returns>
		public Task<IDictionary<string, object>> GetAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default(CancellationToken)) => this._cache.GetAsync(keys, cancellationToken);

		/// <summary>
		/// Retreives a collection of cached items
		/// </summary>
		/// <param name="keys">The collection of items' keys</param>
		/// <returns>The collection of cache items</returns>
		public IDictionary<string, T> Get<T>(IEnumerable<string> keys) => this._cache.Get<T>(keys);

		/// <summary>
		/// Retreives a collection of cached items
		/// </summary>
		/// <param name="keys">The collection of items' keys</param>
		/// <returns>The collection of cache items</returns>
		public Task<IDictionary<string, T>> GetAsync<T>(IEnumerable<string> keys, CancellationToken cancellationToken = default(CancellationToken)) => this._cache.GetAsync<T>(keys, cancellationToken);
		#endregion

		#region Get (Fragment)
		/// <summary>
		/// Gets fragment information that associates with the key
		/// </summary>
		/// <param name="key">The string that presents key of fragment information</param>
		/// <returns>The information of fragments, first element is total number of fragments, second element is total length of data</returns>
		public Tuple<int, int> GetFragments(string key) => this._cache.GetFragments(key);

		/// <summary>
		/// Gets fragment information that associates with the key
		/// </summary>
		/// <param name="key">The string that presents key of fragment information</param>
		/// <returns>The information of fragments, first element is total number of fragments, second element is total length of data</returns>
		public Task<Tuple<int, int>> GetFragmentsAsync(string key, CancellationToken cancellationToken = default(CancellationToken)) => this._cache.GetFragmentsAsync(key, cancellationToken);

		/// <summary>
		/// Gets cached of fragmented items that associates with the key and indexes
		/// </summary>
		/// <param name="key">The string that presents key of all fragmented items</param>
		/// <param name="indexes">The collection that presents indexes of all fragmented items need to get</param>
		/// <returns>The collection of array of bytes that presents serialized information of fragmented items</returns>
		public List<byte[]> GetAsFragments(string key, List<int> indexes) => this._cache.GetAsFragments(key, indexes);

		/// <summary>
		/// Gets cached of fragmented items that associates with the key and indexes
		/// </summary>
		/// <param name="key">The string that presents key of all fragmented items</param>
		/// <param name="indexes">The collection that presents indexes of all fragmented items need to get</param>
		/// <returns>The collection of array of bytes that presents serialized information of fragmented items</returns>
		public List<byte[]> GetAsFragments(string key, params int[] indexes) => this._cache.GetAsFragments(key, indexes);

		/// <summary>
		/// Gets cached of fragmented items that associates with the key and indexes
		/// </summary>
		/// <param name="key">The string that presents key of all fragmented items</param>
		/// <param name="indexes">The collection that presents indexes of all fragmented items need to get</param>
		/// <returns>The collection of array of bytes that presents serialized information of fragmented items</returns>
		public Task<List<byte[]>> GetAsFragmentsAsync(string key, List<int> indexes, CancellationToken cancellationToken = default(CancellationToken)) => this._cache.GetAsFragmentsAsync(key, indexes, cancellationToken);

		/// <summary>
		/// Gets cached of fragmented items that associates with the key and indexes
		/// </summary>
		/// <param name="key">The string that presents key of all fragmented items</param>
		/// <param name="indexes">The collection that presents indexes of all fragmented items need to get</param>
		/// <returns>The collection of array of bytes that presents serialized information of fragmented items</returns>
		public Task<List<byte[]>> GetAsFragmentsAsync(string key, CancellationToken cancellationToken = default(CancellationToken), params int[] indexes) => this._cache.GetAsFragmentsAsync(key, cancellationToken, indexes);
		#endregion

		#region Remove
		/// <summary>
		/// Removes a cached item
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to remove</param>
		/// <returns>Returns a boolean value indicating if the item is removed or not</returns>
		public bool Remove(string key) => this._cache.Remove(key);

		/// <summary>
		/// Removes a cached item
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to remove</param>
		/// <returns>Returns a boolean value indicating if the item is removed or not</returns>
		public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default(CancellationToken)) => this._cache.RemoveAsync(key, cancellationToken);
		#endregion

		#region Remove (Multiple)
		/// <summary>
		/// Removes a collection of cached items
		/// </summary>
		/// <param name="keys">The collection that presents key of cached items need to remove</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		public void Remove(IEnumerable<string> keys, string keyPrefix = null) => this._cache.Remove(keys, keyPrefix);

		/// <summary>
		/// Removes a collection of cached items
		/// </summary>
		/// <param name="keys">The collection that presents key of cached items need to remove</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		public Task RemoveAsync(IEnumerable<string> keys, string keyPrefix = null, CancellationToken cancellationToken = default(CancellationToken)) => this._cache.RemoveAsync(keys, keyPrefix, cancellationToken);
		#endregion

		#region Remove (Fragment)
		/// <summary>
		/// Removes a cached item (with first 100 fragments) from cache storage
		/// </summary>
		/// <param name="key">The string that presents key of fragmented items need to be removed</param>
		public void RemoveFragments(string key) => this._cache.RemoveFragments(key);

		/// <summary>
		/// Removes a cached item (with first 100 fragments) from cache storage
		/// </summary>
		/// <param name="key">The string that presents key of fragmented items need to be removed</param>
		public Task RemoveFragmentsAsync(string key, CancellationToken cancellationToken = default(CancellationToken)) => this._cache.RemoveFragmentsAsync(key, cancellationToken);
		#endregion

		#region Exists
		/// <summary>
		/// Determines whether an item exists in the cache
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to check</param>
		/// <returns>Returns a boolean value indicating if the object that associates with the key is cached or not</returns>
		public bool Exists(string key) => this._cache.Exists(key);

		/// <summary>
		/// Determines whether an item exists in the cache
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to check</param>
		/// <returns>Returns a boolean value indicating if the object that associates with the key is cached or not</returns>
		public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default(CancellationToken)) => this._cache.ExistsAsync(key, cancellationToken);
		#endregion

		#region Clear
		/// <summary>
		/// Clears the cache storage of this isolated region
		/// </summary>
		public void Clear() => this._cache.Clear();

		/// <summary>
		/// Clears the cache storage of this isolated region
		/// </summary>
		public Task ClearAsync(CancellationToken cancellationToken = default(CancellationToken)) => this._cache.ClearAsync(cancellationToken);
		#endregion

		#region Implements of IDistributedCache
		void IDistributedCache.Set(string key, byte[] value, DistributedCacheEntryOptions options)
		{
			if (string.IsNullOrWhiteSpace(key))
				throw new ArgumentNullException(nameof(key));
			var expires = options == null ? TimeSpan.Zero : options.GetExpiration();
			var validFor = expires is TimeSpan
				? (TimeSpan)expires
				: CacheUtils.Helper.UnixEpoch.AddSeconds((long)expires).ToTimeSpan();
			if (this.Set(key, value, validFor) && expires is TimeSpan && validFor != TimeSpan.Zero)
				this.Set(key.GetIDistributedCacheExpirationKey(), expires, validFor);
		}

		async Task IDistributedCache.SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken cancellationToken = default(CancellationToken))
		{
			if (string.IsNullOrWhiteSpace(key))
				throw new ArgumentNullException(nameof(key));
			var expires = options == null ? TimeSpan.Zero : options.GetExpiration();
			var validFor = expires is TimeSpan
				? (TimeSpan)expires
				: CacheUtils.Helper.UnixEpoch.AddSeconds((long)expires).ToTimeSpan();
			if (await this.SetAsync(key, value, validFor, cancellationToken).ConfigureAwait(false) && expires is TimeSpan && validFor != TimeSpan.Zero)
				await this.SetAsync(key.GetIDistributedCacheExpirationKey(), expires, validFor, cancellationToken).ConfigureAwait(false);
		}

		byte[] IDistributedCache.Get(string key)
			=> string.IsNullOrWhiteSpace(key)
				? throw new ArgumentNullException(nameof(key))
				: this.Get<byte[]>(key);

		Task<byte[]> IDistributedCache.GetAsync(string key, CancellationToken cancellationToken = default(CancellationToken))
			=> string.IsNullOrWhiteSpace(key)
				? Task.FromException<byte[]>(new ArgumentNullException(nameof(key)))
				: this.GetAsync<byte[]>(key, cancellationToken);

		void IDistributedCache.Refresh(string key)
		{
			if (string.IsNullOrWhiteSpace(key))
				throw new ArgumentNullException(nameof(key));
			var value = this.Get<byte[]>(key);
			var expires = value != null ? this.Get(key.GetIDistributedCacheExpirationKey()) : null;
			if (value != null && expires != null && expires is TimeSpan && this.Replace(key, value, (TimeSpan)expires))
				this.Replace(key.GetIDistributedCacheExpirationKey(), expires, (TimeSpan)expires);
		}

		async Task IDistributedCache.RefreshAsync(string key, CancellationToken cancellationToken = default(CancellationToken))
		{
			if (string.IsNullOrWhiteSpace(key))
				throw new ArgumentNullException(nameof(key));
			var value = await this.GetAsync<byte[]>(key, cancellationToken).ConfigureAwait(false);
			var expires = value != null ? await this.GetAsync(key.GetIDistributedCacheExpirationKey(), cancellationToken).ConfigureAwait(false) : null;
			if (value != null && expires != null && expires is TimeSpan && await this.ReplaceAsync(key, value, (TimeSpan)expires, cancellationToken).ConfigureAwait(false))
				await this.ReplaceAsync(key.GetIDistributedCacheExpirationKey(), expires, (TimeSpan)expires, cancellationToken).ConfigureAwait(false);
		}

		void IDistributedCache.Remove(string key)
		{
			if (string.IsNullOrWhiteSpace(key))
				throw new ArgumentNullException(nameof(key));
			this.Remove(new List<string>() { key, key.GetIDistributedCacheExpirationKey() }, null);
		}

		Task IDistributedCache.RemoveAsync(string key, CancellationToken cancellationToken = default(CancellationToken))
			=> string.IsNullOrWhiteSpace(key)
				? Task.FromException(new ArgumentNullException(nameof(key)))
				: this.RemoveAsync(new List<string>() { key, key.GetIDistributedCacheExpirationKey() }, null, cancellationToken);
		#endregion

	}
}