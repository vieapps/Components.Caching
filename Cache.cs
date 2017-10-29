#region Related components
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.IO.Compression;
using System.Runtime.Serialization.Formatters.Binary;
using System.Diagnostics;
using System.Configuration;

using Enyim.Caching;
using Enyim.Caching.Configuration;
using Enyim.Caching.Memcached;
#endregion

namespace net.vieapps.Components.Caching
{
	/// <summary>
	/// Manipulates cached objects in isolated regions
	/// </summary>
	[DebuggerDisplay("Name: {_name}, Caching Time (minutes): {_expirationTime}")]
	public sealed class Cache
	{

		[Serializable]
		public struct Fragment
		{
			public string Key;
			public string Type;
			public int TotalFragments;
		}

		#region Default
		static MemcachedClient _MemcachedClient = null;

		/// <summary>
		/// Gets the instance of memcached client
		/// </summary>
		public static MemcachedClient Memcached
		{
			get
			{
				if (Cache._MemcachedClient == null)
					Cache._MemcachedClient = new MemcachedClient(ConfigurationManager.GetSection("memcached") as MemcachedClientConfigurationSectionHandler);
				return Cache._MemcachedClient;
			}
		}

		/// <summary>
		/// Gets default expiration time (in minutes)
		/// </summary>
		public static readonly int DefaultExpirationTime = 30;

		/// <summary>
		/// Gets default size of one fragment (1 MBytes)
		/// </summary>
		public static readonly int DefaultFragmentSize = (1024 * 1024) - 512;
		#endregion

		#region Attributes
		string _name = "";
		int _expirationTime = Cache.DefaultExpirationTime;
		int _fragmentSize = Cache.DefaultFragmentSize;
		bool _updateKeys = false;
		HashSet<string> _addedKeys = null, _removedKeys = null;
		ReaderWriterLockSlim _lock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);

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
		#endregion

		/// <summary>
		/// Create instance of cache storage with default isolated region
		/// </summary>
		public Cache() : this(null) { }

		/// <summary>
		/// Create instance of cache storage with isolated region
		/// </summary>
		/// <param name="name">The string that presents name of isolated region that the cache manager will work with</param>
		/// <param name="expirationTime">Time in minutes to cache any item</param>
		/// <param name="updateKeys">true to active update keys when working mode is Distributed</param>
		public Cache(string name, int expirationTime = 0, bool updateKeys = false)
		{
			// region name
			this._name = string.IsNullOrWhiteSpace(name)
				? "VIEApps-Cache-Storage"
				: System.Text.RegularExpressions.Regex.Replace(name, "[^0-9a-zA-Z:-]+", "");

			// expiration time
			this._expirationTime = expirationTime > 0
				? expirationTime
				: Cache.DefaultExpirationTime;

			// update keys
			if (updateKeys)
			{
				this._updateKeys = true;
				this._addedKeys = new HashSet<string>();
				this._removedKeys = new HashSet<string>();
			}

			// register region
			Task.Run(async () =>
			{
				await Cache.RegisterRegionAsync(this._name).ConfigureAwait(false);
			}).ConfigureAwait(false);

#if DEBUG
			Task.Run(async () =>
			{
				await Task.Delay(345);
				Debug.WriteLine("The cache storage is initialized successful...");
				Debug.WriteLine("- Region name: " + this._name);
				Debug.WriteLine("- Expiration time: " + this._expirationTime + " minutes");
			}).ConfigureAwait(false);
#endif
		}

		#region Keys
#if DEBUG
		async Task _PushKeysAsync(string label, bool checkUpdatedKeys = true, Action callback = null)
#else
		async Task _PushKeysAsync(bool checkUpdatedKeys = true, Action callback = null)
#endif
		{
#if DEBUG
			var debug = Helper.GetLogPrefix(this._name);
#endif

			if (checkUpdatedKeys && this._addedKeys.Count < 1 && this._removedKeys.Count < 1)
			{
#if DEBUG
				Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: Stop the pushing process because no key need to update [" + this._RegionUpdatingFlag + "]");
#endif
				return;
			}

			// stop if task/thread complete update distributed cache
			if (await Cache.Memcached.ExistsAsync(this._RegionPushingFlag))
				return;

#if DEBUG
			var stopwatch = new Stopwatch();
			stopwatch.Start();
			var watch = new Stopwatch();
			watch.Start();
#endif

			// set flag
			await Cache.Memcached.StoreAsync(StoreMode.Set, this._RegionPushingFlag, Thread.CurrentThread.ManagedThreadId.ToString(), TimeSpan.FromMilliseconds(5000));

#if DEBUG
			watch.Stop();
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: Set pushing flag [" + this._RegionPushingFlag + ":" + Thread.CurrentThread.ManagedThreadId.ToString() + "] (distributed) is completed in " + watch.ElapsedMilliseconds.ToString() + " miliseconds");
			watch.Restart();
#endif

			// get keys
			var distributedKeys = Cache.GetRemoteKeys(this._RegionKey);

#if DEBUG
			watch.Stop();
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: Get keys [" + this._RegionKey + "] from distributed cache is completed in " + watch.ElapsedMilliseconds.ToString() + " miliseconds");
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: Total of distributed-keys: " + distributedKeys.Count.ToString());
#endif

			var totalAddedKeys = this._addedKeys.Count;
			var totalRemovedKeys = this._removedKeys.Count;

#if DEBUG
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: Start to merge distributed-keys with added/removed-keys [" + this._RegionKey + "]");
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: Total of added-keys [" + this._RegionKey + "]: " + totalAddedKeys.ToString());
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: Total of removed-keys [" + this._RegionKey + "]: " + this._removedKeys.Count.ToString());
			watch.Restart();
#endif

			// prepare keys for synchronizing
			var syncKeys = distributedKeys;

			// update removed keys
			if (totalRemovedKeys > 0)
			{
				var removedKeys = Helper.Clone(this._removedKeys);
				foreach (string key in removedKeys)
					if (syncKeys.Contains(key))
						syncKeys.Remove(key);
			}

			// update added keys
			if (totalAddedKeys > 0)
				syncKeys = Helper.Merge(syncKeys, this._addedKeys);

#if DEBUG
			watch.Stop();
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: Merge keys [" + this._RegionKey + "] is completed in " + watch.ElapsedMilliseconds.ToString() + " miliseconds");
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: Total of merged-keys [" + this._RegionKey + "]: " + syncKeys.Count.ToString());
			watch.Restart();
#endif

			// update mapping keys at distributed cache
			try
			{
				await Cache.SetRemoteKeysAsync(this._RegionKey, syncKeys);
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this._name, "Error occurred while pushing keys of the region", ex);
			}

#if DEBUG
			watch.Stop();
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: Push merged-keys [" + this._RegionKey + "] to distributed cache is completed in " + watch.ElapsedMilliseconds.ToString() + " miliseconds");
#endif

