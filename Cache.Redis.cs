#region Related components
using System;
using System.Linq;
using System.Xml;
using System.Net;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Configuration;
using System.Diagnostics;

using StackExchange.Redis;

using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

using CacheUtils;
#endregion

namespace net.vieapps.Components.Caching
{
	/// <summary>
	/// Manipulates cached objects in isolated regions with Redis
	/// </summary>
	[DebuggerDisplay("Redis: {Name} ({ExpirationTime} minutes)")]
	public sealed class Redis : ICache, IDisposable
	{
		readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
		readonly bool _storeKeys;

		/// <summary>
		/// Create new instance of Redis
		/// </summary>
		/// <param name="name">The string that presents name of isolated region of the cache</param>
		/// <param name="expirationTime">The number that presents times (in minutes) for caching an item</param>
		/// <param name="storeKeys">true to active store keys of the region (to clear or using with other purposes)</param>
		/// <param name="loggerFactory">The logger factory for working with logs</param>
		public Redis(string name, int expirationTime, bool storeKeys, ILoggerFactory loggerFactory = null)
		{
			// region name
			this.Name = Helper.GetRegionName(name);

			// expiration time
			this.ExpirationTime = expirationTime > 0 ? expirationTime : Helper.ExpirationTime;

			// store keys
			this._storeKeys = storeKeys;

			// register the region
			Task.Run(async () =>
			{
				await Task.Delay(Redis.ConnectionTimeout > 0 ? Redis.ConnectionTimeout + 123 : 5432).ConfigureAwait(false);
				await Redis.RegisterRegionAsync(this.Name).ConfigureAwait(false);
			}).ConfigureAwait(false);
		}

		public void Dispose()
			=> this._lock.Dispose();

		~Redis()
		{
			this.Dispose();
			GC.SuppressFinalize(this);
		}

		#region Get client (singleton)
		static ConnectionMultiplexer _Connection { get; set; } = null;
		static IDatabase _Client { get; set; } = null;
		static int ConnectionTimeout { get; set; }

		internal static IDatabase GetClient(RedisClientConfiguration configuration, ILoggerFactory loggerFactory = null)
		{
			if (Redis._Client == null)
			{
				var connectTimeout = "5000";
				if (configuration.Options.IndexOf("connectTimeout=") > 0)
				{
					connectTimeout = configuration.Options.Substring(configuration.Options.IndexOf("connectTimeout="));
					connectTimeout = connectTimeout.Substring(connectTimeout.IndexOf("=") + 1);
					if (connectTimeout.IndexOf(",") > 0)
						connectTimeout = connectTimeout.Remove(connectTimeout.IndexOf(","));
				}
				Redis.ConnectionTimeout = Int32.TryParse(connectTimeout, out var timeout) ? timeout : 5000;
				var logger = (loggerFactory ?? Enyim.Caching.Logger.GetLoggerFactory()).CreateLogger<Redis>();
				if (Redis._Connection == null)
				{
					var connectionString = "";
					configuration.Servers.ForEach(server => connectionString += (connectionString != "" ? "," : "") + $"{server.Address}:{server.Port}");
					connectionString += string.IsNullOrWhiteSpace(configuration.Options) ? "" : (connectionString != "" ? "," : "") + configuration.Options;
					try
					{
						Redis._Connection = string.IsNullOrWhiteSpace(connectionString) ? null : ConnectionMultiplexer.Connect(connectionString);
						if (logger.IsEnabled(LogLevel.Debug))
							logger.LogDebug($"The redis's connection was established => {connectionString}");
					}
					catch (Exception ex)
					{
						var exception = new ConfigurationErrorsException($"Error occurred while creating the redis's connection [{connectionString}] => {ex.Message}", ex);
						logger.LogError(exception, $"Cannot create new redis's connection");
						throw exception;
					}
				}
				Redis._Client = Redis._Connection.GetDatabase();
				if (logger.IsEnabled(LogLevel.Debug))
					logger.LogDebug($"The redis's instance was created");
			}
			return Redis._Client;
		}

		/// <summary>
		/// Gets the instance of the Redis client
		/// </summary>
		public static IDatabase Client
		{
			get
			{
				if (Redis._Client == null)
				{
					var logger = Enyim.Caching.Logger.GetLoggerFactory().CreateLogger<Redis>();
					if (ConfigurationManager.GetSection("redis") is RedisClientConfigurationSectionHandler redisConfigurationSection)
					{
						Redis.GetClient(redisConfigurationSection.GetRedisConfiguration(), Enyim.Caching.Logger.GetLoggerFactory());
						if (logger.IsEnabled(LogLevel.Debug))
							logger.LogDebug("The Redis's instance was created with stand-alone configuration (app.config/web.config) at the section named 'redis'");
					}
					else if (ConfigurationManager.GetSection("cache") is CacheConfigurationSectionHandler cacheConfigurationSection)
					{
						Redis.GetClient(cacheConfigurationSection.GetRedisConfiguration(), Enyim.Caching.Logger.GetLoggerFactory());
						if (logger.IsEnabled(LogLevel.Debug))
							logger.LogDebug("The Redis's instance was created with stand-alone configuration (app.config/web.config) at the section named 'cache'");
					}
					else
						throw new ConfigurationErrorsException("No configuration section is found, the configuration file (app.config/web.config) must have a section named 'redis' or 'cache'.");
				}
				return Redis._Client;
			}
		}
		#endregion

