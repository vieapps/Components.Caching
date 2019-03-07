#region Related components
using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Configuration;
using System.Diagnostics;

using Enyim.Caching;
using Enyim.Caching.Memcached;
using Enyim.Caching.Configuration;

using Microsoft.Extensions.Logging;
#endregion

namespace net.vieapps.Components.Caching
{
	/// <summary>
	/// Manipulates cached objects in isolated regions with Memcached
	/// </summary>
	[DebuggerDisplay("Memcached: {Name} ({ExpirationTime} minutes)")]
	public sealed class Memcached : ICache, IDisposable
	{
		/// <summary>
		/// Create new instance of Memcached
		/// </summary>
		/// <param name="name">The string that presents name of isolated region of the cache</param>
		/// <param name="expirationTime">The number that presents times (in minutes) for caching an item</param>
		/// <param name="storeKeys">true to active store keys of the region (to clear or using with other purposes)</param>
		public Memcached(string name, int expirationTime, bool storeKeys)
		{
			// region name
			this.Name = Helper.GetRegionName(name);

			// expiration time
			this.ExpirationTime = expirationTime > 0 ? expirationTime : Helper.ExpirationTime;

			// store keys
			this._storeKeys = storeKeys;

			// register the region
			Task.Run(() => Memcached.RegisterRegionAsync(this.Name)).ConfigureAwait(false);
		}

		public void Dispose() => this._lock.Dispose();

		~Memcached()
		{
			this.Dispose();
			GC.SuppressFinalize(this);
		}

		#region Get client (singleton)
		static MemcachedClient _Client;

		/// <summary>
		/// Gets the instance of the Memcached client
		/// </summary>
		public static MemcachedClient Client => Memcached._Client ?? (Memcached._Client = Memcached.GetClient());

		internal static MemcachedClient GetClient(IMemcachedClientConfiguration configuration, ILoggerFactory loggerFactory = null)
			=> Memcached._Client ?? (Memcached._Client = new MemcachedClient(loggerFactory, configuration));

		internal static MemcachedClient GetClient(ICacheConfiguration configuration, ILoggerFactory loggerFactory = null)
		{
			if (Memcached._Client == null)
			{
				Memcached.GetClient(configuration.GetMemcachedConfiguration(loggerFactory), loggerFactory);
				var logger = loggerFactory?.CreateLogger<Memcached>();
				if (logger != null && logger.IsEnabled(LogLevel.Debug))
					logger.LogInformation("The Memcached's instance was created");
			}
			return Memcached._Client;
		}

		internal static MemcachedClient GetClient(ILoggerFactory loggerFactory = null)
		{
			if (Memcached._Client == null)
			{
				if (ConfigurationManager.GetSection("memcached") is MemcachedClientConfigurationSectionHandler memcachedSection)
				{
					Memcached._Client = new MemcachedClient(memcachedSection, loggerFactory);
					var logger = loggerFactory?.CreateLogger<Memcached>();
					if (logger != null && logger.IsEnabled(LogLevel.Debug))
						logger.LogInformation("The Memcached's instance was created with stand-alone configuration (app.config/web.config) at the section named 'memcached'");
				}
				else if (ConfigurationManager.GetSection("cache") is CacheConfigurationSectionHandler cacheSection)
				{
					Memcached.GetClient(new CacheConfiguration(cacheSection).GetMemcachedConfiguration(loggerFactory), loggerFactory);
					var logger = loggerFactory?.CreateLogger<Memcached>();
					if (logger != null && logger.IsEnabled(LogLevel.Debug))
						logger.LogInformation("The Memcached's instance was created with stand-alone configuration (app.config/web.config) at the section named 'cache'");
				}
				else
				{
					loggerFactory?.CreateLogger<Memcached>()?.LogError("No configuration is found");
					throw new ConfigurationErrorsException("No configuration is found, the configuration file (app.config/web.config) must have a section named 'memcached' or 'cache'.");
				}
			}
			return Memcached._Client;
		}

