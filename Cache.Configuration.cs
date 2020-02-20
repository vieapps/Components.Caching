#region Related components
using System;
using System.Linq;
using System.Xml;
using System.Net;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Configuration;
using System.Diagnostics;

using Enyim.Caching.Configuration;
using Enyim.Caching.Memcached;

using StackExchange.Redis;

using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
#endregion

namespace net.vieapps.Components.Caching
{
	/// <summary>
	/// Caching configuration
	/// </summary>
	public interface ICacheConfiguration
	{
		string Provider { get; }

		string RegionName { get; }

		int ExpirationTime { get; }

		IList<CacheServer> Servers { get; }

		string Options { get; }

		MemcachedProtocol Protocol { get; }

		ISocketPoolConfiguration SocketPool { get; }

		IAuthenticationConfiguration Authentication { get; }

		string KeyTransformer { get; }

		string Transcoder { get; }

		string NodeLocator { get; }
	}

	/// <summary>
	/// Caching configuration
	/// </summary>
	[Serializable]
	public class CacheConfiguration : ICacheConfiguration
	{
		public CacheConfiguration() { }

		public string Provider { get; set; } = "Redis";

		public string RegionName { get; set; } = "VIEApps-NGX-Cache";

		public int ExpirationTime { get; set; } = 30;

		public IList<CacheServer> Servers { get; set; } = new List<CacheServer>();

		public string Options { get; set; } = "";

		public MemcachedProtocol Protocol { get; set; } = MemcachedProtocol.Binary;

		public ISocketPoolConfiguration SocketPool { get; set; } = new SocketPoolConfiguration();

		public IAuthenticationConfiguration Authentication { get; set; } = new AuthenticationConfiguration();

		public string KeyTransformer { get; set; } = "";

		public string Transcoder { get; set; } = "";

		public string NodeLocator { get; set; } = "";

		public CacheConfiguration(IOptions<CacheOptions> options)
		{
			if (options == null)
				throw new ArgumentNullException(nameof(options));

			var configuration = options.Value;
			
			this.Provider = configuration.Provider;
			this.RegionName = configuration.RegionName;
			this.ExpirationTime = configuration.ExpirationTime;

			this.Servers = configuration.Servers;

			this.Options = configuration.Options;

			this.Protocol = configuration.Protocol;
			this.SocketPool = configuration.SocketPool;
			this.Authentication = configuration.Authentication;
			this.KeyTransformer = configuration.KeyTransformer;
			this.Transcoder = configuration.Transcoder;
			this.NodeLocator = configuration.NodeLocator;
		}

