#region Related components
using System;
using System.Net;
using System.Xml;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Enyim.Caching.Configuration;
using Enyim.Caching.Memcached;
using net.vieapps.Components.Caching;
using CacheUtils;
#endregion

namespace net.vieapps.Components.Caching
{
	public static class Helper
	{

		#region Data
		public const int FlagOfFirstFragmentBlock = 0xfe52;
		public static readonly int FragmentSize = (1024 * 1024) - 256;
		internal static readonly string RegionsKey = "VIEApps-NGX-Regions";

		public static int ExpirationTime => Cache.Configuration != null && Cache.Configuration.ExpirationTime > 0 ? Cache.Configuration.ExpirationTime : 30;

		public static string GetRegionName(string name)
			=> Regex.Replace(!string.IsNullOrWhiteSpace(name) ? name : Cache.Configuration?.RegionName ?? "VIEApps-NGX-Cache", "[^0-9a-zA-Z:-]+", "");

		public static string GetCacheKey(string region, string key)
			=> region + "@" + key.Replace(" ", "-");

		public static string GetFragmentKey(string key, int index)
		{
			var fragmentKey = "0" + index.ToString();
			return key.Replace(" ", "-") + "$[Fragment<" + fragmentKey.Substring(fragmentKey.Length - 2) + ">]";
		}

		internal static List<string> GetFragmentKeys(string key, int max)
		{
			var keys = new List<string> { key };
			for (var index = 1; index <= max; index++)
				keys.Add(Helper.GetFragmentKey(key, index));
			return keys;
		}
		#endregion

		#region Serialize & Deserialize
		/// <summary>
		/// Gets the flags
		/// </summary>
		/// <param name="data"></param>
		/// <param name="getLength"></param>
		/// <returns></returns>
		public static (int TypeFlag, int Length) GetFlags(this byte[] data, bool getLength = false)
		{
			if (data == null || data.Length < 4)
				return (0, 0);

			var tmp = new byte[4];
			Buffer.BlockCopy(data, 0, tmp, 0, 4);
			var typeFlag = BitConverter.ToInt32(tmp, 0);

			var length = data.Length - 4;
			if (getLength && data.Length > 7)
			{
				Buffer.BlockCopy(data, 4, tmp, 0, 4);
				length = BitConverter.ToInt32(tmp, 0);
			}

			return (typeFlag, length);
		}

		/// <summary>
		/// Serializes an object into array of bytes
		/// </summary>
		/// <param name="value"></param>
		/// <param name="addFlags"></param>
		/// <returns></returns>
		public static byte[] Serialize(object value, bool addFlags = true)
		{
			var data = CacheUtils.Helper.Serialize(value);
			return addFlags
				? CacheUtils.Helper.Concat(new[] { BitConverter.GetBytes(data.TypeFlag), data.Data })
				: data.Data;
		}

		/// <summary>
		/// Deserializes an object from the array of bytes
		/// </summary>
		/// <param name="data"></param>
		/// <param name="typeFlag"></param>
		/// <param name="start"></param>
		/// <param name="count"></param>
		/// <returns></returns>
		public static object Deserialize(byte[] data, int typeFlag, int start, int count)
			=> CacheUtils.Helper.Deserialize(data, typeFlag, start, count);

		/// <summary>
		/// Deserializes an object from the array of bytes
		/// </summary>
		/// <param name="data"></param>
		/// <param name="start"></param>
		/// <param name="count"></param>
		/// <returns></returns>
		public static object Deserialize(byte[] data, int start, int count)
			=> Helper.Deserialize(data, (int)TypeCode.Object | 0x0100, start, count);

		/// <summary>
		/// Deserializes an object from the array of bytes
		/// </summary>
		/// <param name="data"></param>
		/// <returns></returns>
		public static object Deserialize(byte[] data)
			=> data == null || data.Length < 4
				? null
				: Helper.Deserialize(data, data.GetFlags().TypeFlag, 4, data.Length - 4);

		/// <summary>
		/// Deserializes an object from the array of bytes
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="data"></param>
		/// <returns></returns>
		public static T Deserialize<T>(byte[] data)
		{
			var value = data != null ? Helper.Deserialize(data) : null;
			return value != null && value is T val ? val : default;
		}

		internal static object DeserializeFromFragments(this byte[] data)
		{
			var tmp = new byte[4];
			Buffer.BlockCopy(data, 8, tmp, 0, 4);
			var typeFlag = BitConverter.ToInt32(tmp, 0);
			return Helper.Deserialize(data, typeFlag, 12, data.Length - 12);
		}

