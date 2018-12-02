#region Related components
using System;
using System.Linq;
using System.Xml;
using System.Net;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Configuration;
using System.Diagnostics;

using Enyim.Reflection;
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
				throw new ArgumentNullException(nameof(configuration));

			this.Provider = configuration.Section.Attributes["provider"]?.Value ?? "Redis";
			this.RegionName = configuration.Section.Attributes["region"]?.Value ?? "VIEApps-NGX-Cache";
			this.ExpirationTime = Convert.ToInt32(configuration.Section.Attributes["expirationTime"]?.Value ?? "30");

			if (configuration.Section.SelectNodes("servers/add") is XmlNodeList servers)
				foreach (XmlNode server in servers)
				{
					var type = server.Attributes["type"]?.Value ?? "Redis";
					var address = server.Attributes["address"]?.Value ?? "localhost";
					var port = Convert.ToInt32(server.Attributes["port"]?.Value ?? (type.ToLower().Equals("redis") ? "6379" : "11211"));
					this.Servers.Add(new CacheServer()
					{
						Address = address,
						Port = port,
						Type = type
					});
				}

			if (configuration.Section.SelectSingleNode("options") is XmlNode options)
				foreach (XmlAttribute option in options.Attributes)
					if (!string.IsNullOrWhiteSpace(option.Value))
						this.Options += (this.Options != "" ? "," : "") + option.Name + "=" + option.Value;

			if (Enum.TryParse(configuration.Section.Attributes["protocol"]?.Value ?? "Binary", out MemcachedProtocol protocol))
				this.Protocol = protocol;

			if (configuration.Section.SelectSingleNode("socketPool") is XmlNode socketpool)
			{
				if (socketpool.Attributes["maxPoolSize"]?.Value != null)
					this.SocketPool.MaxPoolSize = Convert.ToInt32(socketpool.Attributes["maxPoolSize"].Value);
				if (socketpool.Attributes["minPoolSize"]?.Value != null)
					this.SocketPool.MinPoolSize = Convert.ToInt32(socketpool.Attributes["minPoolSize"].Value);
				if (socketpool.Attributes["connectionTimeout"]?.Value != null)
					this.SocketPool.ConnectionTimeout = TimeSpan.Parse(socketpool.Attributes["connectionTimeout"].Value);
				if (socketpool.Attributes["deadTimeout"]?.Value != null)
					this.SocketPool.DeadTimeout = TimeSpan.Parse(socketpool.Attributes["deadTimeout"].Value);
				if (socketpool.Attributes["queueTimeout"]?.Value != null)
					this.SocketPool.QueueTimeout = TimeSpan.Parse(socketpool.Attributes["queueTimeout"].Value);
				if (socketpool.Attributes["receiveTimeout"]?.Value != null)
					this.SocketPool.ReceiveTimeout = TimeSpan.Parse(socketpool.Attributes["receiveTimeout"].Value);
				if (socketpool.Attributes["noDelay"]?.Value != null)
					this.SocketPool.NoDelay = Convert.ToBoolean(socketpool.Attributes["noDelay"].Value);

				var failurePolicy = socketpool.Attributes["failurePolicy"]?.Value;
				if ("throttling" == failurePolicy)
				{
					var failureThreshold = Convert.ToInt32(socketpool.Attributes["failureThreshold"]?.Value ?? "4");
					var resetAfter = TimeSpan.Parse(socketpool.Attributes["resetAfter"]?.Value ?? "00:05:00");
					this.SocketPool.FailurePolicyFactory = new ThrottlingFailurePolicyFactory(failureThreshold, resetAfter);
				}
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

		internal MemcachedClientConfiguration GetMemcachedConfiguration(ILoggerFactory loggerFactory)
		{
			var configuration = new MemcachedClientConfiguration(loggerFactory)
			{
				Protocol = this.Protocol
			};

			this.Servers.Where(s => s.Type.ToLower().Equals("memcached"))
				.ToList()
				.ForEach(s => configuration.Servers.Add(s.Address.IndexOf(":") > 0 ? ConfigurationHelper.ResolveToEndPoint(s.Address) : ConfigurationHelper.ResolveToEndPoint(s.Address, s.Port)));

			configuration.SocketPool.MinPoolSize = this.SocketPool.MinPoolSize;
			configuration.SocketPool.MaxPoolSize = this.SocketPool.MaxPoolSize;
			configuration.SocketPool.ConnectionTimeout = this.SocketPool.ConnectionTimeout;
			configuration.SocketPool.ReceiveTimeout = this.SocketPool.ReceiveTimeout;
			configuration.SocketPool.QueueTimeout = this.SocketPool.QueueTimeout;
			configuration.SocketPool.DeadTimeout = this.SocketPool.DeadTimeout;
			configuration.SocketPool.FailurePolicyFactory = this.SocketPool.FailurePolicyFactory;

			configuration.Authentication.Type = this.Authentication.Type;
			foreach (var kvp in this.Authentication.Parameters)
				configuration.Authentication.Parameters[kvp.Key] = kvp.Value;

			if (!string.IsNullOrWhiteSpace(this.KeyTransformer))
				configuration.KeyTransformer = FastActivator.Create(Type.GetType(this.KeyTransformer)) as IKeyTransformer;

			if (!string.IsNullOrWhiteSpace(this.Transcoder))
				configuration.Transcoder = FastActivator.Create(Type.GetType(this.Transcoder)) as ITranscoder;

			if (!string.IsNullOrWhiteSpace(this.NodeLocator))
				configuration.NodeLocator = Type.GetType(this.NodeLocator);

			return configuration;
		}

		internal RedisClientConfiguration GetRedisConfiguration(ILoggerFactory loggerFactory)
			=> new RedisClientConfiguration
			{
				Servers = this.Servers.Where(s => s.Type.ToLower().Equals("redis")).Select(s => s.Address.IndexOf(":") > 0 ? ConfigurationHelper.ResolveToEndPoint(s.Address) as IPEndPoint : ConfigurationHelper.ResolveToEndPoint(s.Address, s.Port) as IPEndPoint).ToList(),
				Options = this.Options
			};
	}

	// -----------------------------------------------------------

	/// <summary>
	/// Caching options
	/// </summary>
	[Serializable]
	public class CacheOptions : IOptions<CacheOptions>
	{
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
	}

	// -----------------------------------------------------------

	/// <summary>
	/// Redis cache configuration
	/// </summary>
	[Serializable]
	public class RedisClientConfiguration
	{
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
		public List<IPEndPoint> Servers { get; set; } = new List<IPEndPoint>();

		public string Options { get; set; } = "";

		public RedisClientOptions Value => this;
	}

	// -----------------------------------------------------------

	/// <summary>
	/// Configuration section handler of the caching component
	/// </summary>
	public class CacheConfigurationSectionHandler : IConfigurationSectionHandler
	{
		public object Create(object parent, object configContext, XmlNode section)
		{
			this.Section = section;
			return this;
		}

		public XmlNode Section { get; private set; } = null;
	}

	/// <summary>
	/// Configuration section handler of the Redis caching component
	/// </summary>
	public class RedisClientConfigurationSectionHandler : CacheConfigurationSectionHandler { }
}