		public CacheConfiguration(CacheConfigurationSectionHandler configuration)
		{
			if (configuration == null)
				throw new ArgumentNullException(nameof(configuration), "No configuration is found");

			this.Provider = configuration.Section.Attributes["provider"]?.Value ?? "Redis";
			this.RegionName = configuration.Section.Attributes["region"]?.Value ?? "VIEApps-NGX-Cache";
			if (Int32.TryParse(configuration.Section.Attributes["expirationTime"]?.Value ?? "30", out var intValue))
				this.ExpirationTime = intValue;

			if (configuration.Section.SelectNodes("servers/add") is XmlNodeList servers)
				foreach (XmlNode server in servers)
				{
					var type = server.Attributes["type"]?.Value ?? "Redis";
					this.Servers.Add(new CacheServer(server.Attributes["address"]?.Value ?? "localhost", Int32.TryParse(server.Attributes["port"]?.Value ?? (type.ToLower().Equals("redis") ? "6379" : "11211"), out var port) ? port : type.ToLower().Equals("redis") ? 6379 : 11211, type));
				}

			if (configuration.Section.SelectSingleNode("options") is XmlNode options)
				foreach (XmlAttribute option in options.Attributes)
					if (!string.IsNullOrWhiteSpace(option.Value))
						this.Options += (this.Options != "" ? "," : "") + option.Name + "=" + option.Value;

			if (Enum.TryParse(configuration.Section.Attributes["protocol"]?.Value ?? "Binary", out MemcachedProtocol protocol))
				this.Protocol = protocol;

			if (configuration.Section.SelectSingleNode("socketPool") is XmlNode socketpool)
			{
				if (Int32.TryParse(socketpool.Attributes["maxPoolSize"]?.Value, out intValue))
					this.SocketPool.MaxPoolSize = intValue;
				if (Int32.TryParse(socketpool.Attributes["minPoolSize"]?.Value, out intValue))
					this.SocketPool.MinPoolSize = intValue;
				if (TimeSpan.TryParse(socketpool.Attributes["connectionTimeout"]?.Value, out var timespanValue))
					this.SocketPool.ConnectionTimeout = timespanValue;
				if (TimeSpan.TryParse(socketpool.Attributes["deadTimeout"]?.Value, out timespanValue))
					this.SocketPool.DeadTimeout = timespanValue;
				if (TimeSpan.TryParse(socketpool.Attributes["queueTimeout"]?.Value, out timespanValue))
					this.SocketPool.QueueTimeout = timespanValue;
				if (TimeSpan.TryParse(socketpool.Attributes["receiveTimeout"]?.Value, out timespanValue))
					this.SocketPool.ReceiveTimeout = timespanValue;
				if (Boolean.TryParse(socketpool.Attributes["noDelay"]?.Value, out var boolValue))
					this.SocketPool.NoDelay = boolValue;

				if ("throttling" == socketpool.Attributes["failurePolicy"]?.Value)
					this.SocketPool.FailurePolicyFactory = new ThrottlingFailurePolicyFactory(Int32.TryParse(socketpool.Attributes["failureThreshold"]?.Value, out intValue) ? intValue : 4, TimeSpan.TryParse(socketpool.Attributes["resetAfter"]?.Value, out timespanValue) ? timespanValue : TimeSpan.FromSeconds(5));
			}

			if (configuration.Section.SelectSingleNode("authentication") is XmlNode authentication)
				if (authentication.Attributes["type"]?.Value != null)
					try
					{
						this.Authentication.Type = authentication.Attributes["type"].Value;
						if (authentication.Attributes["zone"]?.Value != null)
							this.Authentication.Parameters.Add("zone", authentication.Attributes["zone"].Value);
						if (authentication.Attributes["userName"]?.Value != null)
							this.Authentication.Parameters.Add("userName", authentication.Attributes["userName"].Value);
						if (authentication.Attributes["password"]?.Value != null)
							this.Authentication.Parameters.Add("password", authentication.Attributes["password"].Value);
					}
					catch { }

			if (configuration.Section.SelectSingleNode("keyTransformer") is XmlNode keyTransformer)
				this.KeyTransformer = keyTransformer.Attributes["type"]?.Value;

			if (configuration.Section.SelectSingleNode("transcoder") is XmlNode transcoder)
				this.Transcoder = transcoder.Attributes["type"]?.Value;

			if (configuration.Section.SelectSingleNode("nodeLocator") is XmlNode nodeLocator)
				this.NodeLocator = nodeLocator.Attributes["type"]?.Value;
		}
	}

	// -----------------------------------------------------------

	/// <summary>
	/// Caching options
	/// </summary>
	[Serializable]
	public class CacheOptions : IOptions<CacheOptions>
	{
		public CacheOptions() { }

		public string Provider { get; set; } = "Redis";

		public string RegionName { get; set; } = "VIEApps-NGX-Cache";

		public int ExpirationTime { get; set; } = 30;

		public List<CacheServer> Servers { get; set; } = new List<CacheServer>();

		public string Options { get; set; } = "";

		public MemcachedProtocol Protocol { get; set; } = MemcachedProtocol.Binary;

		public SocketPoolConfiguration SocketPool { get; set; } = new SocketPoolConfiguration();

		public AuthenticationConfiguration Authentication { get; set; } = new AuthenticationConfiguration();

		public string KeyTransformer { get; set; } = "";

		public string Transcoder { get; set; } = "";

		public string NodeLocator { get; set; } = "";

		public CacheOptions Value => this;
	}

	// -----------------------------------------------------------

	/// <summary>
	/// Information of a distributed cache server
	/// </summary>
	[Serializable]
	public class CacheServer
	{
		public string Address { get; set; }

		public int Port { get; set; }

		public string Type { get; set; } = "Redis";

		public CacheServer() { }

		public CacheServer(string address, int port, string type = "Redis")
		{
			this.Address = address;
			this.Port = port;
			this.Type = type;
		}
	}

	// -----------------------------------------------------------

	/// <summary>
	/// Redis cache configuration
	/// </summary>
	[Serializable]
	public class RedisClientConfiguration
	{
		public RedisClientConfiguration() { }

		public List<IPEndPoint> Servers { get; internal set; } = new List<IPEndPoint>();

		public string Options { get; internal set; } = "";
	}

	// -----------------------------------------------------------

	/// <summary>
	/// Redis cache options
	/// </summary>
	[Serializable]
	public class RedisClientOptions : IOptions<RedisClientOptions>
	{
		public RedisClientOptions() { }

		public List<IPEndPoint> Servers { get; set; } = new List<IPEndPoint>();

		public string Options { get; set; } = "";

		public RedisClientOptions Value => this;
	}

	// -----------------------------------------------------------

	/// <summary>
	/// Configuration section handler of the caching component
	/// </summary>
	public class CacheConfigurationSectionHandler : MemcachedClientConfigurationSectionHandler { }
}