			// check to see new updated keys
			if (!totalAddedKeys.Equals(this._addedKeys.Count) || !totalRemovedKeys.Equals(this._removedKeys.Count))
			{
#if DEBUG
				Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: Wait other task complete before re-merging/re-pushing");
#endif

				// delay a moment before re-merging/re-pushing
				await Task.Delay(313);

				var rePush = false;

				// re-merge
				if (!totalRemovedKeys.Equals(this._removedKeys.Count) && this._removedKeys.Count > 0)
				{
#if DEBUG
					watch.Restart();
#endif

					rePush = true;
					totalRemovedKeys = this._removedKeys.Count;
					var removedKeys = Helper.Clone(this._removedKeys);
					foreach (var key in removedKeys)
						if (syncKeys.Contains(key))
							syncKeys.Remove(key);

#if DEBUG
					watch.Stop();
					Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: Re-Merge with removed-keys [" + this._RegionKey + "] is completed in " + watch.ElapsedMilliseconds.ToString() + " miliseconds");
					Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: Total of merged-keys [" + this._RegionKey + "]: " + syncKeys.Count.ToString());
#endif
				}

				if (!totalAddedKeys.Equals(this._addedKeys.Count))
				{
#if DEBUG
					watch.Restart();
#endif

					rePush = true;
					totalAddedKeys = this._addedKeys.Count;
					syncKeys = Helper.Merge(syncKeys, this._addedKeys);

#if DEBUG
					watch.Stop();
					Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: Re-Merge with added-keys [" + this._RegionKey + "] is completed in " + watch.ElapsedMilliseconds.ToString() + " miliseconds");
					Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: Total of merged-keys [" + this._RegionKey + "]: " + syncKeys.Count.ToString());
#endif
				}

				// re-push
				if (rePush)
				{
#if DEBUG
					watch.Restart();
#endif
					try
					{
						Cache.SetRemoteKeys(this._RegionKey, syncKeys);
					}
					catch (Exception ex)
					{
						Helper.WriteLogs(this._name, "Error occurred while (re)pushing keys of the region", ex);
					}

#if DEBUG
					watch.Stop();
					Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: Re-Push merged-keys [" + this._RegionKey + "] to distributed cache is completed in " + watch.ElapsedMilliseconds.ToString() + " miliseconds");
#endif
				}
			}

			// clear added/removed keys
			if (totalAddedKeys.Equals(this._addedKeys.Count))
				try
				{
					this._lock.EnterWriteLock();
					this._addedKeys.Clear();
				}
				catch (Exception ex)
				{
					Helper.WriteLogs(this._name, "Error occurred while removes added keys (after pushing process)", ex);
				}
				finally
				{
					if (this._lock.IsWriteLockHeld)
						this._lock.ExitWriteLock();
				}
#if DEBUG
			else
				Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: By-pass to clear added-keys [" + this._RegionKey + "], current total of added-keys: " + this._addedKeys.Count.ToString());
#endif

			if (totalRemovedKeys.Equals(this._removedKeys.Count))
				try
				{
					this._lock.EnterWriteLock();
					this._removedKeys.Clear();
				}
				catch (Exception ex)
				{
					Helper.WriteLogs(this._name, "Error occurred while removes removed keys (after pushing process)", ex);
				}
				finally
				{
					if (this._lock.IsWriteLockHeld)
						this._lock.ExitWriteLock();
				}
#if DEBUG
			else
				Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: By-pass to clear removed-keys [" + this._RegionKey + "], current total of removed-keys: " + this._removedKeys.Count.ToString());
#endif

#if DEBUG
			watch.Restart();
#endif

			// remove flags
			try
			{
				await Cache.Memcached.RemoveAsync(this._RegionPushingFlag);
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this._name, "Error occurred while updating flag when push keys of the region", ex);
			}

#if DEBUG
			watch.Stop();
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: Remove flags of updating/pushing [" + this._RegionUpdatingFlag + "/" + this._RegionPushingFlag + "] is completed in " + watch.ElapsedMilliseconds.ToString() + " miliseconds");
			stopwatch.Stop();
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: Push all cached keys [" + this._RegionKey + "] to distributed cache is completed in " + stopwatch.ElapsedMilliseconds.ToString() + " miliseconds. Total of pushed-keys: " + syncKeys.Count.ToString());
#endif

			// callback
#if DEBUG
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">:  -- And now run callback action");
#endif
			callback?.Invoke();
		}

#if DEBUG
		void _UpdateKeys(string label, int delay = 13, bool checkUpdatedKeys = true, Action callback = null)
#else
		void _UpdateKeys(int delay = 13, bool checkUpdatedKeys = true, Action callback = null)
#endif
		{
#if DEBUG
			var debug = Helper.GetLogPrefix(this._name);
#endif
			Task.Run(async () =>
			{
				await Task.Delay(delay);
#if DEBUG
				debug = "[" + this._name + " > " + Process.GetCurrentProcess().Id.ToString() + " : " + AppDomain.CurrentDomain.Id.ToString() + " : " + Thread.CurrentThread.ManagedThreadId.ToString() + "]";
				Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: Start to push keys [" + this._RegionKey + "] to distributed cache");
				if (this._addedKeys != null)
					Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: -- Total of added-keys: " + this._addedKeys.Count.ToString());
				if (this._removedKeys != null)
					Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: -- Total of removed-keys: " + this._removedKeys.Count.ToString());

				await this._PushKeysAsync(label, checkUpdatedKeys, callback).ConfigureAwait(false);
#else
				await this._PushKeysAsync(checkUpdatedKeys, callback).ConfigureAwait(false);
#endif
			}).ConfigureAwait(false);
		}

		void _UpdateKeys(string key, bool doPush)
		{
			// update added keys
			if (!this._updateKeys)
				return;

			if (!this._addedKeys.Contains(key))
				try
				{
					this._lock.EnterWriteLock();
					this._addedKeys.Add(key);
				}
				catch (Exception ex)
				{
					Helper.WriteLogs(this._name, "Error occurred while updating the added keys of the region (for pushing)", ex);
				}
				finally
				{
					if (this._lock.IsWriteLockHeld)
						this._lock.ExitWriteLock();
				}

			if (doPush && this._addedKeys.Count > 0)
#if DEBUG
				this._UpdateKeys("SET", 113);
#else
				this._UpdateKeys(113);
#endif
		}

		HashSet<string> _GetKeys()
		{
			return Cache.GetRemoteKeys(this._RegionKey);
		}

		Task<HashSet<string>> _GetKeysAsync()
		{
			return Cache.GetRemoteKeysAsync(this._RegionKey);
		}
		#endregion

		#region Set
		bool _Set(string key, object value, int expirationTime = 0, bool doPush = true, StoreMode mode = StoreMode.Set)
		{
			// check key & value
			if (string.IsNullOrWhiteSpace(key) || value == null)
				return false;

			// store
			var success = false;
			try
			{
				success = Cache.Memcached.Store(mode, this._GetKey(key), value, TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this._expirationTime));
			}
			catch (ArgumentException)
			{
				throw;
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this._name, "Error occurred while updating an object into cache [" + value.GetType().ToString() + "#" + key + "]", ex);
			}

			// update mapping key when added successful
			if (success)
				this._UpdateKeys(key, doPush);

			// return state
			return success;
		}

		async Task<bool> _SetAsync(string key, object value, int expirationTime = 0, bool doPush = true, StoreMode mode = StoreMode.Set)
		{
			// check key & value
			if (string.IsNullOrWhiteSpace(key) || value == null)
				return false;

			// store
			var success = false;
			try
			{
				success = await Cache.Memcached.StoreAsync(mode, this._GetKey(key), value, TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this._expirationTime));
			}
			catch (ArgumentException)
			{
				throw;
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this._name, "Error occurred while updating an object into cache [" + value.GetType().ToString() + "#" + key + "]", ex);
			}

			// update mapping key when added successful
			if (success)
				this._UpdateKeys(key, doPush);

			// return state
			return success;
		}
		#endregion

		#region Set (Multiple)
		void _Set<T>(IDictionary<string, T> items, string keyPrefix = null, int expirationTime = 0, StoreMode mode = StoreMode.Set)
		{
			// check collection
			if (items == null || items.Count < 1)
				return;

#if DEBUG
			var debug = Helper.GetLogPrefix(this._name);
			var stopwatch = new Stopwatch();
			stopwatch.Start();
#endif

			// set items
			foreach (var item in items)
				this._Set((string.IsNullOrWhiteSpace(keyPrefix) ? "" : keyPrefix) + item.Key, item.Value, expirationTime, false, mode);

#if DEBUG
			stopwatch.Stop();
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + "): " + "Set multiple items (" + items.Count.ToString() + ") is completed in " + stopwatch.ElapsedMilliseconds.ToString() + " miliseconds");
#endif

			// push keys
			if (this._updateKeys && this._addedKeys.Count > 0)