		/// <summary>
		/// Prepares the instance of memcached client
		/// </summary>
		/// <param name="loggerFactory"></param>
		/// <param name="configuration"></param>
		public static void PrepareClient(IMemcachedClientConfiguration configuration, ILoggerFactory loggerFactory = null) => Memcached.GetClient(configuration, loggerFactory);

		/// <summary>
		/// Prepares the instance of memcached client
		/// </summary>
		/// <param name="configuration"></param>
		/// <param name="loggerFactory"></param>
		public static void PrepareClient(ICacheConfiguration configuration, ILoggerFactory loggerFactory = null) => Memcached.GetClient(configuration, loggerFactory);

		/// <summary>
		/// Prepares the instance of memcached client
		/// </summary>
		/// <param name="loggerFactory"></param>
		public static void PrepareClient(ILoggerFactory loggerFactory = null) => Memcached.GetClient(loggerFactory);

		#endregion

		#region Attributes
		readonly bool _storeKeys = false;
		readonly ConcurrentQueue<string> _addedKeys = new ConcurrentQueue<string>();
		readonly ConcurrentQueue<string> _removedKeys = new ConcurrentQueue<string>();
		readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
		bool _isUpdatingKeys = false;
		#endregion

		#region Keys
		async Task _PushKeysAsync(CancellationToken cancellationToken = default(CancellationToken))
		{
			// check state
			if (!this._storeKeys || this._isUpdatingKeys)
				return;

			// process
			this._isUpdatingKeys = true;
			var flag = $"{this._RegionKey}-Updating";
			try
			{
				await this._lock.WaitAsync(cancellationToken).ConfigureAwait(false);
				if (this._addedKeys.Count > 0 || this._removedKeys.Count > 0)
				{
					while (await Memcached.Client.GetAsync(flag, cancellationToken).ConfigureAwait(false) != null)
						await Task.Delay(123, cancellationToken).ConfigureAwait(false);
					if (this._addedKeys.Count > 0 || this._removedKeys.Count > 0)
					{
						await Memcached.Client.StoreAsync(StoreMode.Set, flag, "v", cancellationToken).ConfigureAwait(false);
						var removedKeys = new List<string>();
						var addedKeys = new List<string>();

						while (this._removedKeys.Count > 0)
							if (this._removedKeys.TryDequeue(out string key))
								removedKeys.Add(key);
						while (this._addedKeys.Count > 0)
							if (this._addedKeys.TryDequeue(out string key))
								addedKeys.Add(key);

						await Task.Delay(123, cancellationToken).ConfigureAwait(false);

						while (this._removedKeys.Count > 0)
							if (this._removedKeys.TryDequeue(out string key))
								removedKeys.Add(key);
						while (this._addedKeys.Count > 0)
							if (this._addedKeys.TryDequeue(out string key))
								addedKeys.Add(key);

						var syncKeys = await Memcached.FetchKeysAsync(this._RegionKey, cancellationToken).ConfigureAwait(false);
						await Memcached.SetKeysAsync(this._RegionKey, new HashSet<string>(syncKeys.Except(removedKeys).Union(addedKeys)), cancellationToken).ConfigureAwait(false);
					}
				}
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this.Name, "Error occurred while updating region keys", ex);
			}
			finally
			{
				this._isUpdatingKeys = false;
				this._lock.Release();
				var removeTask = Task.Run(() => Memcached.Client.RemoveAsync(flag)).ConfigureAwait(false);
			}
		}

		void _PushKeys()
			=> Task.Run(() => this._PushKeysAsync()).ConfigureAwait(false);

		HashSet<string> _GetKeys()
			=> Memcached.FetchKeys(this._RegionKey);

		Task<HashSet<string>> _GetKeysAsync(CancellationToken cancellationToken = default(CancellationToken))
			=> Memcached.FetchKeysAsync(this._RegionKey, cancellationToken);