		/// <summary>
		/// Gets the first fragment with attached information
		/// </summary>
		/// <param name="fragments"></param>
		/// <returns></returns>
		public static byte[] GetFirstFragment(this List<byte[]> fragments)
			=> CacheUtils.Helper.Concat(new[] { BitConverter.GetBytes(Helper.FlagOfFirstFragmentBlock), BitConverter.GetBytes(fragments.Where(f => f != null).Sum(f => f.Length)), fragments[0] });

		/// <summary>
		/// Gets information of fragments
		/// </summary>
		/// <param name="data"></param>
		/// <returns></returns>
		public static (int Blocks, int Length) GetFragmentsInfo(this byte[] data)
		{
			var info = data.GetFlags(true);
			if (info.TypeFlag == 0 && info.Length == 0)
				return (0, 0);

			var blocks = 0;
			var offset = 0;
			var length = info.Length;
			while (offset < length)
			{
				blocks++;
				offset += Helper.FragmentSize;
			}
			return (blocks, length);
		}

		/// <summary>
		/// Serializes an object to array of bytes using Json.NET BSON Serializer
		/// </summary>
		/// <param name="value"></param>
		/// <returns></returns>
		public static byte[] SerializeBson(object value)
			=> CacheUtils.Helper.SerializeByBson(value);

		/// <summary>
		/// Deserializes an object from an array of bytes using Json.NET BSON Deserializer
		/// </summary>
		/// <param name="value"></param>
		/// <returns></returns>
		public static object DeserializeBson(byte[] value)
			=> CacheUtils.Helper.SerializeByBson(value);

		/// <summary>
		/// Deserializes an object from an array of bytes using Json.NET BSON Deserializer
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="value"></param>
		/// <returns></returns>
		public static T DeserializeBson<T>(byte[] value)
			=> CacheUtils.Helper.DeserializeByBson<T>(value);
		#endregion

		#region Working with logs
		internal static ILogger Logger { get; set; } = Enyim.Caching.Logger.CreateLogger<Cache>();

		internal static void WriteLogs(string region, List<string> logs, Exception ex)
		{
			if (ex != null)
			{
				logs.ForEach(log => Helper.Logger.LogInformation($"<{region}>: {log}"));
				Helper.Logger.LogError(ex, ex.Message);
			}
			else if (Helper.Logger.IsEnabled(LogLevel.Debug))
				logs.ForEach(log => Helper.Logger.LogInformation($"<{region}>: {log}"));
		}

		internal static void WriteLogs(string region, string log, Exception ex)
		{
			if (ex != null)
				Logger.LogError(ex, $"<{region}>: {log}");
			else if (Logger.IsEnabled(LogLevel.Debug))
				Logger.LogInformation(ex, $"<{region}>: {log}");
		}
		#endregion

		#region Working with configurations
		/// <summary>
		/// Gets the configuration for working with Redis
		/// </summary>
		/// <param name="configSection"></param>
		/// <returns></returns>
		public static RedisClientConfiguration GetRedisConfiguration(this CacheConfigurationSectionHandler configSection)
		{
			var configuration = new RedisClientConfiguration();
			if (configSection.Section.SelectNodes("servers/add") is XmlNodeList servers)
				foreach (XmlNode server in servers)
					if ("redis".Equals((server.Attributes["type"]?.Value ?? "Redis").Trim().ToLower()))
					{
						var address = server.Attributes["address"]?.Value ?? "localhost";
						var endpoint = (address.IndexOf(".") > 0 && address.IndexOf(":") > 0) || (address.IndexOf(":") > 0 && address.IndexOf("]:") > 0)
							? ConfigurationHelper.ResolveToEndPoint(address)
							: ConfigurationHelper.ResolveToEndPoint(address, Int32.TryParse(server.Attributes["port"]?.Value ?? "6379", out var port) ? port : 6379);
						configuration.Servers.Add(endpoint as IPEndPoint);
					}

			if (configSection.Section.SelectSingleNode("options") is XmlNode options)
				foreach (XmlAttribute option in options.Attributes)
					if (!string.IsNullOrWhiteSpace(option.Value))
						configuration.Options += (configuration.Options != "" ? "," : "") + option.Name + "=" + option.Value;

			return configuration;
		}

		/// <summary>
		/// Gets the configuration for working with Redis
		/// </summary>
		/// <param name="cacheConfiguration"></param>
		/// <returns></returns>
		public static RedisClientConfiguration GetRedisConfiguration(this ICacheConfiguration cacheConfiguration)
			=> new RedisClientConfiguration
			{
				Servers = cacheConfiguration.Servers.Where(s => s.Type.ToLower().Equals("redis")).Select(s => (s.Address.IndexOf(".") > 0 && s.Address.IndexOf(":") > 0) || (s.Address.IndexOf(":") > 0 && s.Address.IndexOf("]:") > 0) ? ConfigurationHelper.ResolveToEndPoint(s.Address) as IPEndPoint : ConfigurationHelper.ResolveToEndPoint(s.Address, s.Port) as IPEndPoint).ToList(),
				Options = cacheConfiguration.Options
			};

