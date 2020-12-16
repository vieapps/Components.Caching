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
	public sealed class Memcached : ICache
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
			Task.Run(async () =>
			{
				await Task.Delay(Memcached.ConnectionTimeout > 0 ? Memcached.ConnectionTimeout + 123 : 5432).ConfigureAwait(false);
				await Memcached.RegisterRegionAsync(this.Name).ConfigureAwait(false);
			}).ConfigureAwait(false);
		}

		public void Dispose()
		{
			GC.SuppressFinalize(this);
			this._lock.Dispose();
		}

		~Memcached()
			=> this.Dispose();

		#region Get client (singleton)
		static MemcachedClient _Client { get; set; }

		static SemaphoreSlim _ClientLock { get; } = new SemaphoreSlim(1, 1);

		static int ConnectionTimeout { get; set; }

		internal static MemcachedClient GetClient(IMemcachedClientConfiguration configuration, ILoggerFactory loggerFactory = null)
		{
			Memcached.ConnectionTimeout = (int)configuration.SocketPool.ConnectionTimeout.TotalMilliseconds;
			return Memcached._Client ?? (Memcached._Client = new MemcachedClient(loggerFactory, configuration)); ;
		}

		/// <summary>
		/// Gets the instance of the Memcached client
		/// </summary>
		public static MemcachedClient Client
		{
			get
			{
				if (Memcached._Client == null)
				{
					Memcached._ClientLock.Wait();
					try
					{
						if (Memcached._Client == null)
						{
							if (!(ConfigurationManager.GetSection("net.vieapps.cache") is CacheConfigurationSectionHandler config))
							{
								config = ConfigurationManager.GetSection("cache") as CacheConfigurationSectionHandler;
								if (config == null)
									config = ConfigurationManager.GetSection("memcached") as CacheConfigurationSectionHandler;
							}

							if (config == null)
								throw new ConfigurationErrorsException("No configuration section is found, the configuration file (app.config/web.config) must have a section named 'net.vieapps.cache' or 'cache' or 'memcached'.");

							var loggerFactory = Logger.GetLoggerFactory();
							Memcached.GetClient(new CacheConfiguration(config).GetMemcachedConfiguration(loggerFactory), loggerFactory);

							var logger = loggerFactory.CreateLogger<Memcached>();
							if (logger.IsEnabled(LogLevel.Debug))
								logger.LogDebug("The Memcached's instance was created with stand-alone configuration (app.config/web.config)");
						}
					}
					catch (Exception)
					{
						throw;
					}
					finally
					{
						Memcached._ClientLock.Release();
					}
				}
				return Memcached._Client;
			}
		}
		#endregion

		#region Attributes
		readonly bool _storeKeys = false;
		readonly ConcurrentQueue<string> _addedKeys = new ConcurrentQueue<string>();
		readonly ConcurrentQueue<string> _removedKeys = new ConcurrentQueue<string>();
		readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
		bool _isUpdatingKeys = false;
		#endregion

		#region Keys
		async Task _PushKeysAsync(CancellationToken cancellationToken = default)
		{
			if (!this._storeKeys || this._isUpdatingKeys || (this._addedKeys.Count < 1 && this._removedKeys.Count < 1))
				return;

			this._isUpdatingKeys = true;
			await this._lock.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				if (this._addedKeys.Count > 0 || this._removedKeys.Count > 0)
					try
					{
						var removedKeys = new List<string>();
						var addedKeys = new List<string>();

						while (this._removedKeys.Count > 0)
							if (this._removedKeys.TryDequeue(out var key))
								removedKeys.Add(key);
						while (this._addedKeys.Count > 0)
							if (this._addedKeys.TryDequeue(out var key))
								addedKeys.Add(key);

						var flag = $"{this._RegionKey}-Updating";
						while (await Memcached.Client.GetAsync(flag, cancellationToken).ConfigureAwait(false) != null || await Memcached.Client.GetAsync($"{this._RegionKey}-Cleaning", cancellationToken).ConfigureAwait(false) != null)
							await Task.Delay(123, cancellationToken).ConfigureAwait(false);
						await Memcached.Client.StoreAsync(StoreMode.Set, flag, "v", cancellationToken).ConfigureAwait(false);

						while (this._removedKeys.Count > 0)
							if (this._removedKeys.TryDequeue(out string key))
								removedKeys.Add(key);
						while (this._addedKeys.Count > 0)
							if (this._addedKeys.TryDequeue(out string key))
								addedKeys.Add(key);

						var syncKeys = await Memcached.FetchKeysAsync(this._RegionKey, cancellationToken).ConfigureAwait(false);
						syncKeys = new HashSet<string>(syncKeys.Except(removedKeys).Union(addedKeys).Distinct());
						await Memcached.SetKeysAsync(this._RegionKey, syncKeys, cancellationToken).ConfigureAwait(false);
					}
					catch (Exception)
					{
						throw;
					}
					finally
					{
						var next = Task.Run(async () =>
						{
							await Memcached.Client.RemoveAsync($"{this._RegionKey}-Updating").ConfigureAwait(false);
							if (this._addedKeys.Count > 0 || this._removedKeys.Count > 0)
							{
								await Task.Delay(1234).ConfigureAwait(false);
								this._PushKeys();
							}
						}).ConfigureAwait(false);
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
			}
		}

		void _PushKeys()
			=> Task.Run(() => this._PushKeysAsync()).ConfigureAwait(false);

		HashSet<string> _GetKeys()
			=> Memcached.FetchKeys(this._RegionKey);

		Task<HashSet<string>> _GetKeysAsync(CancellationToken cancellationToken = default)
			=> Memcached.FetchKeysAsync(this._RegionKey, cancellationToken);

		void _ClearKeys()
		{
			try
			{
				while (this._addedKeys.Count > 0)
					this._addedKeys.TryDequeue(out var key);
				while (this._removedKeys.Count > 0)
					this._removedKeys.TryDequeue(out var key);
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

		async Task<bool> _SetAsync(string key, object value, TimeSpan validFor, bool doPush = true, StoreMode mode = StoreMode.Set, CancellationToken cancellationToken = default)
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

		Task<bool> _SetAsync(string key, object value, int expirationTime = 0, bool doPush = true, StoreMode mode = StoreMode.Set, CancellationToken cancellationToken = default)
			=> this._SetAsync(key, value, TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime), doPush, mode, cancellationToken);

		async Task<bool> _SetAsync(string key, object value, DateTime expiresAt, bool doPush = true, StoreMode mode = StoreMode.Set, CancellationToken cancellationToken = default)
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

		async Task _SetAsync<T>(IDictionary<string, T> items, string keyPrefix = null, int expirationTime = 0, StoreMode mode = StoreMode.Set, CancellationToken cancellationToken = default)
		{
			await Task.WhenAll(items != null
				? items.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key)).Select(kvp => this._SetAsync((string.IsNullOrWhiteSpace(keyPrefix) ? "" : keyPrefix) + kvp.Key, kvp.Value, expirationTime, false, mode, cancellationToken))
				: new List<Task<bool>>()
			).ConfigureAwait(false);
			this._PushKeys();
		}

		Task _SetAsync(IDictionary<string, object> items, string keyPrefix = null, int expirationTime = 0, StoreMode mode = StoreMode.Set, CancellationToken cancellationToken = default)
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

		async Task<bool> _SetFragmentsAsync(string key, List<byte[]> fragments, int expirationTime = 0, StoreMode mode = StoreMode.Set, CancellationToken cancellationToken = default)
		{
			var success = fragments != null && fragments.Count > 0 && await this._SetAsync(key, new ArraySegment<byte>(fragments.GetFirstFragment()), expirationTime, true, mode, cancellationToken).ConfigureAwait(false);

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
			=> !string.IsNullOrWhiteSpace(key) && value != null && this._SetFragments(key, CacheUtils.Helper.Split(Helper.Serialize(value), Helper.FragmentSize).ToList(), expirationTime, mode);

		Task<bool> _SetAsFragmentsAsync(string key, object value, int expirationTime = 0, StoreMode mode = StoreMode.Set, CancellationToken cancellationToken = default)
			=> string.IsNullOrWhiteSpace(key) || value == null
				? Task.FromResult(false)
				: this._SetFragmentsAsync(key, CacheUtils.Helper.Split(Helper.Serialize(value), Helper.FragmentSize).ToList(), expirationTime, mode, cancellationToken);
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

		async Task<object> _GetAsync(string key, bool autoGetFragments = true, CancellationToken cancellationToken = default)
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
			=> this._Get(keys)?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value is T ? (T)kvp.Value : default);

		async Task<IDictionary<string, object>> _GetAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
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

		async Task<IDictionary<string, T>> _GetAsync<T>(IEnumerable<string> keys, CancellationToken cancellationToken = default)
			=> (await this._GetAsync(keys, cancellationToken).ConfigureAwait(false))?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value is T ? (T)kvp.Value : default);
		#endregion

		#region Get (Fragment)
		Tuple<int, int> _GetFragments(string key)
			=> Helper.GetFragmentsInfo(this._Get(key, false) as byte[]);

		async Task<Tuple<int, int>> _GetFragmentsAsync(string key, CancellationToken cancellationToken = default)
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

		async Task<List<byte[]>> _GetAsFragmentsAsync(string key, List<int> indexes, CancellationToken cancellationToken = default)
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

		Task<List<byte[]>> _GetAsFragmentsAsync(string key, CancellationToken cancellationToken = default, params int[] indexes)
			=> string.IsNullOrWhiteSpace(key) || indexes == null || indexes.Length < 1
				? Task.FromResult<List<byte[]>>(null)
				: this._GetAsFragmentsAsync(key, indexes.ToList(), cancellationToken);

		object _GetFromFragments(string key, byte[] firstBlock)
		{
			try
			{
				var info = firstBlock.GetFragmentsInfo();
				return CacheUtils.Helper.Concat(new[] { firstBlock }.Concat(info.Item1 > 1 ? this._GetAsFragments(key, Enumerable.Range(1, info.Item1 - 1).ToList()) : new List<byte[]>())).DeserializeFromFragments();
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this.Name, $"Error occurred while serializing an object from fragmented data [{key}]", ex);
				return null;
			}
		}

		async Task<object> _GetFromFragmentsAsync(string key, byte[] firstBlock, CancellationToken cancellationToken = default)
		{
			try
			{
				var info = firstBlock.GetFragmentsInfo();
				return CacheUtils.Helper.Concat(new[] { firstBlock }.Concat(info.Item1 > 1 ? await this._GetAsFragmentsAsync(key, Enumerable.Range(1, info.Item1 - 1).ToList(), cancellationToken).ConfigureAwait(false) : new List<byte[]>())).DeserializeFromFragments();
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

		async Task<bool> _RemoveAsync(string key, bool doPush = true, CancellationToken cancellationToken = default)
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
			(keys ?? new List<string>()).Where(key => !string.IsNullOrWhiteSpace(key))
				.ToList()
				.ForEach(key => this._Remove((string.IsNullOrWhiteSpace(keyPrefix) ? "" : keyPrefix) + key, false));
			this._PushKeys();
		}

		async Task _RemoveAsync(IEnumerable<string> keys, string keyPrefix = null, CancellationToken cancellationToken = default)
		{
			await Task.WhenAll((keys ?? new List<string>()).Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => this._RemoveAsync((string.IsNullOrWhiteSpace(keyPrefix) ? "" : keyPrefix) + key, false, cancellationToken))).ConfigureAwait(false);
			this._PushKeys();
		}
		#endregion

		#region Clear
		void _Clear()
			=> Task.Run(() => this._ClearAsync()).ConfigureAwait(false);

		async Task _ClearAsync(CancellationToken cancellationToken = default)
		{
			var flag = $"{this._RegionKey}-Cleaning";
			await this._lock.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				await Memcached.Client.StoreAsync(StoreMode.Set, flag, "v", cancellationToken).ConfigureAwait(false);
				var keys = await this._GetKeysAsync(cancellationToken).ConfigureAwait(false);
				await Task.WhenAll(keys.Select(key => Memcached.Client.RemoveAsync(this._GetKey(key), cancellationToken))).ConfigureAwait(false);
				await Task.WhenAll(
					Memcached.Client.RemoveAsync(this._RegionKey, cancellationToken),
					this._storeKeys ? Task.Run(() => this._ClearKeys()) : Task.CompletedTask
				).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this.Name, $"Error occurred while cleaning => {ex.Message}", ex);
			}
			finally
			{
				this._lock.Release();
				await Memcached.Client.RemoveAsync(flag, cancellationToken).ConfigureAwait(false);
			}
		}
		#endregion

		#region Set Members
		HashSet<string> _GetSetMembers(string key)
		{
			try
			{
				var firstBlock = Memcached.Client.Get<byte[]>(this._GetKey(key));
				return firstBlock != null && firstBlock.Length > 8 && Helper.GetFlags(firstBlock).Item1.Equals(Helper.FlagOfFirstFragmentBlock)
					? this._GetFromFragments(key, firstBlock) as HashSet<string> ?? new HashSet<string>()
					: new HashSet<string>();
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this.Name, $"Error occurred while getting a set object [{key}]", ex);
				return new HashSet<string>();
			}
		}

		async Task<HashSet<string>> _GetSetMembersAsync(string key, CancellationToken cancellationToken = default)
		{
			try
			{
				var firstBlock = await Memcached.Client.GetAsync<byte[]>(this._GetKey(key), cancellationToken).ConfigureAwait(false);
				return firstBlock != null && firstBlock.Length > 8 && Helper.GetFlags(firstBlock).Item1.Equals(Helper.FlagOfFirstFragmentBlock)
					? await this._GetFromFragmentsAsync(key, firstBlock, cancellationToken).ConfigureAwait(false) as HashSet<string> ?? new HashSet<string>()
					: new HashSet<string>();
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this.Name, $"Error occurred while getting a set object [{key}]", ex);
				return new HashSet<string>();
			}
		}

		bool _AddSetMember(string key, string value, int expirationTime = 0, StoreMode mode = StoreMode.Set)
			=> this._AddSetMembers(key, new[] { value }, expirationTime, mode);

		bool _AddSetMembers(string key, IEnumerable<string> values, int expirationTime = 0, StoreMode mode = StoreMode.Set)
		{
			try
			{
				var set = this._GetSetMembers(key);
				values?.ToList().ForEach(value => set.Add(value));
				return this._SetAsFragments(key, set, expirationTime, mode);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this.Name, $"Error occurred while updating a set object [{key}]", ex);
				return false;
			}
		}

		Task<bool> _AddSetMemberAsync(string key, string value, int expirationTime = 0, StoreMode mode = StoreMode.Set, CancellationToken cancellationToken = default)
			=> this._AddSetMembersAsync(key, new[] { value }, expirationTime, mode, cancellationToken);

		async Task<bool> _AddSetMembersAsync(string key, IEnumerable<string> values, int expirationTime = 0, StoreMode mode = StoreMode.Set, CancellationToken cancellationToken = default)
		{
			try
			{
				var set = await this._GetSetMembersAsync(key, cancellationToken).ConfigureAwait(false);
				values?.ToList().ForEach(value => set.Add(value));
				return await this._SetAsFragmentsAsync(key, set, expirationTime, mode, cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this.Name, $"Error occurred while updating a set object [{key}]", ex);
				return false;
			}
		}

		bool _RemoveSetMembers(string key, string value)
			=> this._RemoveSetMembers(key, new[] { value });

		bool _RemoveSetMembers(string key, IEnumerable<string> values)
		{
			var set = this._GetSetMembers(key);
			values?.ToList().ForEach(value => set.Remove(value));
			return this._SetAsFragments(key, set, 0, StoreMode.Set);
		}

		Task<bool> _RemoveSetMembersAsync(string key, string value, CancellationToken cancellationToken = default)
			=> this._RemoveSetMembersAsync(key, new[] { value }, cancellationToken);

		async Task<bool> _RemoveSetMembersAsync(string key, IEnumerable<string> values, CancellationToken cancellationToken = default)
		{
			var set = await this._GetSetMembersAsync(key, cancellationToken).ConfigureAwait(false);
			values?.ToList().ForEach(value => set.Remove(value));
			return await this._SetAsFragmentsAsync(key, set, 0, StoreMode.Set, cancellationToken).ConfigureAwait(false);
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
		internal static async Task<bool> SetKeysAsync(string key, HashSet<string> keys, CancellationToken cancellationToken = default)
		{
			var fragments = CacheUtils.Helper.Split(Helper.Serialize(keys, false), Helper.FragmentSize).ToList();
			var success = await Memcached.Client.StoreAsync(StoreMode.Set, key, new ArraySegment<byte>(CacheUtils.Helper.Concat(new[] { BitConverter.GetBytes(fragments.Count), fragments[0] })), cancellationToken).ConfigureAwait(false);
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
			data = CacheUtils.Helper.Concat(fragments);
			try
			{
				return Helper.Deserialize(data, 0, data.Length) as HashSet<string>;
			}
			catch
			{
				return new HashSet<string>();
			}
		}

		internal static async Task<HashSet<string>> FetchKeysAsync(string key, CancellationToken cancellationToken = default)
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
			data = CacheUtils.Helper.Concat(fragments);
			try
			{
				return Helper.Deserialize(data, 0, data.Length) as HashSet<string>;
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
		public static Task<HashSet<string>> GetRegionsAsync(CancellationToken cancellationToken = default)
			=> Memcached.FetchKeysAsync(Helper.RegionsKey, cancellationToken);

		static async Task RegisterRegionAsync(string name, CancellationToken cancellationToken = default)
		{
			var flag = $"{Helper.RegionsKey}-Registering";
			try
			{
				var attempt = 0;
				while (attempt < 123 && await Memcached.Client.GetAsync(flag, cancellationToken).ConfigureAwait(false) != null)
				{
					await Task.Delay(123, cancellationToken).ConfigureAwait(false);
					attempt++;
				}
				await Memcached.Client.StoreAsync(StoreMode.Set, flag, "v", cancellationToken).ConfigureAwait(false);

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
				Helper.WriteLogs(name, $"Error occurred while registering the region => {ex.Message}", ex);
			}
			finally
			{
				await Memcached.Client.RemoveAsync(flag, cancellationToken).ConfigureAwait(false);
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
		public HashSet<string> GetKeys()
			=> this._GetKeys();

		/// <summary>
		/// Gets the collection of keys that associates with the cached items
		/// </summary>
		public Task<HashSet<string>> GetKeysAsync(CancellationToken cancellationToken = default)
			=> this._GetKeysAsync(cancellationToken);
		#endregion

		#region [Public] Set
		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Set(string key, object value, int expirationTime = 0)
			=> this._Set(key, value, expirationTime, true, StoreMode.Set);

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Set(string key, object value, TimeSpan validFor)
			=> this._Set(key, value, validFor, true, StoreMode.Set);

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Set(string key, object value, DateTime expiresAt)
			=> this._Set(key, value, expiresAt, true, StoreMode.Set);

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetAsync(string key, object value, int expirationTime = 0, CancellationToken cancellationToken = default)
			=> this._SetAsync(key, value, expirationTime, true, StoreMode.Set, cancellationToken);

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
			=> this._SetAsync(key, value, validFor, true, StoreMode.Set, cancellationToken);

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetAsync(string key, object value, DateTime expiresAt, CancellationToken cancellationToken = default)
			=> this._SetAsync(key, value, expiresAt, true, StoreMode.Set, cancellationToken);
		#endregion

		#region [Public] Set (Multiple)
		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		public void Set(IDictionary<string, object> items, string keyPrefix = null, int expirationTime = 0)
			=> this._Set(items, keyPrefix, expirationTime, StoreMode.Set);

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <typeparam name="T">The type for casting the cached item</typeparam>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		public void Set<T>(IDictionary<string, T> items, string keyPrefix = null, int expirationTime = 0)
			=> this._Set(items, keyPrefix, expirationTime, StoreMode.Set);

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		public Task SetAsync(IDictionary<string, object> items, string keyPrefix = null, int expirationTime = 0, CancellationToken cancellationToken = default)
			=> this._SetAsync(items, keyPrefix, expirationTime, StoreMode.Set, cancellationToken);

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <param name="items">The collection of items to add</param>
		public Task SetAsync(IDictionary<string, object> items, CancellationToken cancellationToken)
			=> this.SetAsync(items, null, 0, cancellationToken);

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <typeparam name="T">The type for casting the cached item</typeparam>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		public Task SetAsync<T>(IDictionary<string, T> items, string keyPrefix = null, int expirationTime = 0, CancellationToken cancellationToken = default)
			=> this._SetAsync(items, keyPrefix, expirationTime, StoreMode.Set, cancellationToken);

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <typeparam name="T">The type for casting the cached item</typeparam>
		/// <param name="items">The collection of items to add</param>
		public Task SetAsync<T>(IDictionary<string, T> items, CancellationToken cancellationToken)
			=> this.SetAsync(items, null, 0, cancellationToken);
		#endregion

		#region [Public] Set (Fragment)
		/// <summary>
		/// Adds an item (as fragments) into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="fragments">The collection that contains all fragments (object that serialized as binary - array bytes)</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool SetFragments(string key, List<byte[]> fragments, int expirationTime = 0)
			=> this._SetFragments(key, fragments, expirationTime, StoreMode.Set);

		/// <summary>
		/// Adds an item (as fragments) into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="fragments">The collection that contains all fragments (object that serialized as binary - array bytes)</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetFragmentsAsync(string key, List<byte[]> fragments, int expirationTime = 0, CancellationToken cancellationToken = default)
			=> this._SetFragmentsAsync(key, fragments, expirationTime, StoreMode.Set, cancellationToken);

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
			=> this._SetAsFragments(key, value, expirationTime, StoreMode.Set);

		/// <summary>
		/// Serializes object into array of bytes, splits into one or more fragments and updates into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetAsFragmentsAsync(string key, object value, int expirationTime = 0, CancellationToken cancellationToken = default)
			=> this._SetAsFragmentsAsync(key, value, expirationTime, StoreMode.Set, cancellationToken);

		/// <summary>
		/// Serializes object into array of bytes, splits into one or more fragments and updates into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetAsFragmentsAsync(string key, object value, CancellationToken cancellationToken)
			=> this.SetAsFragmentsAsync(key, value, 0, cancellationToken);
		#endregion

		#region [Public] Add
		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Add(string key, object value, int expirationTime = 0)
			=> this._Set(key, value, TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime), true, StoreMode.Add);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Add(string key, object value, TimeSpan validFor)
			=> this._Set(key, value, validFor, true, StoreMode.Add);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Add(string key, object value, DateTime expiresAt)
			=> this._Set(key, value, expiresAt, true, StoreMode.Add);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> AddAsync(string key, object value, int expirationTime = 0, CancellationToken cancellationToken = default)
			=> this._SetAsync(key, value, TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime), true, StoreMode.Add, cancellationToken);

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
			=> this._SetAsync(key, value, validFor, true, StoreMode.Add, cancellationToken);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> AddAsync(string key, object value, DateTime expiresAt, CancellationToken cancellationToken = default)
			=> this._SetAsync(key, value, expiresAt, true, StoreMode.Add, cancellationToken);
		#endregion

		#region [Public] Replace
		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Replace(string key, object value, int expirationTime = 0)
			=> this._Set(key, value, TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime), true, StoreMode.Replace);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Replace(string key, object value, TimeSpan validFor)
			=> this._Set(key, value, validFor, true, StoreMode.Replace);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Replace(string key, object value, DateTime expiresAt)
			=> this._Set(key, value, expiresAt, true, StoreMode.Replace);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> ReplaceAsync(string key, object value, int expirationTime = 0, CancellationToken cancellationToken = default)
			=> this._SetAsync(key, value, TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime), true, StoreMode.Replace, cancellationToken);

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
			=> this._SetAsync(key, value, validFor, true, StoreMode.Replace, cancellationToken);

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> ReplaceAsync(string key, object value, DateTime expiresAt, CancellationToken cancellationToken = default)
			=> this._SetAsync(key, value, expiresAt, true, StoreMode.Replace, cancellationToken);
		#endregion

		#region [Public] Refresh
		/// <summary>
		/// Refreshs an existed item
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <returns>Returns a boolean value indicating if the item is refreshed or not</returns>
		public bool Refresh(string key)
		{
			var value = this.Get(key);
			return value != null
				? this.Set(key, value)
				: false;
		}

		/// <summary>
		/// Refreshs an existed item
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <returns>Returns a boolean value indicating if the item is refreshed or not</returns>
		public async Task<bool> RefreshAsync(string key, CancellationToken cancellationToken = default)
		{
			var value = await this.GetAsync(key, cancellationToken).ConfigureAwait(false);
			return value != null
				? await this.SetAsync(key, value, cancellationToken).ConfigureAwait(false)
				: false;
		}
		#endregion

		#region [Public] Get
		/// <summary>
		/// Retreives a cached item
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to retreive</param>
		/// <returns>The retrieved cache item, or a null reference if the key is not found</returns>
		public object Get(string key)
			=> this._Get(key);

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
				: default;
		}

		/// <summary>
		/// Retreives a cached item
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to retreive</param>
		/// <returns>The retrieved cache item, or a null reference if the key is not found</returns>
		public Task<object> GetAsync(string key, CancellationToken cancellationToken = default)
			=> this._GetAsync(key, true, cancellationToken);

		/// <summary>
		/// Retreives a cached item
		/// </summary>
		/// <typeparam name="T">The type for casting the cached item</typeparam>
		/// <param name="key">The string that presents key of cached item need to retreive</param>
		/// <returns>The retrieved cache item, or a null reference if the key is not found</returns>
		public async Task<T> GetAsync<T>(string key, CancellationToken cancellationToken = default)
		{
			var @object = await this.GetAsync(key, cancellationToken).ConfigureAwait(false);
			return @object != null && @object is T
				? (T)@object
				: default;
		}
		#endregion

		#region [Public] Get (Multiple)
		/// <summary>
		/// Retreives a collection of cached items
		/// </summary>
		/// <param name="keys">The collection of items' keys</param>
		/// <returns>The collection of cache items</returns>
		public IDictionary<string, object> Get(IEnumerable<string> keys)
			=> this._Get(keys);

		/// <summary>
		/// Retreives a collection of cached items
		/// </summary>
		/// <param name="keys">The collection of items' keys</param>
		/// <returns>The collection of cache items</returns>
		public Task<IDictionary<string, object>> GetAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
			=> this._GetAsync(keys, cancellationToken);

		/// <summary>
		/// Retreives a collection of cached items
		/// </summary>
		/// <param name="keys">The collection of items' keys</param>
		/// <returns>The collection of cache items</returns>
		public IDictionary<string, T> Get<T>(IEnumerable<string> keys)
			=> this._Get<T>(keys);

		/// <summary>
		/// Retreives a collection of cached items
		/// </summary>
		/// <param name="keys">The collection of items' keys</param>
		/// <returns>The collection of cache items</returns>
		public Task<IDictionary<string, T>> GetAsync<T>(IEnumerable<string> keys, CancellationToken cancellationToken = default)
			=> this._GetAsync<T>(keys, cancellationToken);
		#endregion

		#region [Public] Get (Fragment)
		/// <summary>
		/// Gets fragment information that associates with the key
		/// </summary>
		/// <param name="key">The string that presents key of fragment information</param>
		/// <returns>The information of fragments, first element is total number of fragments, second element is total length of data</returns>
		public Tuple<int, int> GetFragments(string key)
			=> this._GetFragments(key);

		/// <summary>
		/// Gets fragment information that associates with the key
		/// </summary>
		/// <param name="key">The string that presents key of fragment information</param>
		/// <returns>The information of fragments, first element is total number of fragments, second element is total length of data</returns>
		public Task<Tuple<int, int>> GetFragmentsAsync(string key, CancellationToken cancellationToken = default)
			=> this._GetFragmentsAsync(key, cancellationToken);

		/// <summary>
		/// Gets cached of fragmented items that associates with the key and indexes
		/// </summary>
		/// <param name="key">The string that presents key of all fragmented items</param>
		/// <param name="indexes">The collection that presents indexes of all fragmented items need to get</param>
		/// <returns>The collection of array of bytes that presents serialized information of fragmented items</returns>
		public List<byte[]> GetAsFragments(string key, List<int> indexes)
			=> this._GetAsFragments(key, indexes);

		/// <summary>
		/// Gets cached of fragmented items that associates with the key and indexes
		/// </summary>
		/// <param name="key">The string that presents key of all fragmented items</param>
		/// <param name="indexes">The collection that presents indexes of all fragmented items need to get</param>
		/// <returns>The collection of array of bytes that presents serialized information of fragmented items</returns>
		public Task<List<byte[]>> GetAsFragmentsAsync(string key, List<int> indexes, CancellationToken cancellationToken = default)
			=> this._GetAsFragmentsAsync(key, indexes, cancellationToken);

		/// <summary>
		/// Gets cached of fragmented items that associates with the key and indexes
		/// </summary>
		/// <param name="key">The string that presents key of all fragmented items</param>
		/// <param name="indexes">The collection that presents indexes of all fragmented items need to get</param>
		/// <returns>The collection of array of bytes that presents serialized information of fragmented items</returns>
		public List<byte[]> GetAsFragments(string key, params int[] indexes)
			=> this._GetAsFragments(key, indexes);

		/// <summary>
		/// Gets cached of fragmented items that associates with the key and indexes
		/// </summary>
		/// <param name="key">The string that presents key of all fragmented items</param>
		/// <param name="indexes">The collection that presents indexes of all fragmented items need to get</param>
		/// <returns>The collection of array of bytes that presents serialized information of fragmented items</returns>
		public Task<List<byte[]>> GetAsFragmentsAsync(string key, CancellationToken cancellationToken = default, params int[] indexes)
			=> this._GetAsFragmentsAsync(key, cancellationToken, indexes);
		#endregion

		#region [Public] Remove
		/// <summary>
		/// Removes a cached item
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to remove</param>
		/// <returns>Returns a boolean value indicating if the item is removed or not</returns>
		public bool Remove(string key)
			=> this._Remove(key, true);

		/// <summary>
		/// Removes a cached item
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to remove</param>
		/// <returns>Returns a boolean value indicating if the item is removed or not</returns>
		public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
			=> this._RemoveAsync(key, true, cancellationToken);
		#endregion

		#region [Public] Remove (Multiple)
		/// <summary>
		/// Removes a collection of cached items
		/// </summary>
		/// <param name="keys">The collection that presents key of cached items need to remove</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		public void Remove(IEnumerable<string> keys, string keyPrefix = null)
			=> this._Remove(keys, keyPrefix);

		/// <summary>
		/// Removes a collection of cached items
		/// </summary>
		/// <param name="keys">The collection that presents key of cached items need to remove</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		public Task RemoveAsync(IEnumerable<string> keys, string keyPrefix = null, CancellationToken cancellationToken = default)
			=> this._RemoveAsync(keys, keyPrefix, cancellationToken);

		/// <summary>
		/// Removes a collection of cached items
		/// </summary>
		/// <param name="keys">The collection that presents key of cached items need to remove</param>
		public Task RemoveAsync(IEnumerable<string> keys, CancellationToken cancellationToken)
			=> this.RemoveAsync(keys, null, cancellationToken);
		#endregion

		#region [Public] Remove (Fragment)
		/// <summary>
		/// Removes a cached item (with first 100 fragments) from cache storage
		/// </summary>
		/// <param name="key">The string that presents key of fragmented items need to be removed</param>
		public void RemoveFragments(string key)
			=> this._Remove(this._GetFragmentKeys(key, 100), null);

		/// <summary>
		/// Removes a cached item (with first 100 fragments) from cache storage
		/// </summary>
		/// <param name="key">The string that presents key of fragmented items need to be removed</param>
		public Task RemoveFragmentsAsync(string key, CancellationToken cancellationToken = default)
			=> this._RemoveAsync(this._GetFragmentKeys(key, 100), null, cancellationToken);
		#endregion

		#region [Public] Exists
		/// <summary>
		/// Determines whether an item exists in the cache
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to check</param>
		/// <returns>Returns a boolean value indicating if the object that associates with the key is cached or not</returns>
		public bool Exists(string key)
			=> Memcached.Client.Exists(this._GetKey(key));

		/// <summary>
		/// Determines whether an item exists in the cache
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to check</param>
		/// <returns>Returns a boolean value indicating if the object that associates with the key is cached or not</returns>
		public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
			=> Memcached.Client.ExistsAsync(this._GetKey(key), cancellationToken);
		#endregion

		#region [Public] Clear
		/// <summary>
		/// Clears the cache storage of this isolated region
		/// </summary>
		public void Clear()
			=> this._Clear();

		/// <summary>
		/// Clears the cache storage of this isolated region
		/// </summary>
		public Task ClearAsync(CancellationToken cancellationToken = default)
			=> this._ClearAsync(cancellationToken);
		#endregion

		#region [Public] Set Members
		/// <summary>
		/// Gets a set
		/// </summary>
		/// <param name="key"></param>
		/// <returns></returns>
		public HashSet<string> GetSetMembers(string key)
			=> this._GetSetMembers(key);

		/// <summary>
		/// Gets a set
		/// </summary>
		/// <param name="key"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public Task<HashSet<string>> GetSetMembersAsync(string key, CancellationToken cancellationToken = default)
			=> this._GetSetMembersAsync(key, cancellationToken);

		/// <summary>
		/// Adds a value into a set
		/// </summary>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <returns></returns>
		public bool AddSetMember(string key, string value)
			=> this._AddSetMember(key, value, 0, StoreMode.Set);

		/// <summary>
		/// Adds the values into a set
		/// </summary>
		/// <param name="key"></param>
		/// <param name="values"></param>
		/// <returns></returns>
		public bool AddSetMembers(string key, IEnumerable<string> values)
			=> this._AddSetMembers(key, values, 0, StoreMode.Set);

		/// <summary>
		/// Adds a value into a set
		/// </summary>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public Task<bool> AddSetMemberAsync(string key, string value, CancellationToken cancellationToken = default)
			=> this._AddSetMemberAsync(key, value, 0, StoreMode.Set, cancellationToken);

		/// <summary>
		/// Adds the values into a set
		/// </summary>
		/// <param name="key"></param>
		/// <param name="values"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public Task<bool> AddSetMembersAsync(string key, IEnumerable<string> values, CancellationToken cancellationToken = default)
			=> this._AddSetMembersAsync(key, values, 0, StoreMode.Set, cancellationToken);

		/// <summary>
		/// Removes a value from a set
		/// </summary>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <returns></returns>
		public bool RemoveSetMembers(string key, string value)
			=> this._RemoveSetMembers(key, value);

		/// <summary>
		/// Removes the values from a set
		/// </summary>
		/// <param name="key"></param>
		/// <param name="values"></param>
		/// <returns></returns>
		public bool RemoveSetMembers(string key, IEnumerable<string> values)
			=> this._RemoveSetMembers(key, values);

		/// <summary>
		/// Removes a value from a set
		/// </summary>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public Task<bool> RemoveSetMembersAsync(string key, string value, CancellationToken cancellationToken = default)
			=> this._RemoveSetMembersAsync(key, value, cancellationToken);

		/// <summary>
		/// Removes the values from a set
		/// </summary>
		/// <param name="key"></param>
		/// <param name="values"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public Task<bool> RemoveSetMembersAsync(string key, IEnumerable<string> values, CancellationToken cancellationToken = default)
			=> this._RemoveSetMembersAsync(key, values, cancellationToken);
		#endregion

	}
}