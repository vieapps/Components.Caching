#region Related components
using System;
using System.Linq;
using System.Collections.Generic;
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
	/// Manipulates cached objects in isolated regions with memcached
	/// </summary>
	[DebuggerDisplay("Memcached: {Name} ({ExpirationTime} minutes)")]
	public sealed class Memcached : ICacheProvider
	{
		/// <summary>
		/// Create new instance of memcached
		/// </summary>
		/// <param name="name">The string that presents name of isolated region of the cache</param>
		/// <param name="expirationTime">The number that presents times (in minutes) for caching an item</param>
		/// <param name="storeKeys">true to active store keys of the region (to clear or using with other purpose further)</param>
		public Memcached(string name, int expirationTime, bool storeKeys)
		{
			// region name
			this._name = Helper.GetRegionName(name);

			// expiration time
			this._expirationTime = expirationTime > 0
				? expirationTime
				: Helper.ExpirationTime;

			// update keys
			if (storeKeys)
			{
				this._storeKeys = true;
				this._addedKeys = new HashSet<string>();
				this._removedKeys = new HashSet<string>();
			}

			// register the region
			Task.Run(async () => await Memcached.RegisterRegionAsync(this.Name).ConfigureAwait(false)).ConfigureAwait(false);
		}

		#region Get client (singleton)
		internal static MemcachedClient GetClient(CacheConfiguration configuration, ILoggerFactory loggerFactory = null)
		{
			if (configuration == null)
				throw new ArgumentNullException(nameof(configuration));

			var logger = loggerFactory?.CreateLogger<Cache>();
			if (logger != null && logger.IsEnabled(LogLevel.Debug))
				logger.LogInformation("Create new an instance of memcached with integrated configuration (appsettings.json)");

			return new MemcachedClient(loggerFactory, configuration.GetMemcachedConfiguration(loggerFactory));
		}

		internal static MemcachedClient GetClient(ILoggerFactory loggerFactory = null)
		{
			var memcachedSection = ConfigurationManager.GetSection("memcached") as MemcachedClientConfigurationSectionHandler;
			if (memcachedSection != null)
			{
				var logger = loggerFactory?.CreateLogger<Cache>();
				if (logger != null && logger.IsEnabled(LogLevel.Debug))
					logger.LogInformation("Create new an instance of memcached with stand-alone configuration (app.config/web.config) at the section named 'memcached'");

				return new MemcachedClient(memcachedSection, loggerFactory);
			}
			else if (ConfigurationManager.GetSection("cache") is CacheConfigurationSectionHandler cacheSection)
			{
				var logger = loggerFactory?.CreateLogger<Cache>();
				if (logger != null && logger.IsEnabled(LogLevel.Debug))
					logger.LogInformation("Create new an instance of memcached with stand-alone configuration (app.config/web.config) at the section named 'cache'");

				return new MemcachedClient(loggerFactory, (new CacheConfiguration(cacheSection)).GetMemcachedConfiguration(loggerFactory));
			}
			else
				throw new ConfigurationErrorsException("The configuration file (app.config/web.config) must have a section named 'memcached' or 'cache'!");
		}

		static MemcachedClient _Client;

		/// <summary>
		/// Gets the instance of memcached client
		/// </summary>
		public static MemcachedClient Client
		{
			get
			{
				return Memcached._Client ?? (Memcached._Client = Memcached.GetClient());
			}
			internal set
			{
				Memcached._Client = value;
			}
		}
		#endregion

		#region Attributes
		string _name;
		int _expirationTime;
		bool _storeKeys, _isUpdatingKeys = false;
		HashSet<string> _addedKeys, _removedKeys;
		ReaderWriterLockSlim _locker = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
		#endregion

		#region Keys
		async Task _UpdateKeysAsync(bool checkUpdatedKeys = true, Action callback = null)
		{
			// no key need to update, then stop
			if (checkUpdatedKeys && this._addedKeys.Count < 1 && this._removedKeys.Count < 1)
				return;

			// if other task/thread is processing, then stop
			if (this._isUpdatingKeys)
				return;

			// set flag
			this._isUpdatingKeys = true;

			// prepare
			var syncKeys = await Memcached.FetchKeysAsync(this._RegionKey);

			var totalRemovedKeys = this._removedKeys.Count;
			var totalAddedKeys = this._addedKeys.Count;

			// update removed keys
			if (totalRemovedKeys > 0)
				syncKeys = new HashSet<string>(syncKeys.Except(this._removedKeys));

			// update added keys
			if (totalAddedKeys > 0)
				syncKeys = new HashSet<string>(syncKeys.Union(this._addedKeys));

			// update keys
			await Memcached.SetKeysAsync(this._RegionKey, syncKeys);

			// delay a moment before re-checking
			await Task.Delay(123);

			// check to see new updated keys
			if (!totalAddedKeys.Equals(this._addedKeys.Count) || !totalRemovedKeys.Equals(this._removedKeys.Count))
			{
				// delay a moment before re-updating
				await Task.Delay(345);

				// update keys
				await Memcached.SetKeysAsync(this._RegionKey, new HashSet<string>(syncKeys.Except(this._removedKeys).Union(this._addedKeys)));
			}

			// clear keys
			try
			{
				this._locker.EnterWriteLock();
				this._addedKeys.Clear();
				this._removedKeys.Clear();
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this.Name, "Error occurred while removes keys (after pushing process)", ex);
			}
			finally
			{
				if (this._locker.IsWriteLockHeld)
					this._locker.ExitWriteLock();
			}

			// remove flags
			this._isUpdatingKeys = false;

			// callback
			callback?.Invoke();
		}

		void _UpdateKeys(int delay = 13, bool checkUpdatedKeys = true, Action callback = null)
		{
			Task.Run(async () =>
			{
				await Task.Delay(delay);
				await this._UpdateKeysAsync(checkUpdatedKeys, callback).ConfigureAwait(false);
			}).ConfigureAwait(false);
		}

		void _UpdateKeys(string key, bool doPush)
		{
			// update added keys
			if (!this._storeKeys)
				return;

			if (!this._addedKeys.Contains(key))
				try
				{
					this._locker.EnterWriteLock();
					this._addedKeys.Add(key);
				}
				catch (Exception ex)
				{
					Helper.WriteLogs(this.Name, "Error occurred while updating the added keys of the region (for pushing)", ex);
				}
				finally
				{
					if (this._locker.IsWriteLockHeld)
						this._locker.ExitWriteLock();
				}

			if (doPush && this._addedKeys.Count > 0)
				this._UpdateKeys(113);
		}

		HashSet<string> _GetKeys()
		{
			return Memcached.FetchKeys(this._RegionKey);
		}

		Task<HashSet<string>> _GetKeysAsync()
		{
			return Memcached.FetchKeysAsync(this._RegionKey);
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
				this._UpdateKeys(key, doPush);

			return success;
		}

		bool _Set(string key, object value, int expirationTime = 0, bool doPush = true, StoreMode mode = StoreMode.Set)
		{
			return this._Set(key, value, TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime), doPush, mode);
		}

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
				this._UpdateKeys(key, doPush);

			return success;
		}

		async Task<bool> _SetAsync(string key, object value, TimeSpan validFor, bool doPush = true, StoreMode mode = StoreMode.Set)
		{
			var success = false;
			if (!string.IsNullOrWhiteSpace(key) && value != null)
				try
				{
					success = await Memcached.Client.StoreAsync(mode, this._GetKey(key), value, validFor);
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
				this._UpdateKeys(key, doPush);

			return success;
		}

		Task<bool> _SetAsync(string key, object value, int expirationTime = 0, bool doPush = true, StoreMode mode = StoreMode.Set)
		{
			return this._SetAsync(key, value, TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime), doPush, mode);
		}

		async Task<bool> _SetAsync(string key, object value, DateTime expiresAt, bool doPush = true, StoreMode mode = StoreMode.Set)
		{
			var success = false;
			if (!string.IsNullOrWhiteSpace(key) && value != null)
				try
				{
					success = await Memcached.Client.StoreAsync(mode, this._GetKey(key), value, expiresAt);
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
				this._UpdateKeys(key, doPush);

			return success;
		}
		#endregion

		#region Set (Multiple)
		void _Set<T>(IDictionary<string, T> items, string keyPrefix = null, int expirationTime = 0, StoreMode mode = StoreMode.Set)
		{
			if (items != null)
				items.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key)).ToList().ForEach(kvp => this._Set((string.IsNullOrWhiteSpace(keyPrefix) ? "" : keyPrefix) + kvp.Key, kvp.Value, expirationTime, false, mode));
			if (this._storeKeys && this._addedKeys.Count > 0)
				this._UpdateKeys(123);
		}

		void _Set(IDictionary<string, object> items, string keyPrefix = null, int expirationTime = 0, StoreMode mode = StoreMode.Set)
		{
			this._Set<object>(items, keyPrefix, expirationTime, mode);
		}

		async Task _SetAsync<T>(IDictionary<string, T> items, string keyPrefix = null, int expirationTime = 0, StoreMode mode = StoreMode.Set)
		{
			await Task.WhenAll(items != null
				? items.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key)).Select(kvp => this._SetAsync((string.IsNullOrWhiteSpace(keyPrefix) ? "" : keyPrefix) + kvp.Key, kvp.Value, expirationTime, false, mode))
				: new List<Task<bool>>());
			if (this._storeKeys && this._addedKeys.Count > 0)
				this._UpdateKeys(123);
		}

		Task _SetAsync(IDictionary<string, object> items, string keyPrefix = null, int expirationTime = 0, StoreMode mode = StoreMode.Set)
		{
			return this._SetAsync<object>(items, keyPrefix, expirationTime, mode);
		}
		#endregion

		#region Set (Fragment)
		bool _SetFragments(string key, List<byte[]> fragments, int expirationTime = 0, StoreMode mode = StoreMode.Set)
		{
			var success = fragments != null && fragments.Count > 0
				? this._Set(key, new ArraySegment<byte>(Helper.GetFirstBlock(fragments)), expirationTime, false, mode)
				: false;

			if (success)
			{
				if (fragments.Count > 1)
				{
					var items = new Dictionary<string, object>();
					for (var index = 1; index < fragments.Count; index++)
						items.Add(this._GetFragmentKey(key, index), new ArraySegment<byte>(fragments[index]));
					this._Set(items, null, expirationTime, mode);
				}
				else
					this._UpdateKeys(key, true);
			}

			return success;
		}

		async Task<bool> _SetFragmentsAsync(string key, List<byte[]> fragments, int expirationTime = 0, StoreMode mode = StoreMode.Set)
		{
			var success = fragments != null && fragments.Count > 0
				? await this._SetAsync(key, new ArraySegment<byte>(Helper.GetFirstBlock(fragments)), expirationTime, false, mode)
				: false;

			if (success)
			{
				if (fragments.Count > 1)
				{
					var items = new Dictionary<string, object>();
					for (var index = 1; index < fragments.Count; index++)
						items.Add(this._GetFragmentKey(key, index), new ArraySegment<byte>(fragments[index]));
					await this._SetAsync(items, null, expirationTime, mode);
				}
				else
					this._UpdateKeys(key, true);
			}

			return success;
		}

		bool _SetAsFragments(string key, object value, int expirationTime = 0, StoreMode mode = StoreMode.Set)
		{
			return string.IsNullOrWhiteSpace(key) || value == null
				? false
				: this._SetFragments(key, Helper.Split(Helper.Serialize(value, false)), expirationTime, mode);
		}

		Task<bool> _SetAsFragmentsAsync(string key, object value, int expirationTime = 0, StoreMode mode = StoreMode.Set)
		{
			return string.IsNullOrWhiteSpace(key) || value == null
				? Task.FromResult(false)
				: this._SetFragmentsAsync(key, Helper.Split(Helper.Serialize(value, false)), expirationTime, mode);
		}
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

		async Task<object> _GetAsync(string key, bool autoGetFragments = true)
		{
			if (string.IsNullOrWhiteSpace(key))
				throw new ArgumentNullException(key);

			object value = null;
			try
			{
				value = await Memcached.Client.GetAsync(this._GetKey(key));
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this.Name, $"Error occurred while fetching an object from cache storage [{key}]", ex);
			}

			if (autoGetFragments && value != null && value is byte[] && (value as byte[]).Length > 8 && Helper.GetFlags(value as byte[]).Item1.Equals(Helper.FlagOfFirstFragmentBlock))
				try
				{
					value = await this._GetFromFragmentsAsync(key, value as byte[]);
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

			var objects = items?.ToDictionary(kvp => kvp.Key.Substring(this.Name.Length), kvp => kvp.Value);
			return objects != null && objects.Count > 0
				? objects
				: null;
		}

		IDictionary<string, T> _Get<T>(IEnumerable<string> keys)
		{
			return this._Get(keys)?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value is T ? (T)kvp.Value : default(T));
		}

		async Task<IDictionary<string, object>> _GetAsync(IEnumerable<string> keys)
		{
			if (keys == null)
				return null;

			IDictionary<string, object> items = null;
			try
			{
				items = await Memcached.Client.GetAsync(keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => this._GetKey(key)));
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

		async Task<IDictionary<string, T>> _GetAsync<T>(IEnumerable<string> keys)
		{
			return (await this._GetAsync(keys))?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value is T ? (T)kvp.Value : default(T));
		}
		#endregion

		#region Get (Fragment)
		Tuple<int, int> _GetFragments(string key)
		{
			var data = this._Get(key, false) as byte[];
			return data != null
				? Helper.GetFragments(data)
				: null;
		}

		async Task<Tuple<int, int>> _GetFragmentsAsync(string key)
		{
			var data = await this._GetAsync(key, false) as byte[];
			return data != null
				? Helper.GetFragments(data)
				: null;
		}

		List<byte[]> _GetAsFragments(string key, List<int> indexes)
		{
			if (string.IsNullOrWhiteSpace(key) || indexes == null || indexes.Count < 1)
				return new List<byte[]>();

			var fragments = Enumerable.Repeat(new byte[0], indexes.Count).ToList();
			Func<int, Task> func = async (index) =>
			{
				var fragmentIndex = indexes[index];
				fragments[index] = fragmentIndex < 0 ? null : await this._GetAsync(fragmentIndex > 0 ? this._GetFragmentKey(key, fragmentIndex) : key, false) as byte[];
			};
			var tasks = new List<Task>();
			for (var index = 0; index < indexes.Count; index++)
				tasks.Add(func(index));
			Task.WaitAll(tasks.ToArray(), 13000);

			return fragments;
		}

		async Task<List<byte[]>> _GetAsFragmentsAsync(string key, List<int> indexes)
		{
			if (string.IsNullOrWhiteSpace(key) || indexes == null || indexes.Count < 1)
				return new List<byte[]>();

			var fragments = Enumerable.Repeat(new byte[0], indexes.Count).ToList();
			Func<int, Task> func = async (index) =>
			{
				var fragmentIndex = indexes[index];
				fragments[index] = fragmentIndex < 0 ? null : await this._GetAsync(fragmentIndex > 0 ? this._GetFragmentKey(key, fragmentIndex) : key, false) as byte[];
			};
			var tasks = new List<Task>();
			for (var index = 0; index < indexes.Count; index++)
				tasks.Add(func(index));
			await Task.WhenAll(tasks);

			return fragments;
		}

		List<byte[]> _GetAsFragments(string key, params int[] indexes)
		{
			return string.IsNullOrWhiteSpace(key) || indexes == null || indexes.Length < 1
				? null
				: this._GetAsFragments(key, indexes.ToList());
		}

		Task<List<byte[]>> _GetAsFragmentsAsync(string key, params int[] indexes)
		{
			return string.IsNullOrWhiteSpace(key) || indexes == null || indexes.Length < 1
				? Task.FromResult<List<byte[]>>(null)
				: this._GetAsFragmentsAsync(key, indexes.ToList());
		}

		object _GetFromFragments(string key, byte[] firstBlock)
		{
			try
			{
				var info = Helper.GetFragments(firstBlock);
				var data = Helper.Combine(firstBlock, this._GetAsFragments(key, Enumerable.Range(1, info.Item1 - 1).ToList()));
				return Helper.Deserialize(data, 8, data.Length - 8);
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this.Name, $"Error occurred while serializing an object from fragmented data [{key}]", ex);
				return null;
			}
		}

		async Task<object> _GetFromFragmentsAsync(string key, byte[] firstBlock)
		{
			try
			{
				var info = Helper.GetFragments(firstBlock);
				var data = Helper.Combine(firstBlock, await this._GetAsFragmentsAsync(key, Enumerable.Range(1, info.Item1 - 1).ToList()));
				return Helper.Deserialize(data, 8, data.Length - 8);
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
			{
				if (!this._removedKeys.Contains(key))
					try
					{
						this._locker.EnterWriteLock();
						this._removedKeys.Add(key);
					}
					catch (Exception ex)
					{
						Helper.WriteLogs(this.Name, "Error occurred while updating the removed keys (for pushing)", ex);
					}
					finally
					{
						if (this._locker.IsWriteLockHeld)
							this._locker.ExitWriteLock();
					}

				if (doPush && this._removedKeys.Count > 0)
					this._UpdateKeys(123);
			}

			return success;
		}

		async Task<bool> _RemoveAsync(string key, bool doPush = true)
		{
			var success = false;
			if (!string.IsNullOrWhiteSpace(key))
				try
				{
					success = await Memcached.Client.RemoveAsync(this._GetKey(key));
				}
				catch (Exception ex)
				{
					Helper.WriteLogs(this.Name, $"Error occurred while removing an object from cache storage [{key}]", ex);
				}

			if (success)
			{
				if (!this._removedKeys.Contains(key))
					try
					{
						this._locker.EnterWriteLock();
						this._removedKeys.Add(key);
					}
					catch (Exception ex)
					{
						Helper.WriteLogs(this.Name, "Error occurred while updating the removed keys (for pushing)", ex);
					}
					finally
					{
						if (this._locker.IsWriteLockHeld)
							this._locker.ExitWriteLock();
					}

				if (doPush && this._removedKeys.Count > 0)
					this._UpdateKeys(123);
			}

			return success;
		}
		#endregion

		#region Remove (Multiple)
		void _Remove(IEnumerable<string> keys, string keyPrefix = null)
		{
			if (keys != null)
				keys.Where(key => !string.IsNullOrWhiteSpace(key)).ToList().ForEach(key => this._Remove((string.IsNullOrWhiteSpace(keyPrefix) ? "" : keyPrefix) + key, false));
			if (this._storeKeys && this._removedKeys.Count > 0)
				this._UpdateKeys(123);
		}

		async Task _RemoveAsync(IEnumerable<string> keys, string keyPrefix = null)
		{
			await Task.WhenAll(keys == null
				? keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => this._RemoveAsync((string.IsNullOrWhiteSpace(keyPrefix) ? "" : keyPrefix) + key, false))
				: new List<Task<bool>>()
			);
			if (this._storeKeys && this._removedKeys.Count > 0)
				this._UpdateKeys(123);
		}
		#endregion

		#region Remove (Fragment)
		void _RemoveFragments(string key, int max = 100)
		{
			this._Remove(this._GetFragmentKeys(key, max));
		}

		Task _RemoveFragmentsAsync(string key, int max = 100)
		{
			return this._RemoveAsync(this._GetFragmentKeys(key, max));
		}
		#endregion

		#region Clear
		void _Clear()
		{
			this._Remove(this._GetKeys());
			Memcached.Client.Remove(this._RegionKey);

			if (this._storeKeys)
				try
				{
					this._locker.EnterWriteLock();
					this._addedKeys.Clear();
					this._removedKeys.Clear();
				}
				catch { }
				finally
				{
					if (this._locker.IsWriteLockHeld)
						this._locker.ExitWriteLock();
				}
		}

		async Task _ClearAsync()
		{
			var keys = await this._GetKeysAsync();
			await Task.WhenAll(
				this._RemoveAsync(keys),
				Memcached.Client.RemoveAsync(this._RegionKey)
			);

			if (this._storeKeys)
				try
				{
					this._locker.EnterWriteLock();
					this._addedKeys.Clear();
					this._removedKeys.Clear();
				}
				catch { }
				finally
				{
					if (this._locker.IsWriteLockHeld)
						this._locker.ExitWriteLock();
				}
		}
		#endregion

		// -----------------------------------------------------

		#region [Helper]
		string _GetKey(string key)
		{
			return this.Name + "@" + key.Replace(" ", "-");
		}

		string _GetFragmentKey(string key, int index)
		{
			return key.Replace(" ", "-") + "$[Fragment<" + index.ToString() + ">]";
		}

		List<string> _GetFragmentKeys(string key, int max)
		{
			var keys = new List<string>() { key };
			for (var index = 1; index < max; index++)
				keys.Add(this._GetFragmentKey(key, index));
			return keys;
		}

		string _RegionKey
		{
			get
			{
				return this._GetKey("<Region-Keys>");
			}
		}
		#endregion

		#region [Static]
		internal static async Task<bool> SetKeysAsync(string key, HashSet<string> keys)
		{
			var fragments = Helper.Split(Helper.Serialize(keys, false));
			var success = await Memcached.Client.StoreAsync(StoreMode.Set, key, new ArraySegment<byte>(CacheUtils.Helper.Combine(BitConverter.GetBytes(fragments.Count), fragments[0])));
			if (success && fragments.Count > 1)
			{
				var tasks = new List<Task>();
				for (var index = 1; index < fragments.Count; index++)
					tasks.Add(Memcached.Client.StoreAsync(StoreMode.Set, key + ":" + index, new ArraySegment<byte>(fragments[index])));
				await Task.WhenAll(tasks);
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
				Func<int, Task> func = async (index) =>
				{
					fragments[index] = await Memcached.Client.GetAsync<byte[]>(key + ":" + index);
				};
				var tasks = new List<Task>();
				for (var index = 1; index < fragments.Count; index++)
					tasks.Add(func(index));
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

		internal static async Task<HashSet<string>> FetchKeysAsync(string key)
		{
			var data = await Memcached.Client.GetAsync<byte[]>(key);
			if (data == null || data.Length < 4)
				return new HashSet<string>();

			var tmp = new byte[4];
			Buffer.BlockCopy(data, 0, tmp, 0, 4);
			var fragments = Enumerable.Repeat(new byte[0], BitConverter.ToInt32(tmp, 0)).ToList();
			if (fragments.Count > 1)
			{
				Func<int, Task> func = async (index) =>
				{
					fragments[index] = await Memcached.Client.GetAsync<byte[]>(key + ":" + index);
				};
				var tasks = new List<Task>();
				for (var index = 1; index < fragments.Count; index++)
					tasks.Add(func(index));
				await Task.WhenAll(tasks);
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
		{
			return Memcached.FetchKeys(Helper.RegionsKey);
		}

		/// <summary>
		/// Gets the collection of all registered regions (in distributed cache)
		/// </summary>
		public static Task<HashSet<string>> GetRegionsAsync()
		{
			return Memcached.FetchKeysAsync(Helper.RegionsKey);
		}

		static async Task RegisterRegionAsync(string name)
		{
			var attempt = 0;
			while (attempt < 123 && await Memcached.Client.ExistsAsync(Helper.RegionsKey + "-Registering"))
			{
				await Task.Delay(234);
				attempt++;
			}
			await Memcached.Client.StoreAsync(StoreMode.Set, Helper.RegionsKey + "-Registering", "v", TimeSpan.FromSeconds(13));

			try
			{
				var regions = await Memcached.FetchKeysAsync(Helper.RegionsKey);
				if (!regions.Contains(name))
				{
					regions.Add(name);
					await Memcached.SetKeysAsync(Helper.RegionsKey, regions);
				}
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(name, $"Error occurred while registering a region: {ex.Message}", ex);
			}

			await Memcached.Client.RemoveAsync(Helper.RegionsKey + "-Registering");
		}
		#endregion

		// -----------------------------------------------------

		#region [Public] Properties
		/// <summary>
		/// Gets the name of the isolated region
		/// </summary>
		public string Name
		{
			get
			{
				return this._name;
			}
		}

		/// <summary>
		/// Gets the expiration time (in minutes)
		/// </summary>
		public int ExpirationTime
		{
			get
			{
				return this._expirationTime;
			}
		}

		/// <summary>
		/// Gets the collection of keys
		/// </summary>
		public HashSet<string> Keys
		{
			get
			{
				return this._GetKeys();
			}
		}
		#endregion

		#region [Public] Keys
		/// <summary>
		/// Gets the collection of keys that associates with the cached items
		/// </summary>
		public HashSet<string> GetKeys()
		{
			return this._GetKeys();
		}

		/// <summary>
		/// Gets the collection of keys that associates with the cached items
		/// </summary>
		public Task<HashSet<string>> GetKeysAsync()
		{
			return this._GetKeysAsync();
		}
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
		{
			return this._Set(key, value, expirationTime);
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
			return this._Set(key, value, validFor);
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
			return this._Set(key, value, expiresAt);
		}

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetAsync(string key, object value, int expirationTime = 0)
		{
			return this._SetAsync(key, value, expirationTime, true);
		}

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetAsync(string key, object value, TimeSpan validFor)
		{
			return this._SetAsync(key, value, validFor);
		}

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetAsync(string key, object value, DateTime expiresAt)
		{
			return this._SetAsync(key, value, expiresAt);
		}
		#endregion

		#region [Public] Set (Multiple)
		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		public void Set(IDictionary<string, object> items, string keyPrefix = null, int expirationTime = 0)
		{
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
			this._Set<T>(items, keyPrefix, expirationTime);
		}

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		public Task SetAsync(IDictionary<string, object> items, string keyPrefix = null, int expirationTime = 0)
		{
			return this._SetAsync(items, keyPrefix, expirationTime);
		}

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <typeparam name="T">The type for casting the cached item</typeparam>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		public Task SetAsync<T>(IDictionary<string, T> items, string keyPrefix = null, int expirationTime = 0)
		{
			return this._SetAsync<T>(items, keyPrefix, expirationTime);
		}
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
		{
			return this._SetFragments(key, fragments, expirationTime);
		}

		/// <summary>
		/// Adds an item (as fragments) into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="fragments">The collection that contains all fragments (object that serialized as binary - array bytes)</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetFragmentsAsync(string key, List<byte[]> fragments, int expirationTime = 0)
		{
			return this._SetFragmentsAsync(key, fragments, expirationTime);
		}

		/// <summary>
		/// Serializes object into array of bytes, splits into one or more fragments and updates into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool SetAsFragments(string key, object value, int expirationTime = 0)
		{
			return this._SetAsFragments(key, value, expirationTime);
		}

		/// <summary>
		/// Serializes object into array of bytes, splits into one or more fragments and updates into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetAsFragmentsAsync(string key, object value, int expirationTime = 0)
		{
			return this._SetAsFragmentsAsync(key, value, expirationTime);
		}
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
		{
			return this._Set(key, value, TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime), true, StoreMode.Add);
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
			return this._Set(key, value, validFor, true, StoreMode.Add);
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
			return this._Set(key, value, expiresAt, true, StoreMode.Add);
		}

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> AddAsync(string key, object value, int expirationTime = 0)
		{
			return this._SetAsync(key, value, TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime), true, StoreMode.Add);
		}

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> AddAsync(string key, object value, TimeSpan validFor)
		{
			return this._SetAsync(key, value, validFor, true, StoreMode.Add);
		}

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> AddAsync(string key, object value, DateTime expiresAt)
		{
			return this._SetAsync(key, value, expiresAt, true, StoreMode.Add);
		}
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
		{
			return this._Set(key, value, TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime), true, StoreMode.Replace);
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
			return this._Set(key, value, validFor, true, StoreMode.Replace);
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
			return this._Set(key, value, expiresAt, true, StoreMode.Replace);
		}

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> ReplaceAsync(string key, object value, int expirationTime = 0)
		{
			return this._SetAsync(key, value, TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime), true, StoreMode.Replace);
		}

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> ReplaceAsync(string key, object value, TimeSpan validFor)
		{
			return this._SetAsync(key, value, validFor, true, StoreMode.Replace);
		}

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expiresAt">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> ReplaceAsync(string key, object value, DateTime expiresAt)
		{
			return this._SetAsync(key, value, expiresAt, true, StoreMode.Replace);
		}
		#endregion

		#region [Public] Get
		/// <summary>
		/// Retreives a cached item
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to retreive</param>
		/// <returns>The retrieved cache item, or a null reference if the key is not found</returns>
		public object Get(string key)
		{
			return this._Get(key);
		}

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
		public Task<object> GetAsync(string key)
		{
			return this._GetAsync(key);
		}

		/// <summary>
		/// Retreives a cached item
		/// </summary>
		/// <typeparam name="T">The type for casting the cached item</typeparam>
		/// <param name="key">The string that presents key of cached item need to retreive</param>
		/// <returns>The retrieved cache item, or a null reference if the key is not found</returns>
		public async Task<T> GetAsync<T>(string key)
		{
			var @object = await this.GetAsync(key);
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
		public IDictionary<string, object> Get(IEnumerable<string> keys)
		{
			return this._Get(keys);
		}

		/// <summary>
		/// Retreives a collection of cached items
		/// </summary>
		/// <param name="keys">The collection of items' keys</param>
		/// <returns>The collection of cache items</returns>
		public Task<IDictionary<string, object>> GetAsync(IEnumerable<string> keys)
		{
			return this._GetAsync(keys);
		}

		/// <summary>
		/// Retreives a collection of cached items
		/// </summary>
		/// <param name="keys">The collection of items' keys</param>
		/// <returns>The collection of cache items</returns>
		public IDictionary<string, T> Get<T>(IEnumerable<string> keys)
		{
			return this._Get<T>(keys);
		}

		/// <summary>
		/// Retreives a collection of cached items
		/// </summary>
		/// <param name="keys">The collection of items' keys</param>
		/// <returns>The collection of cache items</returns>
		public Task<IDictionary<string, T>> GetAsync<T>(IEnumerable<string> keys)
		{
			return this._GetAsync<T>(keys);
		}
		#endregion

		#region [Public] Get (Fragment)
		/// <summary>
		/// Gets fragment information that associates with the key
		/// </summary>
		/// <param name="key">The string that presents key of fragment information</param>
		/// <returns>The information of fragments, first element is total number of fragments, second element is total length of data</returns>
		public Tuple<int, int> GetFragments(string key)
		{
			return this._GetFragments(key);
		}

		/// <summary>
		/// Gets fragment information that associates with the key
		/// </summary>
		/// <param name="key">The string that presents key of fragment information</param>
		/// <returns>The information of fragments, first element is total number of fragments, second element is total length of data</returns>
		public Task<Tuple<int, int>> GetFragmentsAsync(string key)
		{
			return this._GetFragmentsAsync(key);
		}

		/// <summary>
		/// Gets cached of fragmented items that associates with the key and indexes
		/// </summary>
		/// <param name="key">The string that presents key of all fragmented items</param>
		/// <param name="indexes">The collection that presents indexes of all fragmented items need to get</param>
		/// <returns>The collection of array of bytes that presents serialized information of fragmented items</returns>
		public List<byte[]> GetAsFragments(string key, List<int> indexes)
		{
			return this._GetAsFragments(key, indexes);
		}

		/// <summary>
		/// Gets cached of fragmented items that associates with the key and indexes
		/// </summary>
		/// <param name="key">The string that presents key of all fragmented items</param>
		/// <param name="indexes">The collection that presents indexes of all fragmented items need to get</param>
		/// <returns>The collection of array of bytes that presents serialized information of fragmented items</returns>
		public Task<List<byte[]>> GetAsFragmentsAsync(string key, List<int> indexes)
		{
			return this._GetAsFragmentsAsync(key, indexes);
		}

		/// <summary>
		/// Gets cached of fragmented items that associates with the key and indexes
		/// </summary>
		/// <param name="key">The string that presents key of all fragmented items</param>
		/// <param name="indexes">The collection that presents indexes of all fragmented items need to get</param>
		/// <returns>The collection of array of bytes that presents serialized information of fragmented items</returns>
		public List<byte[]> GetAsFragments(string key, params int[] indexes)
		{
			return this._GetAsFragments(key, indexes);
		}

		/// <summary>
		/// Gets cached of fragmented items that associates with the key and indexes
		/// </summary>
		/// <param name="key">The string that presents key of all fragmented items</param>
		/// <param name="indexes">The collection that presents indexes of all fragmented items need to get</param>
		/// <returns>The collection of array of bytes that presents serialized information of fragmented items</returns>
		public Task<List<byte[]>> GetAsFragmentsAsync(string key, params int[] indexes)
		{
			return this._GetAsFragmentsAsync(key, indexes);
		}
		#endregion

		#region [Public] Remove
		/// <summary>
		/// Removes a cached item
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to remove</param>
		/// <returns>Returns a boolean value indicating if the item is removed or not</returns>
		public bool Remove(string key)
		{
			return this._Remove(key);
		}

		/// <summary>
		/// Removes a cached item
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to remove</param>
		/// <returns>Returns a boolean value indicating if the item is removed or not</returns>
		public Task<bool> RemoveAsync(string key)
		{
			return this._RemoveAsync(key);
		}
		#endregion

		#region [Public] Remove (Multiple)
		/// <summary>
		/// Removes a collection of cached items
		/// </summary>
		/// <param name="keys">The collection that presents key of cached items need to remove</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		public void Remove(IEnumerable<string> keys, string keyPrefix = null)
		{
			this._Remove(keys, keyPrefix);
		}

		/// <summary>
		/// Removes a collection of cached items
		/// </summary>
		/// <param name="keys">The collection that presents key of cached items need to remove</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		public Task RemoveAsync(IEnumerable<string> keys, string keyPrefix = null)
		{
			return this._RemoveAsync(keys, keyPrefix);
		}
		#endregion

		#region [Public] Remove (Fragment)
		/// <summary>
		/// Removes a cached item (with first 100 fragments) from cache storage
		/// </summary>
		/// <param name="key">The string that presents key of fragmented items need to be removed</param>
		public void RemoveFragments(string key)
		{
			this._RemoveFragments(key);
		}

		/// <summary>
		/// Removes a cached item (with first 100 fragments) from cache storage
		/// </summary>
		/// <param name="key">The string that presents key of fragmented items need to be removed</param>
		public Task RemoveFragmentsAsync(string key)
		{
			return this._RemoveFragmentsAsync(key);
		}
		#endregion

		#region [Public] Exists
		/// <summary>
		/// Determines whether an item exists in the cache
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to check</param>
		/// <returns>Returns a boolean value indicating if the object that associates with the key is cached or not</returns>
		public bool Exists(string key)
		{
			return Memcached.Client.Exists(this._GetKey(key));
		}

		/// <summary>
		/// Determines whether an item exists in the cache
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to check</param>
		/// <returns>Returns a boolean value indicating if the object that associates with the key is cached or not</returns>
		public Task<bool> ExistsAsync(string key)
		{
			return Memcached.Client.ExistsAsync(this._GetKey(key));
		}
		#endregion

		#region [Public] Clear
		/// <summary>
		/// Clears the cache storage of this isolated region
		/// </summary>
		public void Clear()
		{
			this._Clear();
		}

		/// <summary>
		/// Clears the cache storage of this isolated region
		/// </summary>
		public Task ClearAsync()
		{
			return this._ClearAsync();
		}
		#endregion

	}
}