		void _ClearKeys()
		{
			try
			{
				while (this._addedKeys.Count > 0)
					this._addedKeys.TryDequeue(out string key);
				while (this._removedKeys.Count > 0)
					this._removedKeys.TryDequeue(out string key);
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this.Name, "Error occurred while cleaning region keys", ex);
			}
		}

		void _UpdateKey(string key, bool doPush, bool isRemoved = false)
		{
			if (this._storeKeys)
			{
				if (isRemoved)
					this._removedKeys.Enqueue(key);
				else
					this._addedKeys.Enqueue(key);
				if (doPush)
					this._PushKeys();
			}
		}
		#endregion

		#region Set
		bool _Set(string key, object value, TimeSpan validFor, bool doPush = true, StoreMode mode = StoreMode.Set)
		{
			var success = false;
			if (!string.IsNullOrWhiteSpace(key) && value != null)
				try
				{
					success = Memcached.Client.Store(mode, this._GetKey(key), value, validFor);
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
				this._UpdateKey(key, doPush);

			return success;
		}

		bool _Set(string key, object value, int expirationTime = 0, bool doPush = true, StoreMode mode = StoreMode.Set)
			=> this._Set(key, value, TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime), doPush, mode);

		bool _Set(string key, object value, DateTime expiresAt, bool doPush = true, StoreMode mode = StoreMode.Set)
		{
			var success = false;
			if (!string.IsNullOrWhiteSpace(key) && value != null)
				try
				{
					success = Memcached.Client.Store(mode, this._GetKey(key), value, expiresAt);
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
				this._UpdateKey(key, doPush);

			return success;
		}

		async Task<bool> _SetAsync(string key, object value, TimeSpan validFor, bool doPush = true, StoreMode mode = StoreMode.Set, CancellationToken cancellationToken = default(CancellationToken))
		{
			var success = false;
			if (!string.IsNullOrWhiteSpace(key) && value != null)
				try
				{
					success = await Memcached.Client.StoreAsync(mode, this._GetKey(key), value, validFor, cancellationToken).ConfigureAwait(false);
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
				this._UpdateKey(key, doPush);

			return success;
		}

		Task<bool> _SetAsync(string key, object value, int expirationTime = 0, bool doPush = true, StoreMode mode = StoreMode.Set, CancellationToken cancellationToken = default(CancellationToken))
			=> this._SetAsync(key, value, TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime), doPush, mode, cancellationToken);

		async Task<bool> _SetAsync(string key, object value, DateTime expiresAt, bool doPush = true, StoreMode mode = StoreMode.Set, CancellationToken cancellationToken = default(CancellationToken))
		{
			var success = false;
			if (!string.IsNullOrWhiteSpace(key) && value != null)
				try
				{
					success = await Memcached.Client.StoreAsync(mode, this._GetKey(key), value, expiresAt, cancellationToken).ConfigureAwait(false);
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
				this._UpdateKey(key, doPush);

			return success;
		}
		#endregion

		#region Set (Multiple)
		void _Set<T>(IDictionary<string, T> items, string keyPrefix = null, int expirationTime = 0, StoreMode mode = StoreMode.Set)
		{
			if (items != null && items.Count > 0)
			{
				items.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key))
					.ToList()
					.ForEach(kvp => this._Set((string.IsNullOrWhiteSpace(keyPrefix) ? "" : keyPrefix) + kvp.Key, kvp.Value, expirationTime, false, mode));
				this._PushKeys();
			}
		}

		void _Set(IDictionary<string, object> items, string keyPrefix = null, int expirationTime = 0, StoreMode mode = StoreMode.Set)
			=> this._Set<object>(items, keyPrefix, expirationTime, mode);

		async Task _SetAsync<T>(IDictionary<string, T> items, string keyPrefix = null, int expirationTime = 0, StoreMode mode = StoreMode.Set, CancellationToken cancellationToken = default(CancellationToken))
		{
			await Task.WhenAll(items != null
				? items.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key)).Select(kvp => this._SetAsync((string.IsNullOrWhiteSpace(keyPrefix) ? "" : keyPrefix) + kvp.Key, kvp.Value, expirationTime, false, mode, cancellationToken))
				: new List<Task<bool>>()
			).ConfigureAwait(false);
			this._PushKeys();
		}

		Task _SetAsync(IDictionary<string, object> items, string keyPrefix = null, int expirationTime = 0, StoreMode mode = StoreMode.Set, CancellationToken cancellationToken = default(CancellationToken))
			=> this._SetAsync<object>(items, keyPrefix, expirationTime, mode, cancellationToken);
		#endregion

		#region Set (Fragment)
		bool _SetFragments(string key, List<byte[]> fragments, int expirationTime = 0, StoreMode mode = StoreMode.Set)
		{
			var success = fragments != null && fragments.Count > 0
				? this._Set(key, new ArraySegment<byte>(fragments.GetFirstFragment()), expirationTime, true, mode)
				: false;

			if (success && fragments.Count > 1)
			{
				var items = new Dictionary<string, object>();
				for (var index = 1; index < fragments.Count; index++)
					items.Add(this._GetFragmentKey(key, index), new ArraySegment<byte>(fragments[index]));
				var validFor = TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime);
				Task.Run(async () => await Task.WhenAll(items.Select(kvp => Memcached.Client.StoreAsync(mode, this._GetKey(kvp.Key), kvp.Value, validFor))).ConfigureAwait(false)).ConfigureAwait(false);
			}

			return success;
		}

		async Task<bool> _SetFragmentsAsync(string key, List<byte[]> fragments, int expirationTime = 0, StoreMode mode = StoreMode.Set, CancellationToken cancellationToken = default(CancellationToken))
		{
			var success = fragments != null && fragments.Count > 0
				? await this._SetAsync(key, new ArraySegment<byte>(fragments.GetFirstFragment()), expirationTime, true, mode, cancellationToken).ConfigureAwait(false)
				: false;

			if (success && fragments.Count > 1)
			{
				var items = new Dictionary<string, object>();
				for (var index = 1; index < fragments.Count; index++)
					items.Add(this._GetFragmentKey(key, index), new ArraySegment<byte>(fragments[index]));
				var validFor = TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime);
				await Task.WhenAll(items.Select(kvp => Memcached.Client.StoreAsync(mode, this._GetKey(kvp.Key), kvp.Value, validFor, cancellationToken))).ConfigureAwait(false);
			}

			return success;
		}

		bool _SetAsFragments(string key, object value, int expirationTime = 0, StoreMode mode = StoreMode.Set)
			=> string.IsNullOrWhiteSpace(key) || value == null
				? false
				: this._SetFragments(key, Helper.Serialize(value).Split(), expirationTime, mode);

		Task<bool> _SetAsFragmentsAsync(string key, object value, int expirationTime = 0, StoreMode mode = StoreMode.Set, CancellationToken cancellationToken = default(CancellationToken))
			=> string.IsNullOrWhiteSpace(key) || value == null
				? Task.FromResult(false)
				: this._SetFragmentsAsync(key, Helper.Serialize(value).Split(), expirationTime, mode, cancellationToken);
		#endregion

		#region Get
		object _Get(string key, bool autoGetFragments = true)
		{
			if (string.IsNullOrWhiteSpace(key))
				throw new ArgumentNullException(key);

			object value = null;
			try
			{
				value = Memcached.Client.Get(this._GetKey(key));
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this.Name, $"Error occurred while fetching an object from cache storage [{key}]", ex);
			}

			if (autoGetFragments && value != null && value is byte[] && (value as byte[]).Length > 8 && Helper.GetFlags(value as byte[]).Item1.Equals(Helper.FlagOfFirstFragmentBlock))
				try
				{
					value = this._GetFromFragments(key, value as byte[]);
				}
				catch (Exception ex)
				{
					Helper.WriteLogs(this.Name, $"Error occurred while fetching an objects' fragments from cache storage [{key}]", ex);
					value = null;
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
				value = await Memcached.Client.GetAsync(this._GetKey(key), cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this.Name, $"Error occurred while fetching an object from cache storage [{key}]", ex);
			}

			if (autoGetFragments && value != null && value is byte[] && (value as byte[]).Length > 8 && Helper.GetFlags(value as byte[]).Item1.Equals(Helper.FlagOfFirstFragmentBlock))
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

			return value;
		}
		#endregion

		#region Get (Multiple)
		IDictionary<string, object> _Get(IEnumerable<string> keys)
		{
			if (keys == null)
				return null;

			// get collection of cached objects
			IDictionary<string, object> items = null;
			try
			{
				items = Memcached.Client.Get(keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => this._GetKey(key)));
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
			=> this._Get(keys)?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value is T ? (T)kvp.Value : default(T));

		async Task<IDictionary<string, object>> _GetAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default(CancellationToken))
		{
			if (keys == null)
				return null;

			IDictionary<string, object> items = null;
			try
			{
				items = await Memcached.Client.GetAsync(keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => this._GetKey(key)), cancellationToken).ConfigureAwait(false);
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
			=> (await this._GetAsync(keys, cancellationToken).ConfigureAwait(false))?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value is T ? (T)kvp.Value : default(T));
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
				: this._Get(indexes.Select(index => this._GetFragmentKey(key, index)));
			return fragments != null
				? fragments.OrderBy(kvp => kvp.Key).Select(kvp => kvp.Value as byte[]).ToList()
				: new List<byte[]>();
		}

		async Task<List<byte[]>> _GetAsFragmentsAsync(string key, List<int> indexes, CancellationToken cancellationToken = default(CancellationToken))
		{
			var fragments = string.IsNullOrWhiteSpace(key) || indexes == null || indexes.Count < 1
				? null
				: await this._GetAsync(indexes.Select(index => this._GetFragmentKey(key, index)), cancellationToken).ConfigureAwait(false);
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
		bool _Remove(string key, bool doPush = true)
		{
			var success = false;
			if (!string.IsNullOrWhiteSpace(key))
				try
				{
					success = Memcached.Client.Remove(this._GetKey(key));
				}
				catch (Exception ex)
				{
					Helper.WriteLogs(this.Name, $"Error occurred while removing an object from cache storage [{key}]", ex);
				}

			if (success)
				this._UpdateKey(key, doPush, true);

			return success;
		}

		async Task<bool> _RemoveAsync(string key, bool doPush = true, CancellationToken cancellationToken = default(CancellationToken))
		{
			var success = false;
			if (!string.IsNullOrWhiteSpace(key))
				try
				{
					success = await Memcached.Client.RemoveAsync(this._GetKey(key), cancellationToken).ConfigureAwait(false);
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
				this._UpdateKey(key, doPush, true);

			return success;
		}
		#endregion

		#region Remove (Multiple)
		void _Remove(IEnumerable<string> keys, string keyPrefix = null)
		{
			keys?.Where(key => !string.IsNullOrWhiteSpace(key)).ToList().ForEach(key => this._Remove((string.IsNullOrWhiteSpace(keyPrefix) ? "" : keyPrefix) + key, false));
			this._PushKeys();
		}

		async Task _RemoveAsync(IEnumerable<string> keys, string keyPrefix = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			await Task.WhenAll(keys?.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => this._RemoveAsync((string.IsNullOrWhiteSpace(keyPrefix) ? "" : keyPrefix) + key, false, cancellationToken))).ConfigureAwait(false);
			this._PushKeys();
		}
		#endregion

		#region Clear
		void _Clear()
			=> Task.Run(() => this._ClearAsync()).ConfigureAwait(false);

		async Task _ClearAsync(CancellationToken cancellationToken = default(CancellationToken))
		{
			var keys = await this._GetKeysAsync(cancellationToken).ConfigureAwait(false);
			await Task.WhenAll(
				this._RemoveAsync(keys, null, cancellationToken),
				Memcached.Client.RemoveAsync(this._RegionKey, cancellationToken),
				Memcached.Client.RemoveAsync(this._RegionKey + "-Updating", cancellationToken),
				this._storeKeys ? Task.Run(() => this._ClearKeys()) : Task.CompletedTask
			).ConfigureAwait(false);
		}
		#endregion

		// -----------------------------------------------------

		#region [Helper]
		string _GetKey(string key)
			=> Helper.GetCacheKey(this.Name, key);

		string _GetFragmentKey(string key, int index)
			=> Helper.GetFragmentKey(key, index);

		List<string> _GetFragmentKeys(string key, int max)
			=> Helper.GetFragmentKeys(key, max);

		string _RegionKey
			=> this._GetKey("<Keys-Of-Region>");
		#endregion

		#region [Static]
		internal static async Task<bool> SetKeysAsync(string key, HashSet<string> keys, CancellationToken cancellationToken = default(CancellationToken))
		{
			var fragments = Helper.Split(Helper.Serialize(keys, false));
			var success = await Memcached.Client.StoreAsync(StoreMode.Set, key, new ArraySegment<byte>(CacheUtils.Helper.Combine(BitConverter.GetBytes(fragments.Count), fragments[0])), cancellationToken).ConfigureAwait(false);
			if (success && fragments.Count > 1)
			{
				var tasks = new List<Task>();
				for (var index = 1; index < fragments.Count; index++)
					tasks.Add(Memcached.Client.StoreAsync(StoreMode.Set, $"{key}:{index}", new ArraySegment<byte>(fragments[index]), cancellationToken));
				await Task.WhenAll(tasks).ConfigureAwait(false);
			}
			return success;
		}

		internal static HashSet<string> FetchKeys(string key)
		{
			var data = Memcached.Client.Get<byte[]>(key);
			if (data == null || data.Length < 4)
				return new HashSet<string>();

			var tmp = new byte[4];
			Buffer.BlockCopy(data, 0, tmp, 0, 4);
			var fragments = Enumerable.Repeat(new byte[0], BitConverter.ToInt32(tmp, 0)).ToList();
			if (fragments.Count > 1)
			{
				Task fetchAsync(int index)
				{
					return Task.Run(() => fragments[index] = Memcached.Client.Get<byte[]>($"{key}:{index}"));
				}
				var tasks = new List<Task>();
				for (var index = 1; index < fragments.Count; index++)
					tasks.Add(fetchAsync(index));
				Task.WaitAll(tasks.ToArray(), 13000);
			}

			tmp = new byte[data.Length - 4];
			Buffer.BlockCopy(data, 4, tmp, 0, data.Length - 4);
			fragments[0] = tmp;
			data = Helper.Combine(new byte[0], fragments);
			try
			{
				return data.Length > 0
					? Helper.Deserialize(data, 0, data.Length) as HashSet<string>
					: new HashSet<string>();
			}
			catch
			{
				return new HashSet<string>();
			}
		}

		internal static async Task<HashSet<string>> FetchKeysAsync(string key, CancellationToken cancellationToken = default(CancellationToken))
		{
			var data = await Memcached.Client.GetAsync<byte[]>(key, cancellationToken).ConfigureAwait(false);
			if (data == null || data.Length < 4)
				return new HashSet<string>();

			var tmp = new byte[4];
			Buffer.BlockCopy(data, 0, tmp, 0, 4);
			var fragments = Enumerable.Repeat(new byte[0], BitConverter.ToInt32(tmp, 0)).ToList();
			if (fragments.Count > 1)
			{
				async Task fetchAsync(int index)
				{
					fragments[index] = await Memcached.Client.GetAsync<byte[]>($"{key}:{index}", cancellationToken).ConfigureAwait(false);
				}
				var tasks = new List<Task>();
				for (var index = 1; index < fragments.Count; index++)
					tasks.Add(fetchAsync(index));
				await Task.WhenAll(tasks).ConfigureAwait(false);
			}

			tmp = new byte[data.Length - 4];
			Buffer.BlockCopy(data, 4, tmp, 0, data.Length - 4);
			fragments[0] = tmp;
			data = Helper.Combine(new byte[0], fragments);
			try
			{
				return data.Length > 0
					? Helper.Deserialize(data, 0, data.Length) as HashSet<string>
					: new HashSet<string>();
			}
			catch
			{
				return new HashSet<string>();
			}
		}

		/// <summary>
		/// Gets the collection of all registered regions (in distributed cache)
		/// </summary>
		public static HashSet<string> GetRegions()
			=> Memcached.FetchKeys(Helper.RegionsKey);

		/// <summary>
		/// Gets the collection of all registered regions (in distributed cache)
		/// </summary>
		public static Task<HashSet<string>> GetRegionsAsync(CancellationToken cancellationToken = default(CancellationToken))
			=> Memcached.FetchKeysAsync(Helper.RegionsKey, cancellationToken);

		static async Task RegisterRegionAsync(string name, CancellationToken cancellationToken = default(CancellationToken))
		{
			var registeringKey = $"{Helper.RegionsKey}-Registering";
			try
			{
				var attempt = 0;
				while (attempt < 123 && await Memcached.Client.GetAsync(registeringKey, cancellationToken).ConfigureAwait(false) != null)
				{
					await Task.Delay(123, cancellationToken).ConfigureAwait(false);
					attempt++;
				}
				await Memcached.Client.StoreAsync(StoreMode.Set, registeringKey, "v", TimeSpan.FromSeconds(13), cancellationToken).ConfigureAwait(false);

				var regions = await Memcached.FetchKeysAsync(Helper.RegionsKey, cancellationToken).ConfigureAwait(false);
				if (!regions.Contains(name))
				{
					regions.Add(name);
					await Memcached.SetKeysAsync(Helper.RegionsKey, regions, cancellationToken).ConfigureAwait(false);
				}
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(name, $"Error occurred while registering a region: {ex.Message}", ex);
			}
			finally
			{
				await Memcached.Client.RemoveAsync(registeringKey, cancellationToken).ConfigureAwait(false);
			}
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
		public bool Set(string key, object value, int expirationTime = 0) => this._Set(key, value, expirationTime, true, StoreMode.Set);

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Set(string key, object value, TimeSpan validFor) => this._Set(key, value, validFor, true, StoreMode.Set);

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Set(string key, object value, DateTime expiresAt) => this._Set(key, value, expiresAt, true, StoreMode.Set);

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetAsync(string key, object value, int expirationTime = 0, CancellationToken cancellationToken = default(CancellationToken)) => this._SetAsync(key, value, expirationTime, true, StoreMode.Set, cancellationToken);

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetAsync(string key, object value, TimeSpan validFor, CancellationToken cancellationToken = default(CancellationToken)) => this._SetAsync(key, value, validFor, true, StoreMode.Set, cancellationToken);

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetAsync(string key, object value, DateTime expiresAt, CancellationToken cancellationToken = default(CancellationToken)) => this._SetAsync(key, value, expiresAt, true, StoreMode.Set, cancellationToken);
		#endregion

		#region [Public] Set (Multiple)
		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		public void Set(IDictionary<string, object> items, string keyPrefix = null, int expirationTime = 0) => this._Set(items, keyPrefix, expirationTime, StoreMode.Set);

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <typeparam name="T">The type for casting the cached item</typeparam>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		public void Set<T>(IDictionary<string, T> items, string keyPrefix = null, int expirationTime = 0) => this._Set<T>(items, keyPrefix, expirationTime, StoreMode.Set);

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		public Task SetAsync(IDictionary<string, object> items, string keyPrefix = null, int expirationTime = 0, CancellationToken cancellationToken = default(CancellationToken)) => this._SetAsync(items, keyPrefix, expirationTime, StoreMode.Set, cancellationToken);

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <typeparam name="T">The type for casting the cached item</typeparam>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		public Task SetAsync<T>(IDictionary<string, T> items, string keyPrefix = null, int expirationTime = 0, CancellationToken cancellationToken = default(CancellationToken)) => this._SetAsync<T>(items, keyPrefix, expirationTime, StoreMode.Set, cancellationToken);
		#endregion

		#region [Public] Set (Fragment)
		/// <summary>
		/// Adds an item (as fragments) into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="fragments">The collection that contains all fragments (object that serialized as binary - array bytes)</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool SetFragments(string key, List<byte[]> fragments, int expirationTime = 0) => this._SetFragments(key, fragments, expirationTime, StoreMode.Set);

		/// <summary>
		/// Adds an item (as fragments) into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="fragments">The collection that contains all fragments (object that serialized as binary - array bytes)</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetFragmentsAsync(string key, List<byte[]> fragments, int expirationTime = 0, CancellationToken cancellationToken = default(CancellationToken)) => this._SetFragmentsAsync(key, fragments, expirationTime, StoreMode.Set, cancellationToken);

		/// <summary>
		/// Serializes object into array of bytes, splits into one or more fragments and updates into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool SetAsFragments(string key, object value, int expirationTime = 0) => this._SetAsFragments(key, value, expirationTime, StoreMode.Set);

		/// <summary>
		/// Serializes object into array of bytes, splits into one or more fragments and updates into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetAsFragmentsAsync(string key, object value, int expirationTime = 0, CancellationToken cancellationToken = default(CancellationToken)) => this._SetAsFragmentsAsync(key, value, expirationTime, StoreMode.Set, cancellationToken);
		#endregion

		#region [Public] Add
		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Add(string key, object value, int expirationTime = 0) => this._Set(key, value, TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime), true, StoreMode.Add);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Add(string key, object value, TimeSpan validFor) => this._Set(key, value, validFor, true, StoreMode.Add);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Add(string key, object value, DateTime expiresAt) => this._Set(key, value, expiresAt, true, StoreMode.Add);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> AddAsync(string key, object value, int expirationTime = 0, CancellationToken cancellationToken = default(CancellationToken)) => this._SetAsync(key, value, TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime), true, StoreMode.Add, cancellationToken);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> AddAsync(string key, object value, TimeSpan validFor, CancellationToken cancellationToken = default(CancellationToken)) => this._SetAsync(key, value, validFor, true, StoreMode.Add, cancellationToken);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> AddAsync(string key, object value, DateTime expiresAt, CancellationToken cancellationToken = default(CancellationToken)) => this._SetAsync(key, value, expiresAt, true, StoreMode.Add, cancellationToken);
		#endregion

		#region [Public] Replace
		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Replace(string key, object value, int expirationTime = 0) => this._Set(key, value, TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime), true, StoreMode.Replace);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Replace(string key, object value, TimeSpan validFor) => this._Set(key, value, validFor, true, StoreMode.Replace);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Replace(string key, object value, DateTime expiresAt) => this._Set(key, value, expiresAt, true, StoreMode.Replace);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> ReplaceAsync(string key, object value, int expirationTime = 0, CancellationToken cancellationToken = default(CancellationToken)) => this._SetAsync(key, value, TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime), true, StoreMode.Replace, cancellationToken);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> ReplaceAsync(string key, object value, TimeSpan validFor, CancellationToken cancellationToken = default(CancellationToken)) => this._SetAsync(key, value, validFor, true, StoreMode.Replace, cancellationToken);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> ReplaceAsync(string key, object value, DateTime expiresAt, CancellationToken cancellationToken = default(CancellationToken)) => this._SetAsync(key, value, expiresAt, true, StoreMode.Replace, cancellationToken);
		#endregion

		#region [Public] Get
		/// <summary>
		/// Retreives a cached item
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to retreive</param>
		/// <returns>The retrieved cache item, or a null reference if the key is not found</returns>
		public object Get(string key) => this._Get(key);

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
		public bool Remove(string key) => this._Remove(key, true);

		/// <summary>
		/// Removes a cached item
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to remove</param>
		/// <returns>Returns a boolean value indicating if the item is removed or not</returns>
		public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default(CancellationToken)) => this._RemoveAsync(key, true, cancellationToken);
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
		public void RemoveFragments(string key) => this._Remove(this._GetFragmentKeys(key, 100), null);

		/// <summary>
		/// Removes a cached item (with first 100 fragments) from cache storage
		/// </summary>
		/// <param name="key">The string that presents key of fragmented items need to be removed</param>
		public Task RemoveFragmentsAsync(string key, CancellationToken cancellationToken = default(CancellationToken)) => this._RemoveAsync(this._GetFragmentKeys(key, 100), null, cancellationToken);
		#endregion

		#region [Public] Exists
		/// <summary>
		/// Determines whether an item exists in the cache
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to check</param>
		/// <returns>Returns a boolean value indicating if the object that associates with the key is cached or not</returns>
		public bool Exists(string key) => Memcached.Client.Exists(this._GetKey(key));

		/// <summary>
		/// Determines whether an item exists in the cache
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to check</param>
		/// <returns>Returns a boolean value indicating if the object that associates with the key is cached or not</returns>
		public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default(CancellationToken)) => Memcached.Client.ExistsAsync(this._GetKey(key), cancellationToken);
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