#if DEBUG
				this._UpdateKeys("SET-MULTIPLE", 113);
#else
				this._UpdateKeys(113);
#endif
		}

		void _Set(IDictionary<string, object> items, string keyPrefix = null, int expirationTime = 0, StoreMode mode = StoreMode.Set)
		{
			this._Set<object>(items, keyPrefix, expirationTime, mode);
		}

		async Task _SetAsync<T>(IDictionary<string, T> items, string keyPrefix = null, int expirationTime = 0, StoreMode mode = StoreMode.Set)
		{
			// check collection
			if (items == null || items.Count < 1)
				return;

#if DEBUG
			var debug = Helper.GetLogPrefix(this._name);
			var stopwatch = new Stopwatch();
			stopwatch.Start();
#endif

			// set items
			var tasks = items.Select(item => this._SetAsync((string.IsNullOrWhiteSpace(keyPrefix) ? "" : keyPrefix) + item.Key, item.Value, expirationTime, false, mode));
			await Task.WhenAll(tasks);

#if DEBUG
			stopwatch.Stop();
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + "): " + "Set multiple items (" + items.Count.ToString() + ") is completed in " + stopwatch.ElapsedMilliseconds.ToString() + " miliseconds");
#endif

			// push keys
			if (this._updateKeys && this._addedKeys.Count > 0)
#if DEBUG
				this._UpdateKeys("SET-MULTIPLE", 113);
#else
				this._UpdateKeys(113);
#endif
		}

		Task _SetAsync(IDictionary<string, object> items, string keyPrefix = null, int expirationTime = 0, StoreMode mode = StoreMode.Set)
		{
			return this._SetAsync<object>(items, keyPrefix, expirationTime, mode);
		}
		#endregion

		#region Set (Fragment)
		bool _SetAsFragments(string key, Type type, List<byte[]> fragments, int expirationTime = 0, StoreMode mode = StoreMode.Set)
		{
			// set info
			var fragment = new Fragment()
			{
				Key = key,
				Type = type.ToString() + "," + type.Assembly.FullName,
				TotalFragments = fragments.Count
			};

			var success = this._Set(fragment.Key, fragment, expirationTime, false, mode);

			// set data
			if (success)
			{
				var items = new Dictionary<string, object>();
				for (var index = 0; index < fragments.Count; index++)
					items.Add(this._GetFragmentKey(fragment.Key, index), new ArraySegment<byte>(fragments[index]));
				this._Set(items, null, expirationTime, mode);
			}

			return success;
		}

		bool _SetAsFragments(string key, object value, int expirationTime = 0, bool setSecondary = false, StoreMode mode = StoreMode.Set)
		{
			// check
			if (value == null)
				return false;

#if DEBUG
			var debug = Helper.GetLogPrefix("FRAGMENTS", " > ");
#endif

			// check to see the object is serializable
			var type = value.GetType();
			if (!type.IsSerializable)
			{
				var ex = new ArgumentException("The object (" + type.ToString() + ") must be serializable");
				Helper.WriteLogs(this._name, "Error occurred while trying to serialize an object [" + key + "]", ex);
				throw ex;
			}

			// serialize the object to an array of bytes
			var bytes = value is byte[]
				? value as byte[]
				: value is ArraySegment<byte>
					? ((ArraySegment<byte>)value).Array
					: null;

			if (bytes == null)
				bytes = Helper.SerializeAsBinary(value);

			// check
			if (bytes == null || bytes.Length < 1)
				return false;

			// split into fragments
			var fragments = Helper.Split(bytes, this._fragmentSize);

#if DEBUG
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") Serialize object and split into fragments successful [" + key + "]. Total of fragments: " + fragments.Count.ToString());
#endif

			// update into cache storage
			var success = this._SetAsFragments(key, type, fragments, expirationTime, mode);

			// post-process when setted
			if (success)
			{
				// update pure object (secondary) into cache
				if (setSecondary && !(value is byte[]))
					try
					{
						this._Set(key + ":(Secondary-Pure-Object)", value, expirationTime, false, mode);
					}
					catch (Exception ex)
					{
						Helper.WriteLogs(this._name, "Error occurred while updating an object into cache (pure object of fragments) [" + key + ":(Secondary-Pure-Object)" + "]", ex);
					}

				// update key
				this._UpdateKeys(key, true);
			}

#if DEBUG
			if (!success)
				Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") Set as fragments failed [" + key + " - " + (type.ToString() + "," + type.Assembly.FullName) + "]");
			else
				Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") Set as fragments successful [" + key + " - " + (type.ToString() + "," + type.Assembly.FullName) + "]. Total of fragments: " + fragments.Count.ToString());