		/// <summary>
		/// Gets the configuration for working with Memcached
		/// </summary>
		/// <param name="configSection"></param>
		/// <param name="loggerFactory"></param>
		/// <returns></returns>
		public static MemcachedClientConfiguration GetMemcachedConfiguration(this CacheConfigurationSectionHandler configSection, ILoggerFactory loggerFactory = null)
			=> new MemcachedClientConfiguration(loggerFactory, configSection);

		/// <summary>
		/// Gets the configuration for working with Memcached
		/// </summary>
		/// <param name="cacheConfiguration"></param>
		/// <param name="loggerFactory"></param>
		/// <returns></returns>
		public static MemcachedClientConfiguration GetMemcachedConfiguration(this ICacheConfiguration cacheConfiguration, ILoggerFactory loggerFactory = null)
		{
			var configuration = new MemcachedClientConfiguration(loggerFactory)
			{
				Protocol = cacheConfiguration.Protocol
			};

			cacheConfiguration.Servers.Where(s => s.Type.ToLower().Equals("memcached"))
				.ToList()
				.ForEach(s => configuration.Servers.Add((s.Address.IndexOf(".") > 0 && s.Address.IndexOf(":") > 0) || (s.Address.IndexOf(":") > 0 && s.Address.IndexOf("]:") > 0) ? ConfigurationHelper.ResolveToEndPoint(s.Address) : ConfigurationHelper.ResolveToEndPoint(s.Address, s.Port)));

			configuration.SocketPool.MaxPoolSize = cacheConfiguration.SocketPool.MaxPoolSize;
			configuration.SocketPool.MinPoolSize = cacheConfiguration.SocketPool.MinPoolSize;
			configuration.SocketPool.ConnectionTimeout = cacheConfiguration.SocketPool.ConnectionTimeout;
			configuration.SocketPool.ReceiveTimeout = cacheConfiguration.SocketPool.ReceiveTimeout;
			configuration.SocketPool.QueueTimeout = cacheConfiguration.SocketPool.QueueTimeout;
			configuration.SocketPool.DeadTimeout = cacheConfiguration.SocketPool.DeadTimeout;
			configuration.SocketPool.FailurePolicyFactory = cacheConfiguration.SocketPool.FailurePolicyFactory;

			configuration.Authentication.Type = cacheConfiguration.Authentication.Type;
			foreach (var kvp in cacheConfiguration.Authentication.Parameters)
				configuration.Authentication.Parameters[kvp.Key] = kvp.Value;

			if (!string.IsNullOrWhiteSpace(cacheConfiguration.KeyTransformer))
				configuration.KeyTransformer = Enyim.Caching.FastActivator.Create(cacheConfiguration.KeyTransformer) as IKeyTransformer;

			if (!string.IsNullOrWhiteSpace(cacheConfiguration.Transcoder))
				configuration.Transcoder = Enyim.Caching.FastActivator.Create(cacheConfiguration.Transcoder) as ITranscoder;

			if (!string.IsNullOrWhiteSpace(cacheConfiguration.NodeLocator))
				configuration.NodeLocator = Type.GetType(cacheConfiguration.NodeLocator);

			return configuration;
		}
		#endregion

	}

	/// <summary>
	/// Presents in-process memory cache
	/// </summary>
	public class MemoryCache : IDisposable
	{
		internal class CacheItem
		{
			public object Value { get; set; }
			public DateTime ExpiresAt { get; set; }
			public CacheItem(object value, DateTime expiresAt)
			{
				this.Value = value;
				this.ExpiresAt = expiresAt;
			}
		}

		internal readonly Action<string> _onUpdateCallback;
		internal readonly Action<string> _onRemoveCallback;
		internal readonly Microsoft.Extensions.Caching.Memory.MemoryCache _cache;
		internal readonly ConcurrentDictionary<string, CacheItem> _storage;
		readonly IDisposable _timer;

