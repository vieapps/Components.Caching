#region Related components
using System;
using System.Linq;
using System.Net;
using System.Xml;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Enyim.Caching.Configuration;
using Enyim.Caching.Memcached;
using net.vieapps.Components.Caching;
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
		public static Tuple<int, int> GetFlags(this byte[] data, bool getLength = false)
		{
			if (data == null || data.Length < 4)
				return null;

			var tmp = new byte[4];
			Buffer.BlockCopy(data, 0, tmp, 0, 4);
			var typeFlag = BitConverter.ToInt32(tmp, 0);

			var length = data.Length - 4;
			if (getLength && data.Length > 7)
			{
				Buffer.BlockCopy(data, 4, tmp, 0, 4);
				length = BitConverter.ToInt32(tmp, 0);
			}

			return new Tuple<int, int>(typeFlag, length);
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
				? CacheUtils.Helper.Concat(new[] { BitConverter.GetBytes(data.Item1), data.Item2 })
				: data.Item2;
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
				: Helper.Deserialize(data, data.GetFlags().Item1, 4, data.Length - 4);

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
		public static Tuple<int, int> GetFragmentsInfo(this byte[] data)
		{
			var info = data.GetFlags(true);
			if (info == null)
				return null;

			var blocks = 0;
			var offset = 0;
			var length = info.Item2;
			while (offset < length)
			{
				blocks++;
				offset += Helper.FragmentSize;
			}
			return new Tuple<int, int>(blocks, length);
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
}

namespace Microsoft.Extensions.DependencyInjection
{
	public static partial class CachingServiceCollectionExtensions
	{
		/// <summary>
		/// Adds the <see cref="ICache">VIEApps NGX Caching</see> service into the collection of services for using with dependency injection
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
			services.Add(ServiceDescriptor.Singleton<ICache, Cache>(svcProvider => Cache.GetInstance(svcProvider)));
			if (addInstanceOfIDistributedCache)
				services.Add(ServiceDescriptor.Singleton<IDistributedCache, Cache>(svcProvider => Cache.GetInstance(svcProvider)));

			return services;
		}
	}
}

namespace Microsoft.AspNetCore.Builder
{
	public static partial class CachingApplicationBuilderExtensions
	{
		/// <summary>
		/// Calls to use the <see cref="ICache">VIEApps NGX Caching</see> service
		/// </summary>
		/// <param name="appBuilder"></param>
		/// <returns></returns>
		public static IApplicationBuilder UseCache(this IApplicationBuilder appBuilder)
		{
			try
			{
				appBuilder.ApplicationServices.GetService<ILogger<ICache>>().LogInformation($"The service of VIEApps NGX Caching was{(appBuilder.ApplicationServices.GetService<ICache>() != null ? " " : " not ")}registered with application service providers");
			}
			catch (Exception ex)
			{
				appBuilder.ApplicationServices.GetService<ILogger<ICache>>().LogError(ex, $"Error occurred while collecting information of VIEApps NGX Caching => {ex.Message}");
			}
			return appBuilder;
		}
	}
}