#endif

			// return result
			return success;
		}

		async Task<bool> _SetAsFragmentsAsync(string key, Type type, List<byte[]> fragments, int expirationTime = 0, StoreMode mode = StoreMode.Set)
		{
			// set info
			var fragment = new Fragment()
			{
				Key = key,
				Type = type.ToString() + "," + type.Assembly.FullName,
				TotalFragments = fragments.Count
			};

			var success = await this._SetAsync(fragment.Key, fragment, expirationTime, false, mode);

			// set data
			if (success)
			{
				var items = new Dictionary<string, object>();
				for (var index = 0; index < fragments.Count; index++)
					items.Add(this._GetFragmentKey(fragment.Key, index), new ArraySegment<byte>(fragments[index]));
				await this._SetAsync(items, null, expirationTime, mode);
			}

			return success;
		}

		async Task<bool> _SetAsFragmentsAsync(string key, object value, int expirationTime = 0, bool setSecondary = false, StoreMode mode = StoreMode.Set)
		{
			// check
			if (value == null)
				return false;

#if DEBUG
			var debug = Helper.GetLogPrefix("FRAGMENTS", " > ");
#endif

			// check to see the object is serializable
			var type = value.GetType();
			if (!type.IsSerializable)
			{
				var ex = new ArgumentException("The object (" + type.ToString() + ") must be serializable");
				Helper.WriteLogs(this._name, "Error occurred while trying to serialize an object [" + key + "]", ex);
				throw ex;
			}

			// serialize the object to an array of bytes
			var bytes = value is byte[]
				? value as byte[]
				: value is ArraySegment<byte>
					? ((ArraySegment<byte>)value).Array
					: null;

			if (bytes == null)
				bytes = Helper.SerializeAsBinary(value);

			// check
			if (bytes == null || bytes.Length < 1)
				return false;

			// split into fragments
			var fragments = Helper.Split(bytes, this._fragmentSize);

#if DEBUG
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") Serialize object and split into fragments successful [" + key + "]. Total of fragments: " + fragments.Count.ToString());
#endif

			// update into cache storage
			var success = await this._SetAsFragmentsAsync(key, type, fragments, expirationTime, mode);

			// post-process when setted
			if (success)
			{
				// update pure object (secondary) into cache
				if (setSecondary && !(value is byte[]))
					try
					{
						await this._SetAsync(key + ":(Secondary-Pure-Object)", value, expirationTime, false, mode);
					}
					catch (Exception ex)
					{
						Helper.WriteLogs(this._name, "Error occurred while updating an object into cache (pure object of fragments) [" + key + ":(Secondary-Pure-Object)" + "]", ex);
					}

				// update key
				this._UpdateKeys(key, true);
			}

#if DEBUG
			if (!success)
				Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") Set as fragments failed [" + key + " - " + (type.ToString() + "," + type.Assembly.FullName) + "]");
			else
				Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") Set as fragments successful [" + key + " - " + (type.ToString() + "," + type.Assembly.FullName) + "]. Total of fragments: " + fragments.Count.ToString());
#endif

			// return result
			return success;
		}
		#endregion

		#region Add & Replace
		bool _Add(string key, object value, int expirationTime = 0)
		{
			return this._Set(key, value, expirationTime, true, StoreMode.Add);
		}

		Task<bool> _AddAsync(string key, object value, int expirationTime = 0)
		{
			return this._SetAsync(key, value, expirationTime, true, StoreMode.Add);
		}

		bool _Replace(string key, object value, int expirationTime = 0)
		{
			return this._Set(key, value, expirationTime, true, StoreMode.Replace);
		}

		Task<bool> _ReplaceAsync(string key, object value, int expirationTime = 0)
		{
			return this._SetAsync(key, value, expirationTime, true, StoreMode.Replace);
		}
		#endregion

		#region Get
		object _Get(string key, bool autoGetFragments = true)
		{
			if (string.IsNullOrWhiteSpace(key))
				throw new ArgumentNullException(key);

#if DEBUG
			var debug = Helper.GetLogPrefix(this._name);
			var stopwatch = new Stopwatch();
			stopwatch.Start();
#endif

			// get cached item
			object value = null;
			try
			{
				value = Cache.Memcached.Get(this._GetKey(key));
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this._name, "Error occurred while fetching an object from cache storage [" + key + "]", ex);
			}

			// get object as merged of all fragments
			if (autoGetFragments && value != null && value is Fragment)
				try
				{
					value = this._GetAsFragments((Fragment)value);
				}
				catch (Exception ex)
				{
					Helper.WriteLogs(this._name, "Error occurred while fetching an objects' fragments from cache storage [" + key + "]", ex);
					value = null;
				}

#if DEBUG
			stopwatch.Stop();
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + "): " + "Get single cache item [" + key + "] is completed in " + stopwatch.ElapsedMilliseconds.ToString() + " miliseconds");
#endif

			// return object
			return value;
		}

		async Task<object> _GetAsync(string key, bool autoGetFragments = true)
		{
			if (string.IsNullOrWhiteSpace(key))
				throw new ArgumentNullException(key);

#if DEBUG
			var debug = Helper.GetLogPrefix(this._name);
			var stopwatch = new Stopwatch();
			stopwatch.Start();
#endif

			// get cached item
			object value = null;
			try
			{
				value = await Cache.Memcached.GetAsync(this._GetKey(key));
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this._name, "Error occurred while fetching an object from cache storage [" + key + "]", ex);
			}

			// get object as merged of all fragments
			if (autoGetFragments && value != null && value is Fragment)
				try
				{
					value = this._GetAsFragments((Fragment)value);
				}
				catch (Exception ex)
				{
					Helper.WriteLogs(this._name, "Error occurred while fetching an objects' fragments from cache storage [" + key + "]", ex);
					value = null;
				}

#if DEBUG
			stopwatch.Stop();
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + "): " + "Get single cache item [" + key + "] is completed in " + stopwatch.ElapsedMilliseconds.ToString() + " miliseconds");
#endif

			// return object
			return value;
		}
		#endregion

		#region Get (Multiple)
		IDictionary<string, object> _Get(IEnumerable<string> keys)
		{
			// check keys
			if (object.ReferenceEquals(keys, null))
				return null;

#if DEBUG
			var debug = Helper.GetLogPrefix(this._name);
			var stopwatch = new Stopwatch();
			stopwatch.Start();
#endif

			// get collection of cached objects
			var realKeys = new List<string>();
			foreach (var key in keys)
				if (!string.IsNullOrWhiteSpace(key))
					realKeys.Add(this._GetKey(key));

			IDictionary<string, object> items = null;
			try
			{
				items = Cache.Memcached.Get(realKeys);
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this._name, "Error occurred while fetch a collection of objects from cache storage", ex);
			}

			IDictionary<string, object> objects = null;
			if (items != null && items.Count > 0)
				try
				{
					objects = items.ToDictionary(
							item => item.Key.Remove(0, this._name.Length + 1),
							item => item.Value != null && item.Value is Fragment ? this._GetAsFragments((Fragment)item.Value) : item.Value
						);
				}
				catch { }

			if (objects != null && objects.Count < 1)
				objects = null;

#if DEBUG
			stopwatch.Stop();
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + "): " + "Get multiple cache items process is completed in " + stopwatch.ElapsedMilliseconds.ToString() + " miliseconds");
#endif

			// return collection of cached objects
			return objects;
		}

		IDictionary<string, T> _Get<T>(IEnumerable<string> keys)
		{
			var objects = this._Get(keys);
			return objects != null
				? objects.ToDictionary(kvp => kvp.Key, kvp => kvp.Value is T ? (T)kvp.Value : default(T))
				: null;
		}

		async Task<IDictionary<string, object>> _GetAsync(IEnumerable<string> keys)
		{
			// check keys
			if (object.ReferenceEquals(keys, null))
				return null;

#if DEBUG
			var debug = Helper.GetLogPrefix(this._name);
			var stopwatch = new Stopwatch();
			stopwatch.Start();
#endif

			// get collection of cached objects
			var realKeys = new List<string>();
			foreach (var key in keys)
				if (!string.IsNullOrWhiteSpace(key))
					realKeys.Add(this._GetKey(key));

			IDictionary<string, object> items = null;
			try
			{
				items = await Cache.Memcached.GetAsync(realKeys);
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this._name, "Error occurred while fetch a collection of objects from cache storage", ex);
			}

			var objects = new Dictionary<string, object>();
			Func<KeyValuePair<string, object>, Task> func = async (kvp) =>
			{
				var key = kvp.Key.Remove(0, this._name.Length + 1);
				var value = kvp.Value != null && kvp.Value is Fragment
					? await this._GetAsFragmentsAsync((Fragment)kvp.Value)
					: kvp.Value;
				objects.Add(key, value);
			};

			if (items != null && items.Count > 0)
			{
				var tasks = new List<Task>();
				foreach (var kvp in items)
					tasks.Add(func(kvp));
				await Task.WhenAll(tasks);
			}

			if (objects != null && objects.Count < 1)
				objects = null;