		/// <summary>
		/// Creates new an instance of MemoryCache
		/// </summary>
		/// <param name="onUpdateCallback">The action to callback when an item was updated</param>
		/// <param name="onRemoveCallback">The action to callback when an item was removed</param>
		/// <param name="useMemoryCacheExtension">true to use <see cref="Microsoft.Extensions.Caching.Memory.MemoryCache">MemoryCache</see> as L1-Cache object</param>
		/// <param name="loggerFactory">The logger factory for working with logs</param>
		public MemoryCache(Action<string> onUpdateCallback = null, Action<string> onRemoveCallback = null, bool useMemoryCacheExtension = true, ILoggerFactory loggerFactory = null)
		{
			this._onUpdateCallback = onUpdateCallback;
			this._onRemoveCallback = onRemoveCallback;
			var useInternal = !useMemoryCacheExtension;
			if (useMemoryCacheExtension)
				try
				{
					this._cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new MemoryCacheOptions(), loggerFactory);
				}
				catch (Exception ex)
				{
					useInternal = true;
					(loggerFactory ?? Enyim.Caching.Logger.GetLoggerFactory()).CreateLogger<Cache>().LogError(ex, $"Cannot create new instance of MemoryCache => {ex.Message}");
				}
			if (useInternal)
			{
				this._storage = new ConcurrentDictionary<string, CacheItem>(StringComparer.OrdinalIgnoreCase);
				this._timer = System.Reactive.Linq.Observable.Timer(TimeSpan.Zero, TimeSpan.FromSeconds(60)).Subscribe(_ => this._storage.Where(kvp => kvp.Value.ExpiresAt <= DateTime.Now).Select(kvp => kvp.Key).ToList().ForEach(key => this.Remove(key, false)));
			}
		}
			
		/// <summary>
		/// Sets a cache item
		/// </summary>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <param name="validFor"></param>
		/// <param name="fireCallbackHandler"></param>
		/// <returns></returns>
		public bool Set<T>(string key, T value, TimeSpan validFor, bool fireCallbackHandler = true)
		{
			this.Remove(key, false);
			var result = false;
			if (!string.IsNullOrWhiteSpace(key) && value != null)
			{
				result = this._cache != null
					? this._cache.Set(key, value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = validFor }) != null
					: this._storage.TryAdd(key, new CacheItem(value, validFor.Equals(TimeSpan.Zero) ? DateTime.Now.AddYears(10) : DateTime.Now.AddSeconds(validFor.TotalSeconds)));
				if (result && fireCallbackHandler)
					this._onUpdateCallback?.Invoke(key);
			}
			return result;
		}

		/// <summary>
		/// Sets a cache item
		/// </summary>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <param name="expiresAt"></param>
		/// <param name="fireCallbackHandler"></param>
		/// <returns></returns>
		public bool Set<T>(string key, T value, DateTime expiresAt, bool fireCallbackHandler = true)
			=> this.Set(key, value, expiresAt.ToTimeSpan(), fireCallbackHandler);

		/// <summary>
		/// Sets a collection of cache items
		/// </summary>
		/// <param name="items"></param>
		/// <param name="keyPrefix"></param>
		/// <param name="expiresAt"></param>
		/// <param name="fireCallbackHandler"></param>
		/// <returns></returns>
		public bool Set<T>(IDictionary<string, T> items, string keyPrefix, DateTime expiresAt, bool fireCallbackHandler = true)
		{
			var dictionary = items?.Where(kvp => kvp.Key != null).ToDictionary(kvp => (string.IsNullOrWhiteSpace(keyPrefix) ? "" : keyPrefix) + kvp.Key, kvp => kvp.Value) ?? new Dictionary<string, T>();
			foreach (var kvp in dictionary)
				this.Set(kvp.Key, kvp.Value, expiresAt, fireCallbackHandler);
			return items != null && items.Any();
		}

		/// <summary>
		/// Gets a cache item
		/// </summary>
		/// <param name="key"></param>
		/// <returns></returns>
		public object Get(string key)
		{
			object value = null;
			if (!string.IsNullOrWhiteSpace(key))
			{
				if (this._cache != null)
					this._cache.TryGetValue(key, out value);
				else if (this._storage.TryGetValue(key, out var cacheItem) && cacheItem.ExpiresAt > DateTime.Now)
					value = cacheItem.Value;
			}
			return value;
		}

		/// <summary>
		/// Gets a cache item
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="key"></param>
		/// <returns></returns>
		public T Get<T>(string key)
		{
			var value = this.Get(key);
			return value != null && value is T tvalue ? tvalue : default;
		}

		/// <summary>
		/// Gets a collection of cache items
		/// </summary>
		/// <param name="keys"></param>
		/// <returns></returns>
		public IDictionary<string, object> Get(IEnumerable<string> keys)
		{
			var dictionary = keys?.Select(key => new KeyValuePair<string, object>(key, this.Get(key))).Where(kvp => kvp.Key != null && kvp.Value != null).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
			return dictionary != null && keys != null && dictionary.Count > 0 && dictionary.Count == keys.Count() ? dictionary : null;
		}

		/// <summary>
		/// Gets a collection of cache items
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="keys"></param>
		/// <returns></returns>
		public IDictionary<string, T> Get<T>(IEnumerable<string> keys)
			=> this.Get(keys)?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value is T tvalue ? tvalue : default);

		/// <summary>
		/// Removes a cache item
		/// </summary>
		/// <param name="key"></param>
		/// <param name="fireCallbackHandler"></param>
		/// <returns></returns>
		public bool Remove(string key, bool fireCallbackHandler = true)
		{
			var result = false;
			if (!string.IsNullOrWhiteSpace(key))
			{
				this._cache?.Remove(key);
				result = this._cache != null || this._storage.TryRemove(key, out var _);
				if (result && fireCallbackHandler)
					this._onRemoveCallback?.Invoke(key);
			}
			return result;
		}

		/// <summary>
		/// Removes a collection of cache items
		/// </summary>
		/// <param name="keys"></param>
		/// <param name="keyPrefix"></param>
		/// <param name="fireCallbackHandler"></param>
		/// <returns></returns>
		public bool Remove(IEnumerable<string> keys, string keyPrefix, bool fireCallbackHandler = true)
			=> keys != null && !keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => (string.IsNullOrWhiteSpace(keyPrefix) ? "" : keyPrefix) + key).ToList().Select(key => this.Remove(key, fireCallbackHandler)).Any(value => value == false);

		/// <summary>
		/// Checks existing of a cache item
		/// </summary>
		/// <param name="key"></param>
		/// <returns></returns>
		public bool Exists(string key)
			=> this._cache != null
				? this._cache.TryGetValue(key, out var _)
				: this._storage.ContainsKey(key);

		/// <summary>
		/// Clears the cache bag
		/// </summary>
		public void Clear()
		{
			this._cache?.Clear();
			this._storage?.Clear();
		}

		public void Dispose()
		{
			GC.SuppressFinalize(this);
			this._cache?.Dispose();
			this._timer?.Dispose();
		}

		~MemoryCache()
			=> this.Dispose();

		/// <summary>
		/// Gets the collection of keys
		/// </summary>
		public IEnumerable<string> Keys
			=> this._cache != null
				? this._cache.Keys.Select(key => key as string)
				: this._storage.Keys;
	}
}