		#region Keys
		void _UpdateKey(string key)
		{
			if (this._storeKeys)
				Redis.Client.UpdateSetMembers(this._RegionKey, key);
		}

		Task _UpdateKeyAsync(string key, CancellationToken cancellationToken = default(CancellationToken))
			=> this._storeKeys
				? Redis.Client.UpdateSetMembersAsync(this._RegionKey, key, cancellationToken)
				: Task.CompletedTask;

		void _UpdateKeys(IEnumerable<string> keys, string keyPrefix = null)
		{
			if (this._storeKeys && keys != null && keys.Count() > 0)
				Redis.Client.UpdateSetMembers(this._RegionKey, keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => (string.IsNullOrWhiteSpace(keyPrefix) ? "" : keyPrefix) + key).ToArray());
		}

		Task _UpdateKeysAsync(IEnumerable<string> keys, string keyPrefix = null, CancellationToken cancellationToken = default(CancellationToken))
			=> this._storeKeys && keys != null && keys.Count() > 0
				? Redis.Client.UpdateSetMembersAsync(this._RegionKey, keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => (string.IsNullOrWhiteSpace(keyPrefix) ? "" : keyPrefix) + key).ToArray(), cancellationToken)
				: Task.CompletedTask;

		void _RemoveKey(string key)
		{
			if (this._storeKeys)
				Redis.Client.RemoveSetMembers(this._RegionKey, key);
		}

		Task _RemoveKeyAsync(string key, CancellationToken cancellationToken = default(CancellationToken))
			=> this._storeKeys
				? Redis.Client.RemoveSetMembersAsync(this._RegionKey, key, cancellationToken)
				: Task.CompletedTask;

		HashSet<string> _GetKeys()
			=> Redis.Client.GetSetMembers(this._RegionKey);

		Task<HashSet<string>> _GetKeysAsync(CancellationToken cancellationToken = default(CancellationToken))
			=> Redis.Client.GetSetMembersAsync(this._RegionKey, cancellationToken);
		#endregion

		#region Set
		bool _Set(string key, object value, TimeSpan validFor)
		{
			var success = false;
			if (!string.IsNullOrWhiteSpace(key) && value != null)
				try
				{
					success = Redis.Client.Set(this._GetKey(key), value, validFor);
				}
				catch (ArgumentException)
				{
					throw;
				}
				catch (Exception ex)
				{
					Helper.WriteLogs(this.Name, $"Error occurred while updating an object into cache [{value.GetType().ToString()}#{key}]", ex);
				}

			if (success)
				this._UpdateKey(key);

			return success;
		}

		bool _Set(string key, object value, int expirationTime = 0)
			=> this._Set(key, value, TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime));

		bool _Set(string key, object value, DateTime expiresAt)
			=> this._Set(key, value, expiresAt.ToTimeSpan());

		async Task<bool> _SetAsync(string key, object value, TimeSpan validFor, CancellationToken cancellationToken = default(CancellationToken))
		{
			var success = false;
			if (!string.IsNullOrWhiteSpace(key) && value != null)
				try
				{
					success = await Redis.Client.SetAsync(this._GetKey(key), value, validFor, cancellationToken).ConfigureAwait(false);
				}
				catch (ArgumentException)
				{
					throw;
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception ex)
				{
					Helper.WriteLogs(this.Name, $"Error occurred while updating an object into cache [{value.GetType().ToString()}#{key}]", ex);
				}

			if (success)
				await this._UpdateKeyAsync(key, cancellationToken).ConfigureAwait(false);

			return success;
		}

		Task<bool> _SetAsync(string key, object value, int expirationTime = 0, CancellationToken cancellationToken = default(CancellationToken))
			=> this._SetAsync(key, value, TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime), cancellationToken);

		Task<bool> _SetAsync(string key, object value, DateTime expiresAt, CancellationToken cancellationToken = default(CancellationToken))
			=> this._SetAsync(key, value, expiresAt.ToTimeSpan(), cancellationToken);
		#endregion

		#region Set (Multiple)
		void _Set<T>(IDictionary<string, T> items, string keyPrefix = null, int expirationTime = 0)
		{
			Redis.Client.Set(
				items != null
					? items.Where(kvp => kvp.Key != null).ToDictionary(kvp => this._GetKey((string.IsNullOrWhiteSpace(keyPrefix) ? "" : keyPrefix) + kvp.Key), kvp => kvp.Value)
					: new Dictionary<string, T>(),
				TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime)
			);
			this._UpdateKeys(items?.Keys, keyPrefix);
		}

		void _Set(IDictionary<string, object> items, string keyPrefix = null, int expirationTime = 0)
			=> this._Set<object>(items, keyPrefix, expirationTime);

		Task _SetAsync<T>(IDictionary<string, T> items, string keyPrefix = null, int expirationTime = 0, CancellationToken cancellationToken = default(CancellationToken))
			=> Task.WhenAll(
				Redis.Client.SetAsync(
					items != null
						? items.Where(kvp => kvp.Key != null).ToDictionary(kvp => this._GetKey((string.IsNullOrWhiteSpace(keyPrefix) ? "" : keyPrefix) + kvp.Key), kvp => kvp.Value)
						: new Dictionary<string, T>(),
					TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime),
					cancellationToken
				),
				this._UpdateKeysAsync(items?.Keys, keyPrefix, cancellationToken)
			);

		Task _SetAsync(IDictionary<string, object> items, string keyPrefix = null, int expirationTime = 0, CancellationToken cancellationToken = default(CancellationToken))
			=> this._SetAsync<object>(items, keyPrefix, expirationTime, cancellationToken);
		#endregion

		#region Set (Fragment)
		bool _SetFragments(string key, List<byte[]> fragments, int expirationTime = 0)
		{
			var validFor = TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime);
			var success = fragments != null && fragments.Count > 0
				? Redis.Client.Set(this._GetKey(key), fragments.GetFirstFragment(), validFor)
				: false;

			if (success)
			{
				this._UpdateKey(key);
				if (fragments.Count > 1)
				{
					var items = new Dictionary<string, byte[]>();
					for (var index = 1; index < fragments.Count; index++)
						items[this._GetKey(this._GetFragmentKey(key, index))] = fragments[index];
					Redis.Client.Set(items, validFor);
				}
			}

			return success;
		}

		async Task<bool> _SetFragmentsAsync(string key, List<byte[]> fragments, int expirationTime = 0, CancellationToken cancellationToken = default(CancellationToken))
		{
			var validFor = TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime);
			var success = fragments != null && fragments.Count > 0
				? await Redis.Client.SetAsync(this._GetKey(key), fragments.GetFirstFragment(), validFor, cancellationToken).ConfigureAwait(false)
				: false;

			if (success)
			{
				var tasks = new List<Task> { this._UpdateKeyAsync(key, cancellationToken) };
				if (fragments.Count > 1)
				{
					var items = new Dictionary<string, byte[]>();
					for (var index = 1; index < fragments.Count; index++)
						items[this._GetKey(this._GetFragmentKey(key, index))] = fragments[index];
					tasks.Add(Redis.Client.SetAsync(items, validFor, cancellationToken));
				}
				await Task.WhenAll(tasks);
			}

			return success;
		}

		bool _SetAsFragments(string key, object value, int expirationTime = 0)
			=> string.IsNullOrWhiteSpace(key) || value == null
				? false
				: this._SetFragments(key, Helper.Serialize(value).Split(), expirationTime);

		Task<bool> _SetAsFragmentsAsync(string key, object value, int expirationTime = 0, CancellationToken cancellationToken = default(CancellationToken))
			=> string.IsNullOrWhiteSpace(key) || value == null
				? Task.FromResult(false)
				: this._SetFragmentsAsync(key, Helper.Serialize(value).Split(), expirationTime, cancellationToken);
		#endregion

		#region Add
		bool _Add(string key, object value, TimeSpan validFor)
		{
			var success = false;
			if (!string.IsNullOrWhiteSpace(key) && value != null)
				try
				{
					success = Redis.Client.Add(this._GetKey(key), value, validFor);
				}
				catch (ArgumentException)
				{
					throw;
				}
				catch (Exception ex)
				{
					Helper.WriteLogs(this.Name, $"Error occurred while updating an object into cache [{value.GetType().ToString()}#{key}]", ex);
				}

			if (success)
				this._UpdateKey(key);

			return success;
		}

		bool _Add(string key, object value, int expirationTime = 0)
			=> this._Add(key, value, TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime));

		bool _Add(string key, object value, DateTime expiresAt)
			=> this._Add(key, value, expiresAt.ToTimeSpan());

		async Task<bool> _AddAsync(string key, object value, TimeSpan validFor, CancellationToken cancellationToken = default(CancellationToken))
		{
			var success = false;
			if (!string.IsNullOrWhiteSpace(key) && value != null)
				try
				{
					success = await Redis.Client.AddAsync(this._GetKey(key), value, validFor, cancellationToken).ConfigureAwait(false);
				}
				catch (ArgumentException)
				{
					throw;
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception ex)
				{
					Helper.WriteLogs(this.Name, $"Error occurred while updating an object into cache [{value.GetType().ToString()}#{key}]", ex);
				}

			if (success)
				await this._UpdateKeyAsync(key, cancellationToken).ConfigureAwait(false);

			return success;
		}

		Task<bool> _AddAsync(string key, object value, int expirationTime = 0, CancellationToken cancellationToken = default(CancellationToken))
			=> this._AddAsync(key, value, TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime), cancellationToken);

		Task<bool> _AddAsync(string key, object value, DateTime expiresAt, CancellationToken cancellationToken = default(CancellationToken))
			=> this._AddAsync(key, value, expiresAt.ToTimeSpan(), cancellationToken);
		#endregion

		#region Replace
		bool _Replace(string key, object value, TimeSpan validFor)
		{
			var success = false;
			if (!string.IsNullOrWhiteSpace(key) && value != null)
				try
				{
					success = Redis.Client.Replace(this._GetKey(key), value, validFor);
				}
				catch (ArgumentException)
				{
					throw;
				}
				catch (Exception ex)
				{
					Helper.WriteLogs(this.Name, $"Error occurred while updating an object into cache [{value.GetType().ToString()}#{key}]", ex);
				}

			if (success)
				this._UpdateKey(key);

			return success;
		}

		bool _Replace(string key, object value, int expirationTime = 0)
			=> this._Replace(key, value, TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime));

		bool _Replace(string key, object value, DateTime expiresAt)
			=> this._Replace(key, value, expiresAt.ToTimeSpan());

		async Task<bool> _ReplaceAsync(string key, object value, TimeSpan validFor, CancellationToken cancellationToken = default(CancellationToken))
		{
			var success = false;
			if (!string.IsNullOrWhiteSpace(key) && value != null)
				try
				{
					success = await Redis.Client.ReplaceAsync(this._GetKey(key), value, validFor, cancellationToken).ConfigureAwait(false);
				}
				catch (ArgumentException)
				{
					throw;
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception ex)
				{
					Helper.WriteLogs(this.Name, $"Error occurred while updating an object into cache [{value.GetType().ToString()}#{key}]", ex);
				}

			if (success)
				await this._UpdateKeyAsync(key, cancellationToken).ConfigureAwait(false);

			return success;
		}

		Task<bool> _ReplaceAsync(string key, object value, int expirationTime = 0, CancellationToken cancellationToken = default(CancellationToken))
			=> this._ReplaceAsync(key, value, TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime), cancellationToken);

		Task<bool> _ReplaceAsync(string key, object value, DateTime expiresAt, CancellationToken cancellationToken = default(CancellationToken))
			=> this._ReplaceAsync(key, value, expiresAt.ToTimeSpan(), cancellationToken);
		#endregion

		#region Get
		object _Get(string key, bool autoGetFragments = true)
		{
			if (string.IsNullOrWhiteSpace(key))
				throw new ArgumentNullException(key);

			object value = null;
			try
			{
				value = Redis.Client.Get(this._GetKey(key), false);
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this.Name, $"Error occurred while fetching an object from cache storage [{key}]", ex);
			}

			if (value != null && (value as byte[]).Length > 7)
			{
				if (autoGetFragments && Helper.GetFlags(value as byte[]).Item1.Equals(Helper.FlagOfFirstFragmentBlock))
					try
					{
						value = this._GetFromFragments(key, value as byte[]);
					}
					catch (Exception ex)
					{
						Helper.WriteLogs(this.Name, $"Error occurred while fetching an objects' fragments from cache storage [{key}]", ex);
						value = null;
					}
				else
					try
					{
						value = Helper.Deserialize(value as byte[]);
					}
					catch (Exception ex)
					{
						Helper.WriteLogs(this.Name, $"Error occurred while fetching an object from cache storage [{key}]", ex);
						value = null;
					}
			}

			return value;
		}

		async Task<object> _GetAsync(string key, bool autoGetFragments = true, CancellationToken cancellationToken = default(CancellationToken))
		{
			if (string.IsNullOrWhiteSpace(key))
				throw new ArgumentNullException(key);

			object value = null;
			try
			{
				value = await Redis.Client.GetAsync(this._GetKey(key), false, cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this.Name, $"Error occurred while fetching an object from cache storage [{key}]", ex);
			}

			if (value != null && (value as byte[]).Length > 7)
			{
				if (autoGetFragments && Helper.GetFlags(value as byte[]).Item1.Equals(Helper.FlagOfFirstFragmentBlock))
					try
					{
						value = await this._GetFromFragmentsAsync(key, value as byte[], cancellationToken).ConfigureAwait(false);
					}
					catch (OperationCanceledException)
					{
						throw;
					}
					catch (Exception ex)
					{
						Helper.WriteLogs(this.Name, $"Error occurred while fetching an objects' fragments from cache storage [{key}]", ex);
						value = null;
					}
				else
					try
					{
						value = Helper.Deserialize(value as byte[]);
					}
					catch (Exception ex)
					{
						Helper.WriteLogs(this.Name, $"Error occurred while fetching an object from cache storage [{key}]", ex);
						value = null;
					}
			}

			return value;
		}
		#endregion

		#region Get (Multiple)
		IDictionary<string, object> _Get(IEnumerable<string> keys)
		{
			if (keys == null)
				return null;

			IDictionary<string, object> items = null;
			try
			{
				items = Redis.Client.Get(keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => this._GetKey(key)));
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this.Name, "Error occurred while fetch a collection of objects from cache storage", ex);
			}

			var objects = items?.ToDictionary(kvp => kvp.Key.Substring(this.Name.Length + 1), kvp => kvp.Value);
			return objects != null && objects.Count > 0
				? objects
				: null;
		}

		IDictionary<string, T> _Get<T>(IEnumerable<string> keys)
		{
			if (keys == null)
				return null;

			IDictionary<string, T> items = null;
			try
			{
				items = Redis.Client.Get<T>(keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => this._GetKey(key)));
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this.Name, "Error occurred while fetch a collection of objects from cache storage", ex);
			}

			var objects = items?.ToDictionary(kvp => kvp.Key.Substring(this.Name.Length + 1), kvp => kvp.Value);
			return objects != null && objects.Count > 0
				? objects
				: null;
		}

		async Task<IDictionary<string, object>> _GetAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default(CancellationToken))
		{
			if (keys == null)
				return null;

			IDictionary<string, object> items = null;
			try
			{
				items = await Redis.Client.GetAsync(keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => this._GetKey(key)), cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this.Name, "Error occurred while fetch a collection of objects from cache storage", ex);
			}

			var objects = items?.ToDictionary(kvp => kvp.Key.Substring(this.Name.Length + 1), kvp => kvp.Value);
			return objects != null && objects.Count > 0
				? objects
				: null;
		}

		async Task<IDictionary<string, T>> _GetAsync<T>(IEnumerable<string> keys, CancellationToken cancellationToken = default(CancellationToken))
		{
			if (keys == null)
				return null;

			IDictionary<string, T> items = null;
			try
			{
				items = await Redis.Client.GetAsync<T>(keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => this._GetKey(key)), cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this.Name, "Error occurred while fetch a collection of objects from cache storage", ex);
			}

			var objects = items?.ToDictionary(kvp => kvp.Key.Substring(this.Name.Length), kvp => kvp.Value);
			return objects != null && objects.Count > 0
				? objects
				: null;
		}
		#endregion

		#region Get (Fragment)
		Tuple<int, int> _GetFragments(string key)
			=> Helper.GetFragmentsInfo(this._Get(key, false) as byte[]);

		async Task<Tuple<int, int>> _GetFragmentsAsync(string key, CancellationToken cancellationToken = default(CancellationToken))
			=> Helper.GetFragmentsInfo(await this._GetAsync(key, false, cancellationToken).ConfigureAwait(false) as byte[]);

		List<byte[]> _GetAsFragments(string key, List<int> indexes)
		{
			var fragments = string.IsNullOrWhiteSpace(key) || indexes == null || indexes.Count < 1
				? null
				: Redis.Client.Get(indexes.Select(index => this._GetKey(this._GetFragmentKey(key, index))), false);
			return fragments != null
				? fragments.OrderBy(kvp => kvp.Key).Select(kvp => kvp.Value as byte[]).ToList()
				: new List<byte[]>();
		}

		async Task<List<byte[]>> _GetAsFragmentsAsync(string key, List<int> indexes, CancellationToken cancellationToken = default(CancellationToken))
		{
			var fragments = string.IsNullOrWhiteSpace(key) || indexes == null || indexes.Count < 1
				? null
				: await Redis.Client.GetAsync(indexes.Select(index => this._GetKey(this._GetFragmentKey(key, index))), false, cancellationToken).ConfigureAwait(false);
			return fragments != null
				? fragments.OrderBy(kvp => kvp.Key).Select(kvp => kvp.Value as byte[]).ToList()
				: new List<byte[]>();
		}

		List<byte[]> _GetAsFragments(string key, params int[] indexes)
			=> string.IsNullOrWhiteSpace(key) || indexes == null || indexes.Length < 1
				? null
				: this._GetAsFragments(key, indexes.ToList());

		Task<List<byte[]>> _GetAsFragmentsAsync(string key, CancellationToken cancellationToken = default(CancellationToken), params int[] indexes)
			=> string.IsNullOrWhiteSpace(key) || indexes == null || indexes.Length < 1
				? Task.FromResult<List<byte[]>>(null)
				: this._GetAsFragmentsAsync(key, indexes.ToList(), cancellationToken);

		object _GetFromFragments(string key, byte[] firstBlock)
		{
			try
			{
				var info = firstBlock.GetFragmentsInfo();
				return firstBlock.Combine(info.Item1 > 1 ? this._GetAsFragments(key, Enumerable.Range(1, info.Item1 - 1).ToList()) : new List<byte[]>()).DeserializeFromFragments();
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this.Name, $"Error occurred while serializing an object from fragmented data [{key}]", ex);
				return null;
			}
		}

		async Task<object> _GetFromFragmentsAsync(string key, byte[] firstBlock, CancellationToken cancellationToken = default(CancellationToken))
		{
			try
			{
				var info = firstBlock.GetFragmentsInfo();
				return firstBlock.Combine(info.Item1 > 1 ? await this._GetAsFragmentsAsync(key, Enumerable.Range(1, info.Item1 - 1).ToList(), cancellationToken).ConfigureAwait(false) : new List<byte[]>()).DeserializeFromFragments();
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this.Name, $"Error occurred while serializing an object from fragmented data [{key}]", ex);
				return null;
			}
		}
		#endregion

		#region Remove
		bool _Remove(string key)
		{
			var success = false;
			if (!string.IsNullOrWhiteSpace(key))
				try
				{
					success = Redis.Client.Remove(this._GetKey(key));
				}
				catch (Exception ex)
				{
					Helper.WriteLogs(this.Name, $"Error occurred while removing an object from cache storage [{key}]", ex);
				}

			if (success)
				this._RemoveKey(key);

			return success;
		}

		async Task<bool> _RemoveAsync(string key, CancellationToken cancellationToken = default(CancellationToken))
		{
			var success = false;
			if (!string.IsNullOrWhiteSpace(key))
				try
				{
					success = await Redis.Client.RemoveAsync(this._GetKey(key), cancellationToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception ex)
				{
					Helper.WriteLogs(this.Name, $"Error occurred while removing an object from cache storage [{key}]", ex);
				}

			if (success)
				await this._RemoveKeyAsync(key, cancellationToken).ConfigureAwait(false);

			return success;
		}
		#endregion

		#region Remove (Multiple)
		void _Remove(IEnumerable<string> keys, string keyPrefix = null)
			=> keys?.Where(key => !string.IsNullOrWhiteSpace(key)).ToList().ForEach(key => this._Remove((string.IsNullOrWhiteSpace(keyPrefix) ? "" : keyPrefix) + key));

		Task _RemoveAsync(IEnumerable<string> keys, string keyPrefix = null, CancellationToken cancellationToken = default(CancellationToken))
			=> Task.WhenAll(keys?.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => this._RemoveAsync((string.IsNullOrWhiteSpace(keyPrefix) ? "" : keyPrefix) + key, cancellationToken)));
		#endregion

		#region Remove (Fragment)
		void _RemoveFragments(string key, int max = 100)
			=> this._Remove(this._GetFragmentKeys(key, max), null);

		Task _RemoveFragmentsAsync(string key, int max = 100, CancellationToken cancellationToken = default(CancellationToken))
			=> this._RemoveAsync(this._GetFragmentKeys(key, max), null, cancellationToken);
		#endregion

		#region Clear
		void _Clear()
		{
			this._lock.Wait();
			try
			{
				this._Remove(this._GetKeys());
				Redis.Client.Remove(this._RegionKey);
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this.Name, $"Error occurred while cleaning => {ex.Message}", ex);
			}
			finally
			{
				this._lock.Release();
			}
		}

		async Task _ClearAsync(CancellationToken cancellationToken = default(CancellationToken))
		{
			await this._lock.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				var keys = await this._GetKeysAsync(cancellationToken).ConfigureAwait(false);
				await Task.WhenAll(
					this._RemoveAsync(keys, null, cancellationToken),
					Redis.Client.RemoveAsync(this._RegionKey, cancellationToken)
				).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this.Name, $"Error occurred while cleaning => {ex.Message}", ex);
			}
			finally
			{
				this._lock.Release();
			}
		}
		#endregion

		// -----------------------------------------------------

		#region [Helper]
		string _GetKey(string key) => Helper.GetCacheKey(this.Name, key);

		string _GetFragmentKey(string key, int index) => Helper.GetFragmentKey(key, index);

		List<string> _GetFragmentKeys(string key, int max) => Helper.GetFragmentKeys(key, max);

		string _RegionKey => this._GetKey("<Keys-Of-Region>");
		#endregion

		#region [Static]
		/// <summary>
		/// Gets the collection of all registered regions (in distributed cache)
		/// </summary>
		public static HashSet<string> GetRegions()
			=> Redis.Client.GetSetMembers(Helper.RegionsKey);

		/// <summary>
		/// Gets the collection of all registered regions (in distributed cache)
		/// </summary>
		public static Task<HashSet<string>> GetRegionsAsync(CancellationToken cancellationToken = default(CancellationToken))
			=> Redis.Client.GetSetMembersAsync(Helper.RegionsKey, cancellationToken);

		static async Task RegisterRegionAsync(string name, CancellationToken cancellationToken = default(CancellationToken))
		{
			try
			{
				await Redis.Client.UpdateSetMembersAsync(Helper.RegionsKey, name, cancellationToken).ConfigureAwait(false);
			}
			catch { }
		}
		#endregion

		// -----------------------------------------------------

		#region [Public] Properties
		/// <summary>
		/// Gets the name of the isolated region
		/// </summary>
		public string Name { get; }

		/// <summary>
		/// Gets the expiration time (in minutes)
		/// </summary>
		public int ExpirationTime { get; }

		/// <summary>
		/// Gets the collection of keys
		/// </summary>
		public HashSet<string> Keys => this._GetKeys();
		#endregion

		#region [Public] Keys
		/// <summary>
		/// Gets the collection of keys that associates with the cached items
		/// </summary>
		public HashSet<string> GetKeys() => this._GetKeys();

		/// <summary>
		/// Gets the collection of keys that associates with the cached items
		/// </summary>
		public Task<HashSet<string>> GetKeysAsync(CancellationToken cancellationToken = default(CancellationToken)) => this._GetKeysAsync(cancellationToken);
		#endregion

		#region [Public] Set
		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Set(string key, object value, int expirationTime = 0) => this._Set(key, value, expirationTime);

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Set(string key, object value, TimeSpan validFor) => this._Set(key, value, validFor);

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Set(string key, object value, DateTime expiresAt) => this._Set(key, value, expiresAt);

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetAsync(string key, object value, int expirationTime = 0, CancellationToken cancellationToken = default(CancellationToken)) => this._SetAsync(key, value, expirationTime, cancellationToken);

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetAsync(string key, object value, TimeSpan validFor, CancellationToken cancellationToken = default(CancellationToken)) => this._SetAsync(key, value, validFor, cancellationToken);

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetAsync(string key, object value, DateTime expiresAt, CancellationToken cancellationToken = default(CancellationToken)) => this._SetAsync(key, value, expiresAt, cancellationToken);
		#endregion

		#region [Public] Set (Multiple)
		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		public void Set(IDictionary<string, object> items, string keyPrefix = null, int expirationTime = 0) => this._Set(items, keyPrefix, expirationTime);

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <typeparam name="T">The type for casting the cached item</typeparam>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		public void Set<T>(IDictionary<string, T> items, string keyPrefix = null, int expirationTime = 0) => this._Set<T>(items, keyPrefix, expirationTime);

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		public Task SetAsync(IDictionary<string, object> items, string keyPrefix = null, int expirationTime = 0, CancellationToken cancellationToken = default(CancellationToken)) => this._SetAsync(items, keyPrefix, expirationTime, cancellationToken);

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <typeparam name="T">The type for casting the cached item</typeparam>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		public Task SetAsync<T>(IDictionary<string, T> items, string keyPrefix = null, int expirationTime = 0, CancellationToken cancellationToken = default(CancellationToken)) => this._SetAsync<T>(items, keyPrefix, expirationTime, cancellationToken);
		#endregion

		#region [Public] Set (Fragment)
		/// <summary>
		/// Adds an item (as fragments) into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="fragments">The collection that contains all fragments (object that serialized as binary - array bytes)</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool SetFragments(string key, List<byte[]> fragments, int expirationTime = 0) => this._SetFragments(key, fragments, expirationTime);

		/// <summary>
		/// Adds an item (as fragments) into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="fragments">The collection that contains all fragments (object that serialized as binary - array bytes)</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetFragmentsAsync(string key, List<byte[]> fragments, int expirationTime = 0, CancellationToken cancellationToken = default(CancellationToken)) => this._SetFragmentsAsync(key, fragments, expirationTime, cancellationToken);

		/// <summary>
		/// Serializes object into array of bytes, splits into one or more fragments and updates into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool SetAsFragments(string key, object value, int expirationTime = 0) => this._SetAsFragments(key, value, expirationTime);

		/// <summary>
		/// Serializes object into array of bytes, splits into one or more fragments and updates into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetAsFragmentsAsync(string key, object value, int expirationTime = 0, CancellationToken cancellationToken = default(CancellationToken)) => this._SetAsFragmentsAsync(key, value, expirationTime, cancellationToken);
		#endregion

		#region [Public] Add
		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Add(string key, object value, int expirationTime = 0) => this._Add(key, value, expirationTime);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Add(string key, object value, TimeSpan validFor) => this._Add(key, value, validFor);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Add(string key, object value, DateTime expiresAt) => this._Add(key, value, expiresAt);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> AddAsync(string key, object value, int expirationTime = 0, CancellationToken cancellationToken = default(CancellationToken)) => this._AddAsync(key, value, expirationTime, cancellationToken);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> AddAsync(string key, object value, TimeSpan validFor, CancellationToken cancellationToken = default(CancellationToken)) => this._AddAsync(key, value, validFor, cancellationToken);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> AddAsync(string key, object value, DateTime expiresAt, CancellationToken cancellationToken = default(CancellationToken)) => this._AddAsync(key, value, expiresAt, cancellationToken);
		#endregion

		#region [Public] Replace
		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Replace(string key, object value, int expirationTime = 0) => this._Replace(key, value, expirationTime);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Replace(string key, object value, TimeSpan validFor) => this._Replace(key, value, validFor);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Replace(string key, object value, DateTime expiresAt) => this._Replace(key, value, expiresAt);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> ReplaceAsync(string key, object value, int expirationTime = 0, CancellationToken cancellationToken = default(CancellationToken)) => this._ReplaceAsync(key, value, expirationTime, cancellationToken);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> ReplaceAsync(string key, object value, TimeSpan validFor, CancellationToken cancellationToken = default(CancellationToken)) => this._ReplaceAsync(key, value, validFor, cancellationToken);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> ReplaceAsync(string key, object value, DateTime expiresAt, CancellationToken cancellationToken = default(CancellationToken)) => this._ReplaceAsync(key, value, expiresAt, cancellationToken);
		#endregion

		#region [Public] Get
		/// <summary>
		/// Retreives a cached item
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to retreive</param>
		/// <returns>The retrieved cache item, or a null reference if the key is not found</returns>
		public object Get(string key) => this._Get(key, true);

		/// <summary>
		/// Retreives a cached item
		/// </summary>
		/// <typeparam name="T">The type for casting the cached item</typeparam>
		/// <param name="key">The string that presents key of cached item need to retreive</param>
		/// <returns>The retrieved cache item, or a null reference if the key is not found</returns>
		public T Get<T>(string key)
		{
			var @object = this.Get(key);
			return @object != null && @object is T
				? (T)@object
				: default(T);
		}

		/// <summary>
		/// Retreives a cached item
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to retreive</param>
		/// <returns>The retrieved cache item, or a null reference if the key is not found</returns>
		public Task<object> GetAsync(string key, CancellationToken cancellationToken = default(CancellationToken)) => this._GetAsync(key, true, cancellationToken);

		/// <summary>
		/// Retreives a cached item
		/// </summary>
		/// <typeparam name="T">The type for casting the cached item</typeparam>
		/// <param name="key">The string that presents key of cached item need to retreive</param>
		/// <returns>The retrieved cache item, or a null reference if the key is not found</returns>
		public async Task<T> GetAsync<T>(string key, CancellationToken cancellationToken = default(CancellationToken))
		{
			var @object = await this.GetAsync(key, cancellationToken).ConfigureAwait(false);
			return @object != null && @object is T
				? (T)@object
				: default(T);
		}
		#endregion

		#region [Public] Get (Multiple)
		/// <summary>
		/// Retreives a collection of cached items
		/// </summary>
		/// <param name="keys">The collection of items' keys</param>
		/// <returns>The collection of cache items</returns>
		public IDictionary<string, object> Get(IEnumerable<string> keys) => this._Get(keys);

		/// <summary>
		/// Retreives a collection of cached items
		/// </summary>
		/// <param name="keys">The collection of items' keys</param>
		/// <returns>The collection of cache items</returns>
		public Task<IDictionary<string, object>> GetAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default(CancellationToken)) => this._GetAsync(keys, cancellationToken);

		/// <summary>
		/// Retreives a collection of cached items
		/// </summary>
		/// <param name="keys">The collection of items' keys</param>
		/// <returns>The collection of cache items</returns>
		public IDictionary<string, T> Get<T>(IEnumerable<string> keys) => this._Get<T>(keys);

		/// <summary>
		/// Retreives a collection of cached items
		/// </summary>
		/// <param name="keys">The collection of items' keys</param>
		/// <returns>The collection of cache items</returns>
		public Task<IDictionary<string, T>> GetAsync<T>(IEnumerable<string> keys, CancellationToken cancellationToken = default(CancellationToken)) => this._GetAsync<T>(keys, cancellationToken);
		#endregion

		#region [Public] Get (Fragment)
		/// <summary>
		/// Gets fragment information that associates with the key
		/// </summary>
		/// <param name="key">The string that presents key of fragment information</param>
		/// <returns>The information of fragments, first element is total number of fragments, second element is total length of data</returns>
		public Tuple<int, int> GetFragments(string key) => this._GetFragments(key);

		/// <summary>
		/// Gets fragment information that associates with the key
		/// </summary>
		/// <param name="key">The string that presents key of fragment information</param>
		/// <returns>The information of fragments, first element is total number of fragments, second element is total length of data</returns>
		public Task<Tuple<int, int>> GetFragmentsAsync(string key, CancellationToken cancellationToken = default(CancellationToken)) => this._GetFragmentsAsync(key, cancellationToken);

		/// <summary>
		/// Gets cached of fragmented items that associates with the key and indexes
		/// </summary>
		/// <param name="key">The string that presents key of all fragmented items</param>
		/// <param name="indexes">The collection that presents indexes of all fragmented items need to get</param>
		/// <returns>The collection of array of bytes that presents serialized information of fragmented items</returns>
		public List<byte[]> GetAsFragments(string key, List<int> indexes) => this._GetAsFragments(key, indexes);

		/// <summary>
		/// Gets cached of fragmented items that associates with the key and indexes
		/// </summary>
		/// <param name="key">The string that presents key of all fragmented items</param>
		/// <param name="indexes">The collection that presents indexes of all fragmented items need to get</param>
		/// <returns>The collection of array of bytes that presents serialized information of fragmented items</returns>
		public Task<List<byte[]>> GetAsFragmentsAsync(string key, List<int> indexes, CancellationToken cancellationToken = default(CancellationToken)) => this._GetAsFragmentsAsync(key, indexes, cancellationToken);

		/// <summary>
		/// Gets cached of fragmented items that associates with the key and indexes
		/// </summary>
		/// <param name="key">The string that presents key of all fragmented items</param>
		/// <param name="indexes">The collection that presents indexes of all fragmented items need to get</param>
		/// <returns>The collection of array of bytes that presents serialized information of fragmented items</returns>
		public List<byte[]> GetAsFragments(string key, params int[] indexes) => this._GetAsFragments(key, indexes);

		/// <summary>
		/// Gets cached of fragmented items that associates with the key and indexes
		/// </summary>
		/// <param name="key">The string that presents key of all fragmented items</param>
		/// <param name="indexes">The collection that presents indexes of all fragmented items need to get</param>
		/// <returns>The collection of array of bytes that presents serialized information of fragmented items</returns>
		public Task<List<byte[]>> GetAsFragmentsAsync(string key, CancellationToken cancellationToken = default(CancellationToken), params int[] indexes) => this._GetAsFragmentsAsync(key, cancellationToken, indexes);
		#endregion

		#region [Public] Remove
		/// <summary>
		/// Removes a cached item
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to remove</param>
		/// <returns>Returns a boolean value indicating if the item is removed or not</returns>
		public bool Remove(string key) => this._Remove(key);

		/// <summary>
		/// Removes a cached item
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to remove</param>
		/// <returns>Returns a boolean value indicating if the item is removed or not</returns>
		public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default(CancellationToken)) => this._RemoveAsync(key, cancellationToken);
		#endregion

		#region [Public] Remove (Multiple)
		/// <summary>
		/// Removes a collection of cached items
		/// </summary>
		/// <param name="keys">The collection that presents key of cached items need to remove</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		public void Remove(IEnumerable<string> keys, string keyPrefix = null) => this._Remove(keys, keyPrefix);

		/// <summary>
		/// Removes a collection of cached items
		/// </summary>
		/// <param name="keys">The collection that presents key of cached items need to remove</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		public Task RemoveAsync(IEnumerable<string> keys, string keyPrefix = null, CancellationToken cancellationToken = default(CancellationToken)) => this._RemoveAsync(keys, keyPrefix, cancellationToken);
		#endregion

		#region [Public] Remove (Fragment)
		/// <summary>
		/// Removes a cached item (with first 100 fragments) from cache storage
		/// </summary>
		/// <param name="key">The string that presents key of fragmented items need to be removed</param>
		public void RemoveFragments(string key) => this._RemoveFragments(key, 100);

		/// <summary>
		/// Removes a cached item (with first 100 fragments) from cache storage
		/// </summary>
		/// <param name="key">The string that presents key of fragmented items need to be removed</param>
		public Task RemoveFragmentsAsync(string key, CancellationToken cancellationToken = default(CancellationToken)) => this._RemoveFragmentsAsync(key, 100, cancellationToken);
		#endregion

		#region [Public] Exists
		/// <summary>
		/// Determines whether an item exists in the cache
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to check</param>
		/// <returns>Returns a boolean value indicating if the object that associates with the key is cached or not</returns>
		public bool Exists(string key) => Redis.Client.Exists(this._GetKey(key));

		/// <summary>
		/// Determines whether an item exists in the cache
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to check</param>
		/// <returns>Returns a boolean value indicating if the object that associates with the key is cached or not</returns>
		public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default(CancellationToken)) => Redis.Client.ExistsAsync(this._GetKey(key), cancellationToken);
		#endregion

		#region [Public] Clear
		/// <summary>
		/// Clears the cache storage of this isolated region
		/// </summary>
		public void Clear() => this._Clear();

		/// <summary>
		/// Clears the cache storage of this isolated region
		/// </summary>
		public Task ClearAsync(CancellationToken cancellationToken = default(CancellationToken)) => this._ClearAsync(cancellationToken);
		#endregion

	}
}