#if DEBUG
			stopwatch.Stop();
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + "): " + "Get multiple cache items process is completed in " + stopwatch.ElapsedMilliseconds.ToString() + " miliseconds");
#endif

			// return collection of cached objects
			return objects;
		}

		async Task<IDictionary<string, T>> _GetAsync<T>(IEnumerable<string> keys)
		{
			var objects = await this._GetAsync(keys);
			return objects != null
				? objects.ToDictionary(kvp => kvp.Key, kvp => kvp.Value is T ? (T)kvp.Value : default(T))
				: null;
		}
		#endregion

		#region Get (Fragment)
		List<byte[]> _GetAsFragments(string key, List<int> indexes)
		{
			if (string.IsNullOrWhiteSpace(key))
				return null;

			var fragments = new List<byte[]>();
			indexes.ForEach(index =>
			{
				var bytes = index < 0
					? null
					: this._Get(this._GetFragmentKey(key, index), false) as byte[];
				if (bytes != null && bytes.Length > 0)
					fragments.Add(bytes);
			});
			return fragments;
		}

		object _GetAsFragments(Fragment fragment)
		{
			// check data
			if (object.ReferenceEquals(fragment, null) || string.IsNullOrWhiteSpace(fragment.Key) || string.IsNullOrWhiteSpace(fragment.Type) || fragment.TotalFragments < 1)
				return null;

			// check type
			var type = Type.GetType(fragment.Type);
			if (type == null)
				return null;

#if DEBUG
			string debug = "[FRAGMENTS > " + Process.GetCurrentProcess().Id.ToString() + " : " + AppDomain.CurrentDomain.Id.ToString() + " : " + Thread.CurrentThread.ManagedThreadId.ToString() + "]";
#endif

			// get all fragments
			var fragments = new byte[0];
			var length = 0;
			for (var index = 0; index < fragment.TotalFragments; index++)
			{
				byte[] bytes = null;
				var fragmentKey = this._GetKey(this._GetFragmentKey(fragment.Key, index));
				try
				{
					bytes = Cache.Memcached.Get<byte[]>(fragmentKey);
				}
				catch (Exception ex)
				{
					Helper.WriteLogs(this._name, "Error occurred while fetching fragments from cache [" + fragmentKey + "]", ex);
				}

				if (bytes != null && bytes.Length > 0)
				{
					Array.Resize<byte>(ref fragments, length + bytes.Length);
					Array.Copy(bytes, 0, fragments, length, bytes.Length);
					length += bytes.Length;
				}
			}

			// deserialize object
			object @object = Type.GetType(fragment.Type).Equals(typeof(byte[])) && fragments.Length > 0
				? fragments
				: null;

			if (@object == null && fragments.Length > 0)
				try
				{
					@object = Helper.DeserializeFromBinary(fragments);
				}
				catch (Exception ex)
				{
					Helper.WriteLogs(this._name, "Error occurred while trying to get fragmented object (error occured while serializing the object from an array of bytes) [" + fragment.Key + "]", ex);
					if (!type.Equals(typeof(byte[])))
					{
						@object = this._Get(fragment.Key + ":(Secondary-Pure-Object)", false);
						if (@object != null && @object is Fragment)
							@object = null;
					}
				}

#if DEBUG
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") Get as fragments successful [" + fragment.Key + " - " + fragment.Type + "]. Total of fragments: " + fragment.TotalFragments.ToString());
#endif

			// return object
			return @object;
		}

		object _GetAsFragments(string key)
		{
			object fragment = null;
			try
			{
				fragment = Cache.Memcached.Get(key);
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this._name, "Error occurred while fetching fragments from cache [" + key + "]", ex);
			}

			return fragment != null && fragment is Fragment && ((Fragment)fragment).TotalFragments > 0
				? this._GetAsFragments((Fragment)fragment)
				: null;
		}

		async Task<List<byte[]>> _GetAsFragmentsAsync(string key, List<int> indexes)
		{
			if (string.IsNullOrWhiteSpace(key))
				return null;

			var fragments = new List<byte[]>(indexes.Count);
			Func<int, Task> actionAsync = async (index) =>
			{
				var bytes = indexes[index] < 0
					? null
					: await this._GetAsync(this._GetFragmentKey(key, indexes[index]), false) as byte[];

				if (bytes != null && bytes.Length > 0)
					fragments[index] = bytes;
			};

			var tasks = new List<Task>();
			for (var index = 0; index < indexes.Count; index++)
				tasks.Add(actionAsync(index));
			await Task.WhenAll(tasks);

			return fragments;
		}

		async Task<object> _GetAsFragmentsAsync(Fragment fragment)
		{
			// check data
			if (object.ReferenceEquals(fragment, null) || string.IsNullOrWhiteSpace(fragment.Key) || string.IsNullOrWhiteSpace(fragment.Type) || fragment.TotalFragments < 1)
				return null;

			// check type
			var type = Type.GetType(fragment.Type);
			if (type == null)
				return null;

#if DEBUG
			string debug = "[FRAGMENTS > " + Process.GetCurrentProcess().Id.ToString() + " : " + AppDomain.CurrentDomain.Id.ToString() + " : " + Thread.CurrentThread.ManagedThreadId.ToString() + "]";
#endif

			// get all fragments
			var fragments = new List<byte[]>(fragment.TotalFragments);
			Func<int, Task> actionAsync = async (index) =>
			{
				var fragmentKey = this._GetKey(this._GetFragmentKey(fragment.Key, index));
				try
				{
					fragments[index] = await Cache.Memcached.GetAsync<byte[]>(fragmentKey);
				}
				catch (Exception ex)
				{
					Helper.WriteLogs(this._name, "Error occurred while fetching fragments from cache [" + fragmentKey + "]", ex);
				}
			};

			var tasks = new List<Task>();
			for (var index = 0; index < fragment.TotalFragments; index++)
				tasks.Add(actionAsync(index));
			await Task.WhenAll(tasks);

			// merge
			var data = new byte[0];
			var length = 0;
			foreach (var bytes in fragments)
				if (bytes != null && bytes.Length > 0)
				{
					Array.Resize<byte>(ref data, length + bytes.Length);
					Array.Copy(bytes, 0, data, length, bytes.Length);
					length += bytes.Length;
				}

			// deserialize object
			object @object = Type.GetType(fragment.Type).Equals(typeof(byte[])) && data.Length > 0
				? data
				: null;

			if (@object == null && data.Length > 0)
				try
				{
					@object = Helper.DeserializeFromBinary(data);
				}
				catch (Exception ex)
				{
					Helper.WriteLogs(this._name, "Error occurred while trying to get fragmented object (error occured while serializing the object from an array of bytes) [" + fragment.Key + "]", ex);
					if (!type.Equals(typeof(byte[])))
					{
						@object = await this._GetAsync(fragment.Key + ":(Secondary-Pure-Object)", false);
						if (@object != null && @object is Fragment)
							@object = null;
					}
				}

#if DEBUG
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") Get as fragments successful [" + fragment.Key + " - " + fragment.Type + "]. Total of fragments: " + fragment.TotalFragments.ToString());
#endif

			// return object
			return @object;
		}

		async Task<object> _GetAsFragmentsAsync(string key)
		{
			object fragment = null;
			try
			{
				fragment = await Cache.Memcached.GetAsync(key);
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this._name, "Error occurred while fetching fragments from cache [" + key + "]", ex);
			}

			return fragment != null && fragment is Fragment && ((Fragment)fragment).TotalFragments > 0
				? await this._GetAsFragmentsAsync((Fragment)fragment)
				: null;
		}
		#endregion

		#region Remove
		bool _Remove(string key, bool doPush = true)
		{
			// check
			if (string.IsNullOrWhiteSpace(key))
				return false;

			// remove
			var success = false;
			try
			{
				success = Cache.Memcached.Remove(this._GetKey(key));
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this._name, "Error occurred while removing an object from cache storage [" + key + "]", ex);
			}

			// update mapping key when removed successful
			if (success)
			{
				// update removed keys and push to distributed cache
				if (this._updateKeys)
				{
					if (!this._removedKeys.Contains(key))
						try
						{
							this._lock.EnterWriteLock();
							this._removedKeys.Add(key);
						}
						catch (Exception ex)
						{
							Helper.WriteLogs(this._name, "Error occurred while updating the removed keys (for pushing)", ex);
						}
						finally
						{
							if (this._lock.IsWriteLockHeld)
								this._lock.ExitWriteLock();
						}

					if (doPush && this._removedKeys.Count > 0)
#if DEBUG
						this._UpdateKeys("REMOVE", 213);
#else
						this._UpdateKeys(213);
#endif
				}
			}

			// return state
			return success;
		}

		async Task<bool> _RemoveAsync(string key, bool doPush = true)
		{
			// check
			if (string.IsNullOrWhiteSpace(key))
				return false;

			// remove
			var success = false;
			try
			{
				success = await Cache.Memcached.RemoveAsync(this._GetKey(key));
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this._name, "Error occurred while removing an object from cache storage [" + key + "]", ex);
			}

			// update mapping key when removed successful
			if (success)
			{
				// update removed keys and push to distributed cache
				if (this._updateKeys)
				{
					if (!this._removedKeys.Contains(key))
						try
						{
							this._lock.EnterWriteLock();
							this._removedKeys.Add(key);
						}
						catch (Exception ex)
						{
							Helper.WriteLogs(this._name, "Error occurred while updating the removed keys (for pushing)", ex);
						}
						finally
						{
							if (this._lock.IsWriteLockHeld)
								this._lock.ExitWriteLock();
						}

					if (doPush && this._removedKeys.Count > 0)
#if DEBUG
						this._UpdateKeys("REMOVE", 213);
#else
						this._UpdateKeys(213);
#endif
				}
			}

			// return state
			return success;
		}
		#endregion

		#region Remove (Multiple)
		void _Remove(IEnumerable<string> keys, string keyPrefix = null)
		{
			// check
			if (object.ReferenceEquals(keys, null))
				return;

			// remove
			foreach (string key in keys)
				if (!string.IsNullOrWhiteSpace(key))
					this._Remove((string.IsNullOrWhiteSpace(keyPrefix) ? "" : keyPrefix) + key, false);

			// push keys
			if (this._updateKeys && this._removedKeys.Count > 0)
#if DEBUG
				this._UpdateKeys("REMOVE-MULTIPLE", 213);
#else
				this._UpdateKeys(213);
#endif
		}

		async Task _RemoveAsync(IEnumerable<string> keys, string keyPrefix = null)
		{
			// check
			if (object.ReferenceEquals(keys, null))
				return;

			// remove
			var tasks = new List<Task>();
			foreach (string key in keys)
				if (!string.IsNullOrWhiteSpace(key))
					tasks.Add(this._RemoveAsync((string.IsNullOrWhiteSpace(keyPrefix) ? "" : keyPrefix) + key, false));
			await Task.WhenAll(tasks);

			// push keys
			if (this._updateKeys && this._removedKeys.Count > 0)
#if DEBUG
				this._UpdateKeys("REMOVE-MULTIPLE", 213);
#else
				this._UpdateKeys(213);
#endif
		}
		#endregion

		#region Remove (Fragment)
		void _RemoveFragments(string key, int maxIndex = 100)
		{
			var keys = new List<string>() { key, key + ":(Secondary-Pure-Object)" };
			for (var index = 0; index < maxIndex; index++)
				keys.Add(this._GetFragmentKey(key, index));
			this._Remove(keys);
		}

		void _RemoveFragments(Fragment fragment)
		{
			if (!object.ReferenceEquals(fragment, null) && !string.IsNullOrWhiteSpace(fragment.Key) && fragment.TotalFragments > 0)
				this._RemoveFragments(fragment.Key, fragment.TotalFragments);
		}

		async Task _RemoveFragmentsAsync(string key, int maxIndex = 100)
		{
			var keys = new List<string>() { key, key + ":(Secondary-Pure-Object)" };
			for (var index = 0; index < maxIndex; index++)
				keys.Add(this._GetFragmentKey(key, index));
			await this._RemoveAsync(keys);
		}

		async Task _RemoveFragmentsAsync(Fragment fragment)
		{
			if (!object.ReferenceEquals(fragment, null) && !string.IsNullOrWhiteSpace(fragment.Key) && fragment.TotalFragments > 0)
				await this._RemoveFragmentsAsync(fragment.Key, fragment.TotalFragments);
		}
		#endregion

		#region Exists
		bool _Exists(string key)
		{
			return Cache.Memcached.Exists(this._GetKey(key));
		}

		Task<bool> _ExistsAsync(string key)
		{
			return Cache.Memcached.ExistsAsync(this._GetKey(key));
		}
		#endregion

		#region Clear
		void _Clear()
		{
#if DEBUG
			var stopwatch = new Stopwatch();
			stopwatch.Start();
			var debug = Helper.GetLogPrefix(this._name);
#endif

			// get keys
			var keys = this._GetKeys();

#if DEBUG
			stopwatch.Stop();
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <CLEAR>: Get keys to clear [" + this._RegionKey + "] is completed in " + stopwatch.ElapsedMilliseconds.ToString() + " miliseconds");
			stopwatch.Restart();
#endif

			// remove cached items
			this._Remove(keys);

			// remove mapping keys
			Cache.Memcached.Remove(this._RegionKey);

			try
			{
				this._lock.EnterWriteLock();
				this._addedKeys.Clear();
				this._removedKeys.Clear();
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this._name, "Error occurred while updating the added/removed keys (after clear)", ex);
			}
			finally
			{
				if (this._lock.IsWriteLockHeld)
					this._lock.ExitWriteLock();
			}