namespace Microsoft.Extensions.DependencyInjection
{
	public static partial class CachingServiceCollectionExtensions
	{
		/// <summary>
		/// Adds the caching service into the collection of services for using with dependency injection
		/// </summary>
		/// <param name="services"></param>
		/// <param name="setupAction">The action to bind options of 'Cache' section from appsettings.json file</param>
		/// <param name="addInstanceOfIDistributedCache">true to add the cache service as an instance of IDistributedCache</param>
		/// <returns></returns>
		public static IServiceCollection AddCache(this IServiceCollection services, Action<CacheOptions> setupAction, bool addInstanceOfIDistributedCache = true)
		{
			if (setupAction == null)
				throw new ArgumentNullException(nameof(setupAction));

			services.AddOptions().Configure(setupAction);
			services.Add(ServiceDescriptor.Singleton<ICacheConfiguration, CacheConfiguration>());
			services.Add(ServiceDescriptor.Singleton<ICache, Cache>(Cache.GetInstance));
			if (addInstanceOfIDistributedCache)
				services.Add(ServiceDescriptor.Singleton<IDistributedCache, Cache>(Cache.GetInstance));

			return services;
		}
	}
}

namespace Microsoft.AspNetCore.Builder
{
	public static partial class CachingApplicationBuilderExtensions
	{
		/// <summary>
		/// Calls to use the caching service
		/// </summary>
		/// <param name="appBuilder"></param>
		/// <returns></returns>
		public static IApplicationBuilder UseCache(this IApplicationBuilder appBuilder)
		{
			var logger = appBuilder.ApplicationServices.GetService<ILogger<ICache>>();
			try
			{
				var cache = appBuilder.ApplicationServices.GetService<ICache>() as Cache;
				logger.LogInformation($"The caching service was {(cache != null ? "" : "not ")}registered with application service providers{(cache != null ? $" - {cache.Provider}: {cache.Name} ({cache.ExpirationTime} minutes) - L1-Cache: {cache.UseL1Cache}/{cache.PrefetchL1Cache}" : "")}");
			}
			catch (Exception ex)
			{
				logger.LogError(ex, $"Error occurred while collecting information of caching service => {ex.Message}");
			}
			return appBuilder;
		}
	}
}