#if DEBUG
			stopwatch.Stop();
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <CLEAR>: Clear all cached items [" + this._RegionKey + "] is completed in " + stopwatch.ElapsedMilliseconds.ToString() + " miliseconds");
#endif
		}

		async Task _ClearAsync()
		{
#if DEBUG
			var stopwatch = new Stopwatch();
			stopwatch.Start();
			var debug = Helper.GetLogPrefix(this._name);
#endif

			// get keys
			var keys = await this._GetKeysAsync();

#if DEBUG
			stopwatch.Stop();
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <CLEAR>: Get keys to clear [" + this._RegionKey + "] is completed in " + stopwatch.ElapsedMilliseconds.ToString() + " miliseconds");
			stopwatch.Restart();
#endif

			// remove
			await Task.WhenAll(
				this._RemoveAsync(keys),
				Cache.Memcached.RemoveAsync(this._RegionKey)
			);

			try
			{
				this._lock.EnterWriteLock();
				this._addedKeys.Clear();
				this._removedKeys.Clear();
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this._name, "Error occurred while updating the added/removed keys (after clear)", ex);
			}
			finally
			{
				if (this._lock.IsWriteLockHeld)
					this._lock.ExitWriteLock();
			}

#if DEBUG
			stopwatch.Stop();
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <CLEAR>: Clear all cached items [" + this._RegionKey + "] is completed in " + stopwatch.ElapsedMilliseconds.ToString() + " miliseconds");
#endif
		}
		#endregion

		// -----------------------------------------------------

		#region [Public] Keys
		/// <summary>
		/// Gets the collection of keys that associates with the cached items
		/// </summary>
		public HashSet<string> Keys
		{
			get
			{
				return this.GetKeys();
			}
		}

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
			return this._Set(key, value, expirationTime, true);
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
		/// <param name="type">The object that presents type of object that serialized as all fragments</param>
		/// <param name="fragments">The collection that contains all fragments (object that serialized as binary - array bytes)</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool SetFragments(string key, Type type, List<byte[]> fragments, int expirationTime = 0)
		{
			return this._SetAsFragments(key, type, fragments, expirationTime);
		}

		/// <summary>
		/// Serializes object into array of bytes, splits into one or more fragments and updates into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <param name="setSecondary">true to add secondary item as pure object</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool SetAsFragments(string key, object value, int expirationTime = 0, bool setSecondary = false)
		{
			return this._SetAsFragments(key, value, expirationTime, setSecondary);
		}

		/// <summary>
		/// Adds an item (as fragments) into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="type">The object that presents type of object that serialized as all fragments</param>
		/// <param name="fragments">The collection that contains all fragments (object that serialized as binary - array bytes)</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetFragmentsAsync(string key, Type type, List<byte[]> fragments, int expirationTime = 0)
		{
			return this._SetAsFragmentsAsync(key, type, fragments, expirationTime);
		}

		/// <summary>
		/// Serializes object into array of bytes, splits into one or more fragments and updates into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <param name="setSecondary">true to add secondary item as pure object</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetFragmentsAsync(string key, object value, int expirationTime = 0, bool setSecondary = false)
		{
			return this._SetAsFragmentsAsync(key, value, expirationTime, setSecondary);
		}
		#endregion

		#region [Public] Add & Replace
		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Add(string key, object value, int expirationTime = 0)
		{
			return this._Add(key, value, expirationTime);
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
			return this._AddAsync(key, value, expirationTime);
		}

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Replace(string key, object value, int expirationTime = 0)
		{
			return this._Replace(key, value, expirationTime);
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
			return this._ReplaceAsync(key, value, expirationTime);
		}
		#endregion

		#region [Public] Get
		/// <summary>
		/// Retreives a cached item
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to retreive</param>
		/// <returns>The retrieved cache item, or a null reference if the key is not found</returns>
		/// <exception cref="System.ArgumentNullException">If the <paramref name="key">key</paramref> parameter is null</exception>
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
		/// <exception cref="System.ArgumentNullException">If the <paramref name="key">key</paramref> parameter is null</exception>
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
		/// <exception cref="System.ArgumentNullException">If the <paramref name="key">key</paramref> parameter is null</exception>
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
		/// <exception cref="System.ArgumentNullException">If the <paramref name="key">key</paramref> parameter is null</exception>
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
		/// Gets fragment information that associates with the key (only available when working with distributed cache)
		/// </summary>
		/// <param name="key">The string that presents key of fragment information</param>
		/// <returns>The <see cref="Fragment">Fragment</see> object that presents information of all fragmented items in the cache storage</returns>
		public Fragment GetFragment(string key)
		{
			object fragment = null;
			if (!string.IsNullOrWhiteSpace(key))
				fragment = this._Get(key, false);

			return fragment == null || !(fragment is Fragment)
				? new Fragment() { Key = key, TotalFragments = 0 }
				: (Fragment)fragment;
		}

		/// <summary>
		/// Gets cached of fragmented items that associates with the key and indexes (only available when working with distributed cache)
		/// </summary>
		/// <param name="key">The string that presents key of all fragmented items</param>
		/// <param name="indexes">The collection that presents indexes of all fragmented items need to get</param>
		/// <returns>The collection of array of bytes that presents serialized information of fragmented items</returns>
		public List<byte[]> GetAsFragments(string key, List<int> indexes)
		{
			return string.IsNullOrWhiteSpace(key)
				? null
				: this._GetAsFragments(key, indexes);
		}

		/// <summary>
		/// Gets cached of fragmented items that associates with the key and indexes (only available when working with distributed cache)
		/// </summary>
		/// <param name="key">The string that presents key of all fragmented items</param>
		/// <param name="indexes">The collection that presents indexes of all fragmented items need to get</param>
		/// <returns>The collection of array of bytes that presents serialized information of fragmented items</returns>
		public List<byte[]> GetAsFragments(string key, params int[] indexes)
		{
			if (string.IsNullOrWhiteSpace(key))
				return null;

			List<int> indexesList = new List<int>();
			if (indexes != null && indexes.Length > 0)
				foreach (int index in indexes)
					indexesList.Add(index);

			return this.GetAsFragments(key, indexesList);
		}

		/// <summary>
		/// Gets fragment information that associates with the key (only available when working with distributed cache)
		/// </summary>
		/// <param name="key">The string that presents key of fragment information</param>
		/// <returns>The <see cref="Fragment">Fragment</see> object that presents information of all fragmented items in the cache storage</returns>
		public async Task<Fragment> GetFragmentAsync(string key)
		{
			object fragment = null;
			if (!string.IsNullOrWhiteSpace(key))
				fragment = await this._GetAsync(key, false);

			return fragment == null || !(fragment is Fragment)
				? new Fragment() { Key = key, TotalFragments = 0 }
				: (Fragment)fragment;
		}

		/// <summary>
		/// Gets cached of fragmented items that associates with the key and indexes (only available when working with distributed cache)
		/// </summary>
		/// <param name="key">The string that presents key of all fragmented items</param>
		/// <param name="indexes">The collection that presents indexes of all fragmented items need to get</param>
		/// <returns>The collection of array of bytes that presents serialized information of fragmented items</returns>
		public async Task<List<byte[]>> GetAsFragmentsAsync(string key, List<int> indexes)
		{
			return string.IsNullOrWhiteSpace(key)
				? null
				: await this._GetAsFragmentsAsync(key, indexes);
		}

		/// <summary>
		/// Gets cached of fragmented items that associates with the key and indexes (only available when working with distributed cache)
		/// </summary>
		/// <param name="key">The string that presents key of all fragmented items</param>
		/// <param name="indexes">The collection that presents indexes of all fragmented items need to get</param>
		/// <returns>The collection of array of bytes that presents serialized information of fragmented items</returns>
		public Task<List<byte[]>> GetAsFragmentsAsync(string key, params int[] indexes)
		{
			if (string.IsNullOrWhiteSpace(key))
				return null;

			List<int> indexesList = new List<int>();
			if (indexes != null && indexes.Length > 0)
				foreach (int index in indexes)
					indexesList.Add(index);

			return this.GetAsFragmentsAsync(key, indexesList);
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
		/// Removes all fragmented items from cache storage
		/// </summary>
		/// <param name="fragment">The <see cref="Fragment">Fragment</see> object that presents information of all fragmented items in the cache storage need to be removed</param>
		public void RemoveFragments(Fragment fragment)
		{
			this._RemoveFragments(fragment);
		}

		/// <summary>
		/// Removes a cached item (with first 100 fragments) from cache storage
		/// </summary>
		/// <param name="key">The string that presents key of fragmented items need to be removed</param>
		public Task RemoveFragmentsAsync(string key)
		{
			return this._RemoveFragmentsAsync(key);
		}

		/// <summary>
		/// Removes all fragmented items from cache storage
		/// </summary>
		/// <param name="fragment">The <see cref="Fragment">Fragment</see> object that presents information of all fragmented items in the cache storage need to be removed</param>
		public Task RemoveFragmentsAsync(Fragment fragment)
		{
			return this._RemoveFragmentsAsync(fragment);
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
			return this._Exists(key);
		}

		/// <summary>
		/// Determines whether an item exists in the cache
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to check</param>
		/// <returns>Returns a boolean value indicating if the object that associates with the key is cached or not</returns>
		public Task<bool> ExistsAsync(string key)
		{
			return this._ExistsAsync(key);
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

		// -----------------------------------------------------

		#region [Helper]
		string _RegionKey
		{
			get
			{
				return this._GetKey("Isolated-Region-Keys");
			}
		}

		string _RegionUpdatingFlag
		{
			get
			{
				return this._RegionKey + "<Updating-Flag>";
			}
		}

		string _RegionPushingFlag
		{
			get
			{
				return this._RegionKey + "<Pushing-Flag>";
			}
		}

		string _GetKey(string key)
		{
			return this._name + "@" + key.Replace(" ", "-");
		}

		string _GetFragmentKey(string key, int index)
		{
			return key.Replace(" ", "-") + "$[Fragment<" + index.ToString() + ">]";
		}
		#endregion

		#region [Static]
		internal static bool SetRemoteKeys(string key, HashSet<string> keys)
		{
			var fragments = Helper.Split(Helper.SerializeAsBinary(keys), Cache.DefaultFragmentSize);
			var fragmentsInfo = new Fragment()
			{
				Key = key,
				Type = keys.GetType().ToString() + "," + keys.GetType().Assembly.FullName,
				TotalFragments = fragments.Count
			};

			if (Cache.Memcached.Store(StoreMode.Set, Cache.RegionsKey, fragmentsInfo))
			{
				for (int index = 0; index < fragments.Count; index++)
					Cache.Memcached.Store(StoreMode.Set, key + ":" + index, fragments[index]);
				return true;
			}

			return false;
		}

		internal static Task<bool> SetRemoteKeysAsync(string key, HashSet<string> keys)
		{
			return Helper.ExecuteTask<bool>(() => Cache.SetRemoteKeys(key, keys));
		}

		internal static HashSet<string> GetRemoteKeys(string key)
		{
			var info = Cache.Memcached.Get<Fragment>(key);
			if (object.ReferenceEquals(info, null))
				return new HashSet<string>();

			// get all fragments
			var fragments = new byte[0];
			var length = 0;
			for (var index = 0; index < info.TotalFragments; index++)
			{
				byte[] bytes = null;
				try
				{
					bytes = Cache.Memcached.Get<byte[]>(key + ":" + index);
				}
				catch { }

				if (bytes != null && bytes.Length > 0)
				{
					Array.Resize<byte>(ref fragments, length + bytes.Length);
					Array.Copy(bytes, 0, fragments, length, bytes.Length);
					length += bytes.Length;
				}
			}

			try
			{
				return fragments.Length > 0
					? Helper.DeserializeFromBinary(fragments) as HashSet<string>
					: new HashSet<string>();
			}
			catch
			{
				return new HashSet<string>();
			}
		}

		internal static Task<HashSet<string>> GetRemoteKeysAsync(string key)
		{
			return Helper.ExecuteTask<HashSet<string>>(() => Cache.GetRemoteKeys(key));
		}

		static readonly string RegionsKey = "VIEApps-Caching-Regions";

		/// <summary>
		/// Gets the collection of all registered regions (in distributed cache)
		/// </summary>
		public static HashSet<string> GetAllRegions()
		{
			return Cache.GetRemoteKeys(Cache.RegionsKey);
		}

		/// <summary>
		/// Gets the collection of all registered regions (in distributed cache)
		/// </summary>
		public static Task<HashSet<string>> GetAllRegionsAsync()
		{
			return Cache.GetRemoteKeysAsync(Cache.RegionsKey);
		}

		static async Task RegisterRegionAsync(string name)
		{
			// wait for other
			var attempt = 0;
			while (attempt < 123 && await Cache.Memcached.ExistsAsync(Cache.RegionsKey + "-Registering"))
			{
				await Task.Delay(123);
				attempt++;
			}

			// set flag
			await Cache.Memcached.StoreAsync(StoreMode.Set, Cache.RegionsKey + "-Registering", "v", TimeSpan.FromSeconds(30));

			// fetch region-keys
			var regions = await Cache.GetRemoteKeysAsync(Cache.RegionsKey);

			// register new region
			if (!regions.Contains(name))
			{
				regions.Add(name);
				try
				{
#if DEBUG
					string debug = "[" + name + " > " + Process.GetCurrentProcess().Id.ToString() + " : " + AppDomain.CurrentDomain.Id.ToString() + " : " + Thread.CurrentThread.ManagedThreadId.ToString() + "]";
#endif

					if (!await Cache.SetRemoteKeysAsync(Cache.RegionsKey, regions))
					{
#if DEBUG
						Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <INIT>: Cannot update regions into cache storage");
#endif

						Helper.WriteLogs(name, "Cannot update regions into cache storage", null);
					}
#if DEBUG
					else
						Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <INIT>: Update regions into cache storage successful");
#endif
				}
				catch (Exception ex)
				{
					Helper.WriteLogs(name, "Error occurred while updating regions", ex);
				}
			}

			// remove flag
			await Cache.Memcached.RemoveAsync(Cache.RegionsKey + "-Registering");
		}
		#endregion

	}
}