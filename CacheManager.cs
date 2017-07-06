#region Related components
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.IO.Compression;
using System.Runtime.Caching;
using System.Runtime.Serialization.Formatters.Binary;
using System.Diagnostics;

using Enyim.Caching.Memcached;
#endregion

namespace net.vieapps.Components.Caching
{
	/// <summary>
	/// Manipulates objects in isolated regions with distributed cache (memcached)
	/// </summary>
	[DebuggerDisplay("Name = {_name}, Type = {_expirationType}, Time = {_expirationTime}")]
	public sealed class CacheManager
	{

		#region Supporting
		/// <summary>
		/// Caching mode
		/// </summary>
		public enum Mode
		{
			/// <summary>
			/// In-Process caching mechanism (.NET Memory Cache)
			/// </summary>
			Internal = 0,

			/// <summary>
			/// Distributed caching mechanism (memcached)
			/// </summary>
			Distributed = 1,
		}

		[Serializable]
		public struct Fragment
		{
			public string Key;
			public string Type;
			public int TotalFragments;
		}
		#endregion

		#region Default settings
		/// <summary>
		/// Gets default mode of caching mechanism
		/// </summary>
		public static readonly Mode DefaultMode = Mode.Distributed;

		/// <summary>
		/// Gets default expiration type
		/// </summary>
		public static readonly string DefaultExpirationType = "Sliding";

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
		Mode _mode = Mode.Distributed;
		string _expirationType = CacheManager.DefaultExpirationType;
		int _expirationTime = CacheManager.DefaultExpirationTime;
		int _fragmentSize = CacheManager.DefaultFragmentSize;
		bool _activeSynchronize = false, _updateKeys = false, _monitorKeys = false, _throwObjectTooLargeForCacheException = false;
		HashSet<string> _activeKeys = null, _addedKeys = null, _removedKeys = null;
		MemoryCache _bag = MemoryCache.Default;
		ReaderWriterLockSlim _lock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
		#endregion

		#region Constructors
		/// <summary>
		/// Create instance of cache manager with default isolated region
		/// </summary>
		public CacheManager() : this(null) { }

		/// <summary>
		/// Create instance of cache manager with isolated region
		/// </summary>
		/// <param name="name">The string that presents name of isolated region that the cache manager will work with</param>
		/// <param name="activeSynchronize">true to active synchronize keys when working mode is Distributed (active synchronize keys between in-process cache and distributed cache) </param>
		/// <param name="updateKeys">true to active update keys when working mode is Distributed</param>
		/// <param name="monitorKeys">true to active monitor keys when working mode is Distributed with RemovedCallback</param>
		public CacheManager(string name, bool activeSynchronize = false, bool updateKeys = false, bool monitorKeys = false) : this(name, CacheManager.DefaultMode, activeSynchronize, updateKeys, monitorKeys) { }

		/// <summary>
		/// Create instance of cache manager with isolated region
		/// </summary>
		/// <param name="name">The string that presents name of isolated region that the cache manager will work with</param>
		/// <param name="mode">Mode of caching mechanism</param>
		/// <param name="activeSynchronize">true to active synchronize keys when working mode is Distributed (active synchronize keys between in-process cache and distributed cache) </param>
		/// <param name="updateKeys">true to active update keys when working mode is Distributed</param>
		/// <param name="monitorKeys">true to active monitor keys when working mode is Distributed with RemovedCallback</param>
		public CacheManager(string name, Mode mode, bool activeSynchronize = false, bool updateKeys = false, bool monitorKeys = false) : this(name, CacheManager.DefaultExpirationType, CacheManager.DefaultExpirationTime, mode, activeSynchronize, updateKeys, monitorKeys) { }

		/// <summary>
		/// Create instance of cache manager with isolated region
		/// </summary>
		/// <param name="name">The string that presents name of isolated region that the cache manager will work with</param>
		/// <param name="expirationTime">Time in minutes to cache any item</param>
		public CacheManager(string name, int expirationTime) : this(name, CacheManager.DefaultExpirationType, expirationTime) { }

		/// <summary>
		/// Create instance of cache manager with isolated region
		/// </summary>
		/// <param name="name">The string that presents name of isolated region that the cache manager will work with</param>
		/// <param name="expirationType">Type of expiration (Sliding or Absolute)</param>
		/// <param name="expirationTime">Time in minutes to cache any item</param>
		/// <param name="activeSynchronize">true to active synchronize keys when working mode is Distributed (active synchronize keys between in-process cache and distributed cache) </param>
		public CacheManager(string name, string expirationType, int expirationTime, bool activeSynchronize = false) : this(name, expirationType, expirationTime, CacheManager.DefaultMode, activeSynchronize) { }

		/// <summary>
		/// Create instance of cache manager with isolated region
		/// </summary>
		/// <param name="name">The string that presents name of isolated region that the cache manager will work with</param>
		/// <param name="expirationType">Type of expiration (Sliding or Absolute)</param>
		/// <param name="expirationTime">Time in minutes to cache any item</param>
		/// <param name="mode">Mode of caching mechanism</param>
		/// <param name="activeSynchronize">true to active synchronize keys when working mode is Distributed (active synchronize keys between in-process cache and distributed cache)</param>
		/// <param name="updateKeys">true to active update keys when working mode is Distributed</param>
		/// <param name="monitorKeys">true to active monitor keys when working mode is Distributed with RemovedCallback</param>
		/// <param name="throwObjectTooLargeForCacheException">true to throw 'Object too large for cache' exception when working mode is Distributed</param>
		public CacheManager(string name, string expirationType, int expirationTime, Mode mode, bool activeSynchronize = false, bool updateKeys = false, bool monitorKeys = false, bool throwObjectTooLargeForCacheException = false)
		{
			// zone name
			this._name = string.IsNullOrWhiteSpace(name)
				? "VIEApps-Cache-Storage"
				: System.Text.RegularExpressions.Regex.Replace(name, "[^0-9a-zA-Z:-]+", "");

			// expiration type
			if (!string.IsNullOrWhiteSpace(expirationType) && expirationType.ToLower().Equals("absolute"))
				this._expirationType = "Absolute";

			// expiration time
			this._expirationTime = expirationTime > 0
				? expirationTime
				: CacheManager.DefaultExpirationTime;

			// change mode of caching to Internal (In-Process)
			if (mode.Equals(Mode.Internal))
				this._mode = Mode.Internal;

			// special settings for working with distributed cache
			else
			{
				this._activeSynchronize = activeSynchronize;
				this._updateKeys = updateKeys;
				this._monitorKeys = monitorKeys;
				this._throwObjectTooLargeForCacheException = throwObjectTooLargeForCacheException;
			}

			// prepare keys
			this._PrepareKeys();

			// register region
			if (this._mode.Equals(Mode.Distributed))
				Task.Run(async () =>
				{
					await CacheManager.RegisterRegionAsync(this._name).ConfigureAwait(false);
				}).ConfigureAwait(false);

#if DEBUG
			Task.Run(async () =>
			{
				await Task.Delay(345);
				Debug.WriteLine("The cache storage is initialized successful...");
				Debug.WriteLine("- Mode: " + this._mode);
				Debug.WriteLine("- Region name: " + this._name);
				Debug.WriteLine("- Expiration type: " + this._expirationType);
				Debug.WriteLine("- Expiration time: " + this._expirationTime + " minutes");
			}).ConfigureAwait(false);
#endif
		}
		#endregion

		#region Helper properties
		string _RegionKey
		{
			get
			{
				return this._GetKey("Isolated-Region-Keys");
			}
		}

		string _RegionKeysAdded
		{
			get
			{
				return this._RegionKey + "<Added>";
			}
		}

		string _RegionKeysRemoved
		{
			get
			{
				return this._RegionKey + "<Removed>";
			}
		}

		string _RegionUpdatingFlag
		{
			get
			{
				return this._RegionKey + "<Updating-Flag>";
			}
		}

		string _RegionPullingFlag
		{
			get
			{
				return this._RegionKey + "<Pulling-Flag>";
			}
		}

		string _RegionPushingFlag
		{
			get
			{
				return this._RegionKey + "<Pushing-Flag>";
			}
		}
		#endregion

		#region Helper methods
		string _GetKey(string key)
		{
			return this._name + "@" + key.Replace(" ", "-");
		}

		string _GetFragmentKey(string key, int index)
		{
			return key.Replace(" ", "-") + "$[Fragment<" + index.ToString() + ">]";
		}
		#endregion

		#region Prepare (Pull) Keys methods
		void _PrepareKeys()
		{
			// active keys
			if (this._mode.Equals(Mode.Internal) || this._activeSynchronize)
			{
				if (!this._bag.Contains(this._RegionKey))
					this._bag.Set(this._RegionKey, new HashSet<string>(), new CacheItemPolicy() { SlidingExpiration = TimeSpan.FromDays(7), Priority = CacheItemPriority.NotRemovable });
				this._activeKeys = this._bag.Get(this._RegionKey) as HashSet<string>;
			}

			// added keys
			if (!this._bag.Contains(this._RegionKeysAdded))
				this._bag.Set(this._RegionKeysAdded, new HashSet<string>(), new CacheItemPolicy() { SlidingExpiration = TimeSpan.FromDays(7), Priority = CacheItemPriority.NotRemovable });
			this._addedKeys = this._bag.Get(this._RegionKeysAdded) as HashSet<string>;

			// removed keys
			if (!this._bag.Contains(this._RegionKeysRemoved))
				this._bag.Set(this._RegionKeysRemoved, new HashSet<string>(), new CacheItemPolicy() { SlidingExpiration = TimeSpan.FromDays(7), Priority = CacheItemPriority.NotRemovable });
			this._removedKeys = this._bag.Get(this._RegionKeysRemoved) as HashSet<string>;

			// active synchronize keys between in-process cache & distributed cache
			if (this._mode.Equals(Mode.Distributed) && this._activeSynchronize)
				Task.Run(async () =>
				{
					await Task.Delay(13);
#if DEBUG
					string debug = "[" + this._name + " > " + Process.GetCurrentProcess().Id.ToString() + " : " + AppDomain.CurrentDomain.Id.ToString() + " : " + Thread.CurrentThread.ManagedThreadId.ToString() + "]";
					Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <PULL>: Start to pull keys [" + this._RegionKey + "] from distributed cache");
#endif
					await this._PullKeysAsync().ConfigureAwait(false);
				}).ConfigureAwait(false);
		}

		async Task _PullKeysAsync(Action callback = null)
		{
#if DEBUG
			string debug = "[" + this._name + " > " + Process.GetCurrentProcess().Id.ToString() + " : " + AppDomain.CurrentDomain.Id.ToString() + " : " + Thread.CurrentThread.ManagedThreadId.ToString() + "]";
#endif
			// stop if other task is process pulling/pushing
			if (this._bag.Contains(this._RegionPullingFlag))
			{
#if DEBUG
				Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <PULL>: Stop pulling process because other task is processing (pulling) now [" + this._RegionPullingFlag + ":" + (this._bag.Get(this._RegionPullingFlag) as string) + "]");
#endif
				return;
			}
			else if (this._bag.Contains(this._RegionPushingFlag))
			{
#if DEBUG
				Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <PULL>: Stop pulling process because other task is processing (pushing) now [" + this._RegionPushingFlag + ":" + (this._bag.Get(this._RegionPushingFlag) as string) + "]");
#endif
				return;
			}

			// wait for other task/thread complete update distributed cache
			var attempt = 0;
			string distributedFlag = null;
			try
			{
				distributedFlag = DistributedCache.Get<string>(this._RegionPushingFlag);
			}
			catch (Exception ex)
			{
				CacheManager.WriteLogs(this._name, "Error occurred while updating flag when pull keys of the region", ex);
			}

			while (distributedFlag != null && attempt < 3)
			{
				attempt++;
#if DEBUG
				Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <PULL>: Wait for other task complete the pushing process [" + this._RegionPushingFlag + ":" + distributedFlag + ": (" + attempt.ToString() + ")");
#endif
				await Task.Delay(113);
				try
				{
					distributedFlag = DistributedCache.Get<string>(this._RegionPushingFlag);
				}
				catch (Exception ex)
				{
					CacheManager.WriteLogs(this._name, "Error occurred while updating flag when pull keys of the region", ex);
					distributedFlag = null;
				}
			}

#if DEBUG
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <PULL>: Start to pull keys [" + this._RegionKey + "] from distributed cache");
			Stopwatch stopwatch = new Stopwatch();
			stopwatch.Start();
#endif

			// set flag
			this._bag.Set(this._RegionPullingFlag, Thread.CurrentThread.ManagedThreadId.ToString(), new CacheItemPolicy() { SlidingExpiration = TimeSpan.FromSeconds(30), Priority = CacheItemPriority.NotRemovable });

			// pull keys from distributed cache
			var keys = CacheManager.FetchDistributedKeys(this._RegionKey);

			// update the active keys
			try
			{
				this._lock.EnterWriteLock();
				this._activeKeys = CacheManager.Merge(false, this._activeKeys, keys);
			}
			catch (Exception ex)
			{
				CacheManager.WriteLogs(this._name, "Error occurred while updating the active keys of the region (while pulling)", ex);
			}
			finally
			{
				if (this._lock.IsWriteLockHeld)
					this._lock.ExitWriteLock();
			}

			// remove flag
			this._bag.Remove(this._RegionPullingFlag);

#if DEBUG
			stopwatch.Stop();
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <PULL>: Pull all cached keys [" + this._RegionKey + "] is completed in " + stopwatch.ElapsedMilliseconds.ToString() + " miliseconds. Total of pulled-keys: " + this._activeKeys.Count.ToString());
#endif

			// callback
			if (!object.ReferenceEquals(callback, null))
				callback();
		}
		#endregion

		#region Update (Push) Keys methods
#if DEBUG
		async Task _PushKeysAsync(string label, bool checkUpdatedKeys = true, Action callback = null)
#else
		async Task _PushKeysAsync(bool checkUpdatedKeys = true, Action callback = null)
#endif
		{
#if DEBUG
			string debug = "[" + this._name + " > " + Process.GetCurrentProcess().Id.ToString() + " : " + AppDomain.CurrentDomain.Id.ToString() + " : " + Thread.CurrentThread.ManagedThreadId.ToString() + "]";
#endif

			// stop if other task is pushing
			if (this._bag.Contains(this._RegionPushingFlag))
			{
#if DEBUG
				Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: Stop the pushing process because other task is pushing now [" + this._RegionPushingFlag + ":" + (this._bag.Get(this._RegionPushingFlag) as string) + "]");
#endif
				return;
			}
			else if (checkUpdatedKeys && this._addedKeys.Count < 1 && this._removedKeys.Count < 1)
			{
#if DEBUG
				Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: Stop the pushing process because no key need to update [" + this._RegionUpdatingFlag + (this._bag.Contains(this._RegionUpdatingFlag) ? ":" + (this._bag.Get(this._RegionUpdatingFlag) as string) : "") + "]");
#endif

				if (this._bag.Contains(this._RegionUpdatingFlag))
					this._bag.Remove(this._RegionUpdatingFlag);
				return;
			}

#if DEBUG
			Stopwatch stopwatch = new Stopwatch(), watch = new Stopwatch();
			stopwatch.Start();
			watch.Start();
#endif

			// set flag (in-process)
			this._bag.Set(this._RegionPushingFlag, Thread.CurrentThread.ManagedThreadId.ToString(), new CacheItemPolicy() { SlidingExpiration = TimeSpan.FromSeconds(30), Priority = CacheItemPriority.NotRemovable });

#if DEBUG
			watch.Stop();
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: Set pushing flag [" + this._RegionPushingFlag + ":" + Thread.CurrentThread.ManagedThreadId.ToString() + "] is completed in " + watch.ElapsedMilliseconds.ToString() + " miliseconds");
#endif

			// wait for other task/thread complete update distributed cache
			var attempt = 0;
			string distributedFlag = null;
			try
			{
				distributedFlag = DistributedCache.Get<string>(this._RegionPushingFlag);
			}
			catch (Exception ex)
			{
				CacheManager.WriteLogs(this._name, "Error occurred while updating flag when push keys of the region", ex);
			}

			while (distributedFlag != null && attempt < 3)
			{
				attempt++;

#if DEBUG
				Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: Wait for other task complete the pushing process [" + this._RegionPushingFlag + ":" + distributedFlag + "]: (" + attempt.ToString() + ")");
#endif

				await Task.Delay(113);
				try
				{
					distributedFlag = DistributedCache.Get<string>(this._RegionPushingFlag);
				}
				catch (Exception ex)
				{
					CacheManager.WriteLogs(this._name, "Error occurred while updating flag when push keys of the region", ex);
					distributedFlag = null;
				}
			}

#if DEBUG
			watch.Restart();
#endif

			// set flag (distributed)
			try
			{
				DistributedCache.Set(this._RegionPushingFlag, Thread.CurrentThread.ManagedThreadId.ToString(), (long)5000);
			}
			catch (Exception ex)
			{
				CacheManager.WriteLogs(this._name, "Error occurred while updating flag when push keys of the region", ex);
			}

#if DEBUG
			watch.Stop();
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: Set pushing flag [" + this._RegionPushingFlag + ":" + Thread.CurrentThread.ManagedThreadId.ToString() + "] (distributed) is completed in " + watch.ElapsedMilliseconds.ToString() + " miliseconds");
			watch.Restart();
#endif

			// get distributed keys
			var distributedKeys = CacheManager.FetchDistributedKeys(this._RegionKey);

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
			var syncKeys = this._activeSynchronize
				? CacheManager.Merge(false, this._activeKeys, distributedKeys)
				: distributedKeys;

			// update removed keys
			if (totalRemovedKeys > 0)
			{
				var removedKeys = CacheManager.Clone(this._removedKeys);
				foreach (string key in removedKeys)
					if (syncKeys.Contains(key))
						syncKeys.Remove(key);
			}

			// update added keys
			if (totalAddedKeys > 0)
				syncKeys = CacheManager.Merge(syncKeys, this._addedKeys);

#if DEBUG
			watch.Stop();
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: Merge keys [" + this._RegionKey + "] is completed in " + watch.ElapsedMilliseconds.ToString() + " miliseconds");
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: Total of merged-keys [" + this._RegionKey + "]: " + syncKeys.Count.ToString());
			watch.Restart();
#endif

			// update mapping keys at distributed cache
			try
			{
				CacheManager.UpdateDistributedKeys(this._RegionKey, syncKeys);
			}
			catch (Exception ex)
			{
				CacheManager.WriteLogs(this._name, "Error occurred while pushing keys of the region", ex);
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
					var removedKeys = CacheManager.Clone(this._removedKeys);
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
					syncKeys = CacheManager.Merge(syncKeys, this._addedKeys);

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
						CacheManager.UpdateDistributedKeys(this._RegionKey, syncKeys);
					}
					catch (Exception ex)
					{
						CacheManager.WriteLogs(this._name, "Error occurred while (re)pushing keys of the region", ex);
					}

#if DEBUG
					watch.Stop();
					Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: Re-Push merged-keys [" + this._RegionKey + "] to distributed cache is completed in " + watch.ElapsedMilliseconds.ToString() + " miliseconds");
#endif
				}
			}

			// update active keys
			if (this._activeSynchronize)
				this._bag.Set(this._RegionKey, syncKeys, new CacheItemPolicy() { SlidingExpiration = TimeSpan.FromDays(7), Priority = CacheItemPriority.NotRemovable });

			// clear added/removed keys
			if (totalAddedKeys.Equals(this._addedKeys.Count))
				try
				{
					this._lock.EnterWriteLock();
					this._addedKeys.Clear();
				}
				catch (Exception ex)
				{
					CacheManager.WriteLogs(this._name, "Error occurred while removes added keys (after pushing process)", ex);
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
					CacheManager.WriteLogs(this._name, "Error occurred while removes removed keys (after pushing process)", ex);
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
			this._bag.Remove(this._RegionUpdatingFlag);
			this._bag.Remove(this._RegionPushingFlag);
			try
			{
				DistributedCache.Remove(this._RegionPushingFlag);
			}
			catch (Exception ex)
			{
				CacheManager.WriteLogs(this._name, "Error occurred while updating flag when push keys of the region", ex);
			}

#if DEBUG
			watch.Stop();
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: Remove flags of updating/pushing [" + this._RegionUpdatingFlag + "/" + this._RegionPushingFlag + "] is completed in " + watch.ElapsedMilliseconds.ToString() + " miliseconds");
			stopwatch.Stop();
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: Push all cached keys [" + this._RegionKey + "] to distributed cache is completed in " + stopwatch.ElapsedMilliseconds.ToString() + " miliseconds. Total of pushed-keys: " + syncKeys.Count.ToString());
#endif

			// callback
			if (!object.ReferenceEquals(callback, null))
			{
#if DEBUG
				Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">:  -- And now run callback action");
#endif
				callback();
			}
		}

#if DEBUG
		void _UpdateKeys(string label, int delay = 13, bool checkUpdatedKeys = true, Action callback = null)
#else
		void _UpdateKeys(int delay = 13, bool checkUpdatedKeys = true, Action callback = null)
#endif
		{
#if DEBUG
			string debug = "[" + this._name + " > " + Process.GetCurrentProcess().Id.ToString() + " : " + AppDomain.CurrentDomain.Id.ToString() + " : " + Thread.CurrentThread.ManagedThreadId.ToString() + "]";
#endif

			if (this._bag.Contains(this._RegionUpdatingFlag))
			{
#if DEBUG
				Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label  + ">: Stop because other task is updating now [" + this._RegionUpdatingFlag + ":" + (this._bag.Get(this._RegionUpdatingFlag) as string) + "]");
#endif
				return;
			}

#if DEBUG
			Stopwatch stopwatch = new Stopwatch();
			stopwatch.Start();
#endif

			// set flag
			this._bag.Set(this._RegionUpdatingFlag, Thread.CurrentThread.ManagedThreadId.ToString(), new CacheItemPolicy() { SlidingExpiration = TimeSpan.FromSeconds(30), Priority = CacheItemPriority.NotRemovable });

#if DEBUG
			stopwatch.Stop();
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: Set updating-flag [" + this._RegionUpdatingFlag + ":" + Thread.CurrentThread.ManagedThreadId.ToString() + "] is completed in " + stopwatch.ElapsedMilliseconds.ToString() + " miliseconds");
#endif
			// push
			Task.Run(async () =>
			{
				await Task.Delay(delay);

#if DEBUG
				debug = "[" + this._name + " > " + Process.GetCurrentProcess().Id.ToString() + " : " + AppDomain.CurrentDomain.Id.ToString() + " : " + Thread.CurrentThread.ManagedThreadId.ToString() + "]";
				Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: Start to push keys [" + this._RegionKey + "] to distributed cache");
				if (this._activeKeys != null)
					Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <" + label + ">: -- Total of actived-keys: " + this._activeKeys.Count.ToString());
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
			// update active keys
			if ((this._mode.Equals(Mode.Internal) || this._activeSynchronize) && !this._activeKeys.Contains(key))
				try
				{
					this._lock.EnterWriteLock();
					this._activeKeys.Add(key);
				}
				catch (Exception ex)
				{
					CacheManager.WriteLogs(this._name, "Error occurred while updating the active keys of the region (for pushing)", ex);
				}
				finally
				{
					if (this._lock.IsWriteLockHeld)
						this._lock.ExitWriteLock();
				}

			// update added keys and push to distributed cache
			if (this._mode.Equals(Mode.Distributed) && (this._activeSynchronize || this._updateKeys))
			{
				if (!this._addedKeys.Contains(key))
					try
					{
						this._lock.EnterWriteLock();
						this._addedKeys.Add(key);
					}
					catch (Exception ex)
					{
						CacheManager.WriteLogs(this._name, "Error occurred while updating the added keys of the region (for pushing)", ex);
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
		}
		#endregion

		#region Get Keys methods
		HashSet<string> _GetKeys(bool getActiveKeysFirst, bool doClone = false)
		{
			HashSet<string> keys = null;
			try
			{
				keys = this._mode.Equals(Mode.Internal) || (this._activeSynchronize && getActiveKeysFirst)
					? this._activeKeys
					: CacheManager.FetchDistributedKeys(this._RegionKey);
			}
			catch (Exception ex)
			{
				CacheManager.WriteLogs(this._name, "Error occurred while fetching the collection of keys", ex);
			}

			return keys == null
				? new HashSet<string>()
				: doClone && this._mode.Equals(Mode.Internal)
					? CacheManager.Clone(keys)
					: keys;
		}
		#endregion

		#region Set methods
		bool _Set(string key, object value, string expirationType = null, int expirationTime = 0, bool doPush = true, CacheItemPriority priority = CacheItemPriority.Default, StoreMode mode = StoreMode.Set)
		{
			// check key & value
			if (string.IsNullOrWhiteSpace(key) || object.ReferenceEquals(value, null))
				return false;

			// prepare
			expirationType = expirationType != null && (expirationType.Equals("Absolute") || expirationType.Equals("Sliding"))
				? expirationType
				: this._expirationType;

			expirationTime = expirationTime > 0
				? expirationTime
				: this._expirationTime;

			var cacheKey = this._GetKey(key);
			var isSetted = false;

			// in-process cache
			if (this._mode.Equals(Mode.Internal))
			{
				var policy = new CacheItemPolicy()
				{
					Priority = priority,
					RemovedCallback = this._RemovedCallback
				};
				if (expirationType != null && expirationType.Equals("Absolute"))
					policy.AbsoluteExpiration = DateTime.Now.AddMinutes(expirationTime);
				else
					policy.SlidingExpiration = TimeSpan.FromMinutes(expirationTime);

				// set if not exists
				if (mode.Equals(StoreMode.Add))
					isSetted = this._bag.Add(cacheKey, value, policy);

				// set if already exists or always override/add
				else if (mode.Equals(StoreMode.Set) || (mode.Equals(StoreMode.Replace) && this._bag.Contains(cacheKey)))
				{
					this._bag.Set(cacheKey, value, policy);
					isSetted = this._bag.Contains(cacheKey);
				}
			}

			// distributed cache
			else
				try
				{
					isSetted = expirationType.Equals("Absolute")
						? DistributedCache.Set(cacheKey, value, DateTime.Now.AddMinutes(expirationTime), mode)
						: DistributedCache.Set(cacheKey, value, TimeSpan.FromMinutes(expirationTime), mode);

					// not success
					if (!isSetted)
					{
						if (!value.GetType().IsSerializable)
							throw new ArgumentException("The object (" + value.GetType().ToString() + ") must be serializable");
					}

					// success, then add monitor key (removed callback)
					else if (this._monitorKeys)
					{
						var policy = new CacheItemPolicy()
						{
							Priority = priority,
							RemovedCallback = this._RemovedCallback
						};
						if (expirationType != null && expirationType.Equals("Absolute"))
							policy.AbsoluteExpiration = DateTime.Now.AddMinutes(expirationTime);
						else
							policy.SlidingExpiration = TimeSpan.FromMinutes(expirationTime);
						this._bag.Set(cacheKey, "", policy);
					}
				}
				catch (ArgumentException)
				{
					throw;
				}
				catch (Exception ex)
				{
					CacheManager.WriteLogs(this._name, "Error occurred while updating an object into cache [" + value.GetType().ToString() + "#" + key + "]", ex);
					if (ex != null && ex.Message != null && ex.Message.Contains("object too large for cache") && this._throwObjectTooLargeForCacheException)
						throw ex;
				}

			// update mapping key when added successful
			if (isSetted)
				this._UpdateKeys(key, doPush);

			// return state
			return isSetted;
		}

		void _Set(IDictionary<string, object> items, string keyPrefix = null, string expirationType = null, int expirationTime = 0, CacheItemPriority priority = CacheItemPriority.Default, StoreMode mode = StoreMode.Set)
		{
			// check collection
			if (items == null || items.Count < 1)
				return;

#if DEBUG
			string debug = "[" + this._name + " > " + Process.GetCurrentProcess().Id.ToString() + " : " + AppDomain.CurrentDomain.Id.ToString() + " : " + Thread.CurrentThread.ManagedThreadId.ToString() + "]";
			Stopwatch stopwatch = new Stopwatch();
			stopwatch.Start();
#endif

			// set items
			foreach (var item in items)
				this._Set((string.IsNullOrWhiteSpace(keyPrefix) ? "" : keyPrefix) + item.Key, item.Value, expirationType, expirationTime, false, priority, mode);

#if DEBUG
			stopwatch.Stop();
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + "): " + "Set multiple items (" + items.Count.ToString() + ") is completed in " + stopwatch.ElapsedMilliseconds.ToString() + " miliseconds");
#endif

			// push keys
			if (this._mode.Equals(Mode.Distributed) && (this._activeSynchronize || this._updateKeys) && this._addedKeys.Count > 0)
#if DEBUG
				this._UpdateKeys("SET-MULTIPLE", 113);
#else
				this._UpdateKeys(113);
#endif
		}

		void _Set<T>(IDictionary<string, T> items, string keyPrefix = null, string expirationType = null, int expirationTime = 0, CacheItemPriority priority = CacheItemPriority.Default, StoreMode mode = StoreMode.Set)
		{
			// check collection
			if (items == null || items.Count < 1)
				return;

#if DEBUG
			string debug = "[" + this._name + " > " + Process.GetCurrentProcess().Id.ToString() + " : " + AppDomain.CurrentDomain.Id.ToString() + " : " + Thread.CurrentThread.ManagedThreadId.ToString() + "]";
			Stopwatch stopwatch = new Stopwatch();
			stopwatch.Start();
#endif

			// set items
			foreach (var item in items)
				this._Set((string.IsNullOrWhiteSpace(keyPrefix) ? "" : keyPrefix) + item.Key, item.Value, expirationType, expirationTime, false, priority, mode);

#if DEBUG
			stopwatch.Stop();
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + "): " + "Set multiple items (" + items.Count.ToString() + ") is completed in " + stopwatch.ElapsedMilliseconds.ToString() + " miliseconds");
#endif

			// push keys
			if (this._mode.Equals(Mode.Distributed) && (this._activeSynchronize || this._updateKeys) && this._addedKeys.Count > 0)
#if DEBUG
				this._UpdateKeys("SET-MULTIPLE", 113);
#else
				this._UpdateKeys(113);
#endif
		}

		bool _SetIfNotExists(string key, object value, string expirationType = null, int expirationTime = 0)
		{
			return this._Set(key, value, expirationType, expirationTime, true, CacheItemPriority.Default, StoreMode.Add);
		}

		bool _SetIfAlreadyExists(string key, object value, string expirationType = null, int expirationTime = 0)
		{
			return this._Set(key, value, expirationType, expirationTime, true, CacheItemPriority.Default, StoreMode.Replace);
		}

		bool _SetAsFragments(string key, Type type, List<byte[]> fragments, string expirationType = null, int expirationTime = 0, CacheItemPriority priority = CacheItemPriority.Default, StoreMode mode = StoreMode.Set)
		{
			// set info
			var fragment = new Fragment()
			{
				Key = key,
				Type = type.ToString() + "," + type.Assembly.FullName,
				TotalFragments = fragments.Count
			};
			var isSetted = this._Set(fragment.Key, fragment, expirationType, expirationTime, false, priority, mode);

			// set data
			if (isSetted)
			{
				var items = new Dictionary<string, object>();
				for (int index = 0; index < fragments.Count; index++)
					items.Add(this._GetFragmentKey(fragment.Key, index), fragments[index]);
				this._Set(items, null, expirationType, expirationTime, priority, mode);
			}

			return isSetted;
		}

		bool _SetAsFragments(string key, object value, string expirationType = null, int expirationTime = 0, bool setSecondary = false, CacheItemPriority priority = CacheItemPriority.Default, StoreMode mode = StoreMode.Set)
		{
			// check
			if (value == null)
				return false;
			else if (this._mode.Equals(Mode.Internal))
				return this._Set(key, value, expirationType, expirationTime, true, priority, mode);

#if DEBUG
			string debug = "[FRAGMENTS > " + Process.GetCurrentProcess().Id.ToString() + " : " + AppDomain.CurrentDomain.Id.ToString() + " : " + Thread.CurrentThread.ManagedThreadId.ToString() + "]";
#endif

			// check to see the object is serializable
			var type = value.GetType();
			if (!type.IsSerializable)
			{
				var ex = new ArgumentException("The object (" + type.ToString() + ") must be serializable");
				CacheManager.WriteLogs(this._name, "Error occurred while trying to serialize an object [" + key + "]", ex);
				throw ex;
			}

			// serialize the object to an array of bytes
			var bytes = value is byte[]
				? value as byte[]
				: null;

			if (bytes == null)
				bytes = CacheManager.SerializeAsBinary(value);

			// compress
			if (bytes != null)
				bytes = CacheManager.Compress(bytes);

			// check
			if (bytes == null || bytes.Length < 1)
				return false;

			// split into fragments
			var fragments = CacheManager.SplitIntoFragments(bytes, this._fragmentSize);

#if DEBUG
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") Serialize object and split into fragments successful [" + key + "]. Total of fragments: " + fragments.Count.ToString());
#endif

			// update into cache storage
			var isSetted = this._SetAsFragments(key, type, fragments, expirationType, expirationTime, priority, mode);

			// post-process when setted
			if (isSetted)
			{
				// monitor key (removed callback)
				if (this._monitorKeys)
				{
					var policy = new CacheItemPolicy()
					{
						Priority = CacheItemPriority.Default,
						RemovedCallback = this._RemovedCallback
					};
					if (expirationType != null && expirationType.Equals("Absolute"))
						policy.AbsoluteExpiration = DateTime.Now.AddMinutes(expirationTime);
					else
						policy.SlidingExpiration = TimeSpan.FromMinutes(expirationTime);
					this._bag.Set(this._GetKey(key), "", policy);
				}

				// update pure object (secondary) into cache
				if (setSecondary && !(value is byte[]))
					try
					{
						this._Set(key + ":(Secondary-Pure-Object)", value, expirationType, expirationTime, false, priority, mode);
					}
					catch (Exception ex)
					{
						CacheManager.WriteLogs(this._name, "Error occurred while updating an object into cache (pure object of fragments) [" + key + ":(Secondary-Pure-Object)" + "]", ex);
					}

				// update key
				this._UpdateKeys(key, true);
			}

#if DEBUG
			if (!isSetted)
				Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") Set as fragments failed [" + key + " - " + (type.ToString() + "," + type.Assembly.FullName) + "]");
			else
				Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") Set as fragments successful [" + key + " - " + (type.ToString() + "," + type.Assembly.FullName) + "]. Total of fragments: " + fragments.Count.ToString());
#endif

			// return result
			return isSetted;
		}
		#endregion

		#region Get methods
		object _Get(string key, bool autoGetFragments = true)
		{
			if (string.IsNullOrWhiteSpace(key))
				throw new ArgumentNullException(key);

#if DEBUG
			string debug = "[" + this._name + " > " + Process.GetCurrentProcess().Id.ToString() + " : " + AppDomain.CurrentDomain.Id.ToString() + " : " + Thread.CurrentThread.ManagedThreadId.ToString() + "]";
			Stopwatch stopwatch = new Stopwatch();
			stopwatch.Start();
#endif

			// get cached item
			object value = null;
			try
			{
				value = this._mode.Equals(Mode.Internal)
					? this._bag.Get(this._GetKey(key))
					: DistributedCache.Get(this._GetKey(key));
			}
			catch (Exception ex)
			{
				CacheManager.WriteLogs(this._name, "Error occurred while fetching an object from cache storage [" + key + "]", ex);
			}

			// get object as merged of all fragments
			if (autoGetFragments && value != null && value is Fragment)
				try
				{
					value = this._GetAsFragments((Fragment)value);
				}
				catch (Exception ex)
				{
					CacheManager.WriteLogs(this._name, "Error occurred while fetching an objects' fragments from cache storage [" + key + "]", ex);
					value = null;
				}

#if DEBUG
			stopwatch.Stop();
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + "): " + "Get single cache item [" + key + "] is completed in " + stopwatch.ElapsedMilliseconds.ToString() + " miliseconds");
#endif

			// return object
			return value;
		}

		IDictionary<string, object> _Get(IEnumerable<string> keys)
		{
			// check keys
			if (object.ReferenceEquals(keys, null))
				return null;

#if DEBUG
			string debug = "[" + this._name + " > " + Process.GetCurrentProcess().Id.ToString() + " : " + AppDomain.CurrentDomain.Id.ToString() + " : " + Thread.CurrentThread.ManagedThreadId.ToString() + "]";
			Stopwatch stopwatch = new Stopwatch();
			stopwatch.Start();
#endif

			// get collection of cached objects
			IDictionary<string, object> objects = null;
			if (this._mode.Equals(Mode.Distributed))
			{
				var realKeys = new List<string>();
				foreach (var key in keys)
					if (!string.IsNullOrWhiteSpace(key))
						realKeys.Add(this._GetKey(key));

				IDictionary<string, object> items = null;
				try
				{
					items = DistributedCache.Get(realKeys);
				}
				catch (Exception ex)
				{
					CacheManager.WriteLogs(this._name, "Error occurred while fetch a collection of objects from cache storage", ex);
				}

				if (items != null && items.Count > 0)
					try
					{
						objects = items.ToDictionary(
								item => item.Key.Remove(0, this._name.Length + 1),
								item => item.Value != null && item.Value is Fragment ? this._GetAsFragments((Fragment)item.Value) : item.Value
							);
					}
					catch { }
			}
			else
			{
				objects = new Dictionary<string, object>();
				foreach (var key in keys)
					if (!string.IsNullOrWhiteSpace(key))
					{
						var realKey = this._GetKey(key);
						if (!objects.ContainsKey(key) && this._bag.Contains(realKey))
							objects.Add(key, this._bag.Get(realKey));
					}
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

		List<byte[]> _GetAsFragments(string key, List<int> indexes)
		{
			if (string.IsNullOrWhiteSpace(key))
				return null;

			var fragments = new List<byte[]>();
			indexes.ForEach(index =>
			{
				var bytes = index < 0 ? null : this._Get(this._GetFragmentKey(key, index), false) as byte[];
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
			byte[] fragments = new byte[0];
			int length = 0;
			for (int index = 0; index < fragment.TotalFragments; index++)
			{
				byte[] bytes = null;
				string fragmentKey = this._GetFragmentKey(fragment.Key, index);
				try
				{
					bytes = DistributedCache.Get<byte[]>(fragmentKey);
				}
				catch (Exception ex)
				{
					CacheManager.WriteLogs(this._name, "Error occurred while fetching fragments from cache [" + fragmentKey + "]", ex);
				}

				if (bytes != null && bytes.Length > 0)
				{
					Array.Resize<byte>(ref fragments, length + bytes.Length);
					Array.Copy(bytes, 0, fragments, length, bytes.Length);
					length += bytes.Length;
				}
			}

			// decompress
			if (fragments.Length > 0)
				try
				{
					fragments = CacheManager.Decompress(fragments);
				}
				catch { }

			// deserialize object
			object @object = Type.GetType(fragment.Type).Equals(typeof(byte[])) && fragments.Length > 0
				? fragments
				: null;

			if (@object == null && fragments.Length > 0)
				try
				{
					@object = CacheManager.DeserializeFromBinary(fragments);
				}
				catch (Exception ex)
				{
					CacheManager.WriteLogs(this._name, "Error occurred while trying to get fragmented object (error occured while serializing the object from an array of bytes) [" + fragment.Key + "]", ex);
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
				fragment = DistributedCache.Get(key);
			}
			catch (Exception ex)
			{
				CacheManager.WriteLogs(this._name, "Error occurred while fetching fragments from cache [" + key + "]", ex);
			}

			return fragment != null && fragment is Fragment && ((Fragment)fragment).TotalFragments > 0
				? this._GetAsFragments((Fragment)fragment)
				: null;
		}
		#endregion

		#region Remove methods
		bool _Remove(string key, bool doPush = true)
		{
			// check
			if (string.IsNullOrWhiteSpace(key))
				return false;

			// remove
			var isRemoved = false;
			try
			{
				isRemoved = this._mode.Equals(Mode.Internal)
					? this._bag.Remove(this._GetKey(key)) != null
					: DistributedCache.Remove(this._GetKey(key));
			}
			catch (Exception ex)
			{
				CacheManager.WriteLogs(this._name, "Error occurred while removing an object from cache storage [" + key + "]", ex);
			}

			// update mapping key when removed successful
			if (isRemoved)
			{
				// update active keys
				if ((this._mode.Equals(Mode.Internal) || this._activeSynchronize) && this._activeKeys.Contains(key))
					try
					{
						this._lock.EnterWriteLock();
						this._activeKeys.Remove(key);
					}
					catch (Exception ex)
					{
						CacheManager.WriteLogs(this._name, "Error occurred while updating the active keys (for pushing)", ex);
					}
					finally
					{
						if (this._lock.IsWriteLockHeld)
							this._lock.ExitWriteLock();
					}

				// update removed keys and push to distributed cache
				if (this._mode.Equals(Mode.Distributed) && (this._activeSynchronize || this._updateKeys))
				{
					if (!this._removedKeys.Contains(key))
						try
						{
							this._lock.EnterWriteLock();
							this._removedKeys.Add(key);
						}
						catch (Exception ex)
						{
							CacheManager.WriteLogs(this._name, "Error occurred while updating the removed keys (for pushing)", ex);
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
			return isRemoved;
		}

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
			if (this._mode.Equals(Mode.Distributed) && (this._activeSynchronize || this._updateKeys) && this._removedKeys.Count > 0)
#if DEBUG
				this._UpdateKeys("REMOVE-MULTIPLE", 213);
#else
				this._UpdateKeys(213);
#endif
		}

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

		void _RemovedCallback(CacheEntryRemovedArguments args)
		{
			if (args == null || args.RemovedReason.Equals(CacheEntryRemovedReason.Removed)
				|| args.Source == null || args.CacheItem == null || args.CacheItem.Key == null)
				return;

			if (this._mode.Equals(Mode.Internal) || this._activeSynchronize)
			{
				var key = args.CacheItem.Key.Remove(0, this._name.Length + 1);
				if (!string.IsNullOrWhiteSpace(key) && this._activeKeys.Contains(key))
					try
					{
						this._lock.EnterWriteLock();
						this._activeKeys.Remove(key);
					}
					catch (Exception ex)
					{
						CacheManager.WriteLogs(this._name, "Error occurred while updating the active keys (RemovedCallback)", ex);
					}
					finally
					{
						if (this._lock.IsWriteLockHeld)
							this._lock.ExitWriteLock();
					}
			}

			if (this._mode.Equals(Mode.Distributed) && (this._activeSynchronize || this._updateKeys))
			{
				try
				{
					this._lock.EnterWriteLock();
					this._removedKeys.Add(args.CacheItem.Key.Remove(0, this._name.Length + 1));
				}
				catch (Exception ex)
				{
					CacheManager.WriteLogs(this._name, "Error occurred while updating the removed keys (RemovedCallback)", ex);
				}
				finally
				{
					if (this._lock.IsWriteLockHeld)
						this._lock.ExitWriteLock();
				}

				if (this._removedKeys.Count > 0)
#if DEBUG
					this._UpdateKeys("REMOVED-CALLBACK", 213);
#else
					this._UpdateKeys(213);
#endif
			}

			if (this._mode.Equals(Mode.Distributed) && this._monitorKeys)
				try
				{
					DistributedCache.Remove(args.CacheItem.Key);
				}
				catch (Exception ex)
				{
					CacheManager.WriteLogs(this._name, "Error occurred while removing an object from cache storage (RemovedCallback)", ex);
				}
		}
		#endregion

		#region Exists method
		bool _Exists(string key)
		{
			var exist = false;
			if (!string.IsNullOrWhiteSpace(key))
				try
				{
					exist = this._mode.Equals(Mode.Internal)
						? this._bag.Contains(this._GetKey(key))
						: DistributedCache.Exists(this._GetKey(key));
				}
				catch (Exception ex)
				{
					CacheManager.WriteLogs(this._name, "Error occurred while checking existing state of an object [" + key + "]", ex);
				}
			return exist;
		}
		#endregion

		#region Clear methods
		void _Clear()
		{
#if DEBUG
			Stopwatch stopwatch = new Stopwatch();
			stopwatch.Start();
			string debug = "[" + this._name + " > " + Process.GetCurrentProcess().Id.ToString() + " : " + AppDomain.CurrentDomain.Id.ToString() + " : " + Thread.CurrentThread.ManagedThreadId.ToString() + "]";
#endif

			// get keys
			var keys = this._GetKeys(true, true);

#if DEBUG
			stopwatch.Stop();
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <CLEAR>: Get keys to clear [" + this._RegionKey + "] is completed in " + stopwatch.ElapsedMilliseconds.ToString() + " miliseconds");
			stopwatch.Restart();
#endif

			// clear cached items
			foreach (var key in keys)
				if (this._mode.Equals(Mode.Internal))
					this._bag.Remove(this._GetKey(key));

				else
				{
					try
					{
						DistributedCache.Remove(this._GetKey(key));
					}
					catch (Exception ex)
					{
						CacheManager.WriteLogs(this._name, "Error occurred while performing clear process", ex);
					}

					if (this._monitorKeys)
						this._bag.Remove(this._GetKey(key));
				}

			// update active keys
			if (this._mode.Equals(Mode.Internal) || this._activeSynchronize)
				try
				{
					this._lock.EnterWriteLock();
					this._activeKeys.Clear();
				}
				catch (Exception ex)
				{
					CacheManager.WriteLogs(this._name, "Error occurred while updating the active keys (after clear)", ex);
				}
				finally
				{
					if (this._lock.IsWriteLockHeld)
						this._lock.ExitWriteLock();
				}

			// update mapping keys and push to distributed cache
			if (this._mode.Equals(Mode.Distributed))
			{
				var isRemoved = false;
				try
				{
					isRemoved = DistributedCache.Remove(this._RegionKey);
				}
				catch (Exception ex)
				{
					CacheManager.WriteLogs(this._name, "Error occurred while updating the region keys (after clear)", ex);
				}

				if (!isRemoved)
					try
					{
						DistributedCache.Set(this._RegionKey, new HashSet<string>(), TimeSpan.FromMinutes(this._expirationTime + 15));
					}
					catch (Exception ex)
					{
						CacheManager.WriteLogs(this._name, "Error occurred while trying to set the region keys (after clear)", ex);
					}

				try
				{
					this._lock.EnterWriteLock();
					this._addedKeys.Clear();
					this._removedKeys.Clear();
				}
				catch (Exception ex)
				{
					CacheManager.WriteLogs(this._name, "Error occurred while updating the added/removed keys (after clear)", ex);
				}
				finally
				{
					if (this._lock.IsWriteLockHeld)
						this._lock.ExitWriteLock();
				}

				// remove flags
				this._bag.Remove(this._RegionUpdatingFlag);
				this._bag.Remove(this._RegionPushingFlag);
				try
				{
					DistributedCache.Remove(this._RegionPushingFlag);
				}
				catch (Exception ex)
				{
					CacheManager.WriteLogs(this._name, "Error occurred while updating the flag (after clear)", ex);
				}
			}

#if DEBUG
			stopwatch.Stop();
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <CLEAR>: Clear all cached items [" + this._RegionKey + "] is completed in " + stopwatch.ElapsedMilliseconds.ToString() + " miliseconds");
#endif
		}

		void _ClearAll()
		{
			Task.Run(async () =>
			{
				await this._ClearAllAsync().ConfigureAwait(false);
			}).ConfigureAwait(false);
		}

		async Task _ClearAllAsync()
		{
			await Task.WhenAll(
					this._ClearInProcessAsync(),
					this._ClearDistributedAsync()
				).ConfigureAwait(false);
		}

		async Task _ClearInProcessAsync()
		{
			// delay in a moment
			await Task.Delay(113);

#if DEBUG
			Stopwatch stopwatch = new Stopwatch();
			stopwatch.Start();
			string debug = "[" + this._name + " > " + Process.GetCurrentProcess().Id.ToString() + " : " + AppDomain.CurrentDomain.Id.ToString() + " : " + Thread.CurrentThread.ManagedThreadId.ToString() + "]";
#endif

			// prepare keys
			var keys = new List<string>();
			try
			{
				foreach (var item in MemoryCache.Default)
					keys.Add(item.Key);
			}
			catch (Exception ex)
			{
				CacheManager.WriteLogs(this._name, "Error occurred while fetching the keys of memory cache for cleaning (clear all)", ex);
			}

			// remove all
			foreach (var key in keys)
				try
				{
					MemoryCache.Default.Remove(key);
				}
				catch (Exception ex)
				{
					CacheManager.WriteLogs(this._name, "Error occurred while removing an object from memory cache (clear all)", ex);
				}

#if DEBUG
			stopwatch.Stop();
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <CLEAR-ALL>: Process to clear in-process cache is completed in " + stopwatch.ElapsedMilliseconds.ToString() + " miliseconds");
#endif
		}

		async Task _ClearDistributedAsync()
		{
			// delay in a moment
			await Task.Delay(213);

#if DEBUG
			Stopwatch stopwatch = new Stopwatch();
			stopwatch.Start();
			string debug = "[" + this._name + " > " + Process.GetCurrentProcess().Id.ToString() + " : " + AppDomain.CurrentDomain.Id.ToString() + " : " + Thread.CurrentThread.ManagedThreadId.ToString() + "]";
#endif

			// remove all
			try
			{
				DistributedCache.RemoveAll();
			}
			catch (Exception ex)
			{
				CacheManager.WriteLogs(this._name, "Error occurred while performing remove all objects from distributed cache (clear all)", ex);
			}

#if DEBUG
			stopwatch.Stop();
			Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <CLEAR-ALL>: Process to clear distributed cache is completed in " + stopwatch.ElapsedMilliseconds.ToString() + " miliseconds");
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
			return this._GetKeys(true);
		}
		#endregion

		#region [Public] Set methods
		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationType">The type of object expiration (Sliding/Absolute)</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <param name="priority">Relative priority of cached item (only applied for Internal mode)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Set(string key, object value, string expirationType = null, int expirationTime = 0, CacheItemPriority priority = CacheItemPriority.Default)
		{
			return this._Set(key, value, expirationType, expirationTime, true, priority);
		}

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool Set(string key, object value, int expirationTime)
		{
			return this.Set(key, value, this._expirationType, expirationTime);
		}

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="expirationType">The type of object expiration (Slidding/Absolute)</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		public void Set(IDictionary<string, object> items, string keyPrefix = null, string expirationType = null, int expirationTime = 0)
		{
			this._Set(items, keyPrefix, expirationType, expirationTime);
		}

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="expirationType">The type of object expiration (Slidding/Absolute)</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		public void Set<T>(IDictionary<string, T> items, string keyPrefix = null, string expirationType = null, int expirationTime = 0)
		{
			this._Set(items, keyPrefix, expirationType, expirationTime);
		}

		/// <summary>
		/// Adds an item into cache with a specified key using absolute expired policy (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool SetAbsolute(string key, object value, int expirationTime = 0)
		{
			return this.Set(key, value, "Absolute", expirationTime);
		}

		/// <summary>
		/// Adds an item into cache with a specified key using sliding expired policy (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The interval time (in minutes) that the object will expired if got no access</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool SetSliding(string key, object value, int expirationTime = 0)
		{
			return this.Set(key, value, "Sliding", expirationTime);
		}

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationType">The type of object expiration (Slidding/Absolute)</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool SetIfNotExists(string key, object value, string expirationType = null, int expirationTime = 0)
		{
			return this._SetIfNotExists(key, value, expirationType, expirationTime);
		}

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationType">The type of object expiration (Slidding/Absolute)</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool SetIfAlreadyExists(string key, object value, string expirationType = null, int expirationTime = 0)
		{
			return this._SetIfAlreadyExists(key, value, expirationType, expirationTime);
		}

		/// <summary>
		/// Adds an item (as fragments) into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="type">The object that presents type of object that serialized as all fragments</param>
		/// <param name="fragments">The collection that contains all fragments (object that serialized as binary - array bytes)</param>
		/// <param name="expirationType">The type of object expiration (Sliding/Absolute)</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool SetFragments(string key, Type type, List<byte[]> fragments, string expirationType = null, int expirationTime = 0)
		{
			return this._SetAsFragments(key, type, fragments, expirationType, expirationTime);
		}

		/// <summary>
		/// Serializes object into array of bytes, splits into one or more fragments and updates into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationType">The type of object expiration (Sliding/Absolute)</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <param name="setSecondary">true to add secondary item as pure object</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public bool SetAsFragments(string key, object value, string expirationType = null, int expirationTime = 0, bool setSecondary = false)
		{
			return this._SetAsFragments(key, value, expirationType, expirationTime, setSecondary);
		}
		#endregion

		#region [Public] Get methods
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
			object @object = this.Get(key);
			return !object.ReferenceEquals(@object, null) && @object is T
				? (T)@object
				: default(T);
		}

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
		/// Gets fragment information that associates with the key (only available when working with distributed cache)
		/// </summary>
		/// <param name="key">The string that presents key of fragment information</param>
		/// <returns>The <see cref="Fragment">Fragment</see> object that presents information of all fragmented items in the cache storage</returns>
		public Fragment GetFragment(string key)
		{
			object fragment = null;
			if (!string.IsNullOrWhiteSpace(key) && this._mode.Equals(Mode.Distributed))
				fragment = this._Get(key, false);

			return fragment == null
						? new Fragment() { Key = key, TotalFragments = 0 }
						: fragment is Fragment ? (Fragment)fragment : new Fragment() { Key = key, TotalFragments = 0 };
		}

		/// <summary>
		/// Gets cached of fragmented items that associates with the key and indexes (only available when working with distributed cache)
		/// </summary>
		/// <param name="key">The string that presents key of all fragmented items</param>
		/// <param name="indexes">The collection that presents indexes of all fragmented items need to get</param>
		/// <returns>The collection of array of bytes that presents serialized information of fragmented items</returns>
		public List<byte[]> GetAsFragments(string key, List<int> indexes)
		{
			return string.IsNullOrWhiteSpace(key) || this._mode.Equals(Mode.Internal)
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
			if (string.IsNullOrWhiteSpace(key) || this._mode.Equals(Mode.Internal))
				return null;

			List<int> indexesList = new List<int>();
			if (indexes != null && indexes.Length > 0)
				foreach (int index in indexes)
					indexesList.Add(index);

			return this.GetAsFragments(key, indexesList);
		}
		#endregion

		#region [Public] Remove methods
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
		/// Removes a collection of cached items
		/// </summary>
		/// <param name="keys">The collection that presents key of cached items need to remove</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		public void Remove(IEnumerable<string> keys, string keyPrefix = null)
		{
			this._Remove(keys, keyPrefix);
		}

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
		#endregion

		#region [Public] Exists methods
		/// <summary>
		/// Determines whether an item exists in the cache
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to check</param>
		/// <returns>Returns a boolean value indicating if the object that associates with the key is cached or not</returns>
		public bool Exists(string key)
		{
			return this._Exists(key);
		}
		#endregion

		#region [Public] Clear methods
		/// <summary>
		/// Clears the cache storage of this isolated region
		/// </summary>
		public void Clear()
		{
			this._Clear();
		}

		/// <summary>
		/// Clears all the cache storages (current in-process cache storage and distributed cache - mean forces the system to reload everything)
		/// </summary>
		public void ClearAll()
		{
			this._ClearAll();
		}
		#endregion

		// -----------------------------------------------------

		#region [Public Async] Keys
		/// <summary>
		/// Gets the collection of keys that associates with the cached items
		/// </summary>
		public Task<HashSet<string>> GetKeysAsync()
		{
			try
			{
				return Task.FromResult(this.GetKeys());
			}
			catch (Exception ex)
			{
				return Task.FromException<HashSet<string>>(ex);
			}
		}
		#endregion

		#region [Public Async] Set methods
		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationType">The type of object expiration (Sliding/Absolute)</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <param name="priority">Relative priority of cached item (only applied for Internal mode)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetAsync(string key, object value, string expirationType = null, int expirationTime = 0, CacheItemPriority priority = CacheItemPriority.Default)
		{
			try
			{
				return Task.FromResult(this.Set(key, value, expirationType, expirationTime, priority));
			}
			catch (Exception ex)
			{
				return Task.FromException<bool>(ex);
			}
		}

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetAsync(string key, object value, int expirationTime)
		{
			return this.SetAsync(key, value, this._expirationType, expirationTime);
		}

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="expirationType">The type of object expiration (Slidding/Absolute)</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		public Task SetAsync(IDictionary<string, object> items, string keyPrefix = null, string expirationType = null, int expirationTime = 0)
		{
			try
			{
				this.Set(items, keyPrefix, expirationType, expirationTime);
				return Task.CompletedTask;
			}
			catch (Exception ex)
			{
				return Task.FromException(ex);
			}
		}

		/// <summary>
		/// Adds a collection of items into cache
		/// </summary>
		/// <param name="items">The collection of items to add</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		/// <param name="expirationType">The type of object expiration (Slidding/Absolute)</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		public Task SetAsync<T>(IDictionary<string, T> items, string keyPrefix = null, string expirationType = null, int expirationTime = 0)
		{
			try
			{
				this.Set(items, keyPrefix, expirationType, expirationTime);
				return Task.CompletedTask;
			}
			catch (Exception ex)
			{
				return Task.FromException(ex);
			}
		}

		/// <summary>
		/// Adds an item into cache with a specified key using absolute expired policy (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetAbsoluteAsync(string key, object value, int expirationTime = 0)
		{
			return this.SetAsync(key, value, "Absolute", expirationTime);
		}

		/// <summary>
		/// Adds an item into cache with a specified key using sliding expired policy (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationTime">The interval time (in minutes) that the object will expired if got no access</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetSlidingAsync(string key, object value, int expirationTime = 0)
		{
			return this.SetAsync(key, value, "Sliding", expirationTime);
		}

		/// <summary>
		/// Adds an item into cache with a specified key when the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationType">The type of object expiration (Slidding/Absolute)</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetIfNotExistsAsync(string key, object value, string expirationType = null, int expirationTime = 0)
		{
			try
			{
				return Task.FromResult<bool>(this.SetIfNotExists(key, value, expirationType, expirationTime));
			}
			catch (Exception ex)
			{
				return Task.FromException<bool>(ex);
			}
		}

		/// <summary>
		/// Adds an item into cache with a specified key when the key is existed (means replace the existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationType">The type of object expiration (Slidding/Absolute)</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetIfAlreadyExistsAsync(string key, object value, string expirationType = null, int expirationTime = 0)
		{
			try
			{
				return Task.FromResult<bool>(this.SetIfAlreadyExists(key, value, expirationType, expirationTime));				
			}
			catch (Exception ex)
			{
				return Task.FromException<bool>(ex);
			}
		}

		/// <summary>
		/// Adds an item (as fragments) into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="type">The object that presents type of object that serialized as all fragments</param>
		/// <param name="fragments">The collection that contains all fragments (object that serialized as binary - array bytes)</param>
		/// <param name="expirationType">The type of object expiration (Sliding/Absolute)</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetFragmentsAsync(string key, Type type, List<byte[]> fragments, string expirationType = null, int expirationTime = 0)
		{
			try
			{
				return Task.FromResult(this.SetFragments(key, type, fragments, expirationType, expirationTime));
			}
			catch (Exception ex)
			{
				return Task.FromException<bool>(ex);
			}
		}

		/// <summary>
		/// Serializes object into array of bytes, splitted into one or more fragments and update into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="expirationType">The type of object expiration (Sliding/Absolute)</param>
		/// <param name="expirationTime">The time (in minutes) that the object will expired (from added time)</param>
		/// <param name="setSecondary">true to add secondary item as pure object</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> SetAsFragmentsAsync(string key, object value, string expirationType = null, int expirationTime = 0, bool setSecondary = false)
		{
			try
			{
				return Task.FromResult(this.SetAsFragments(key, value, expirationType, expirationTime, setSecondary));
			}
			catch (Exception ex)
			{
				return Task.FromException<bool>(ex);
			}
		}
		#endregion

		#region [Public Async] Get methods
		/// <summary>
		/// Retreives a cached item
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to retreive</param>
		/// <returns>The retrieved cache item, or a null reference if the key is not found</returns>
		/// <exception cref="System.ArgumentNullException">If the <paramref name="key">key</paramref> parameter is null</exception>
		public Task<object> GetAsync(string key)
		{
			try
			{
				return Task.FromResult(this.Get(key));
			}
			catch (Exception ex)
			{
				return Task.FromException<object>(ex);
			}
		}

		/// <summary>
		/// Retreives a cached item
		/// </summary>
		/// <typeparam name="T">The type for casting the cached item</typeparam>
		/// <param name="key">The string that presents key of cached item need to retreive</param>
		/// <returns>The retrieved cache item, or a null reference if the key is not found</returns>
		/// <exception cref="System.ArgumentNullException">If the <paramref name="key">key</paramref> parameter is null</exception>
		public Task<T> GetAsync<T>(string key)
		{
			try
			{
				return Task.FromResult(this.Get<T>(key));
			}
			catch (Exception ex)
			{
				return Task.FromException<T>(ex);
			}
		}

		/// <summary>
		/// Retreives a collection of cached items
		/// </summary>
		/// <param name="keys">The collection of items' keys</param>
		/// <returns>The collection of cache items</returns>
		public Task<IDictionary<string, object>> GetAsync(IEnumerable<string> keys)
		{
			try
			{
				return Task.FromResult(this.Get(keys));
			}
			catch (Exception ex)
			{
				return Task.FromException<IDictionary<string, object>>(ex);
			}
		}

		/// <summary>
		/// Gets fragment information that associates with the key (only available when working with distributed cache)
		/// </summary>
		/// <param name="key">The string that presents key of fragment information</param>
		/// <returns>The <see cref="Fragment">Fragment</see> object that presents information of all fragmented items in the cache storage</returns>
		public Task<Fragment> GetFragmentAsync(string key)
		{
			try
			{
				return Task.FromResult(this.GetFragment(key));
			}
			catch (Exception ex)
			{
				return Task.FromException<Fragment>(ex);
			}
		}

		/// <summary>
		/// Gets cached of fragmented items that associates with the key and indexes (only available when working with distributed cache)
		/// </summary>
		/// <param name="key">The string that presents key of all fragmented items</param>
		/// <param name="indexes">The collection that presents indexes of all fragmented items need to get</param>
		/// <returns>The collection of array of bytes that presents serialized information of fragmented items</returns>
		public Task<List<byte[]>> GetAsFragmentsAsync(string key, List<int> indexes)
		{
			try
			{
				return Task.FromResult(this.GetAsFragments(key, indexes));
			}
			catch (Exception ex)
			{
				return Task.FromException< List<byte[]>>(ex);
			}
		}
		#endregion

		#region [Public Async] Remove methods
		/// <summary>
		/// Removes a cached item
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to remove</param>
		/// <returns>Returns a boolean value indicating if the item is removed or not</returns>
		public Task<bool> RemoveAsync(string key)
		{
			try
			{
				return Task.FromResult(this.Remove(key));
			}
			catch (Exception ex)
			{
				return Task.FromException<bool>(ex);
			}
		}

		/// <summary>
		/// Removes a collection of cached items
		/// </summary>
		/// <param name="keys">The collection that presents key of cached items need to remove</param>
		/// <param name="keyPrefix">The string that presents prefix of all keys</param>
		public Task RemoveAsync(IEnumerable<string> keys, string keyPrefix = null)
		{
			try
			{
				this.Remove(keys, keyPrefix);
				return Task.CompletedTask;
			}
			catch (Exception ex)
			{
				return Task.FromException(ex);
			}
		}

		/// <summary>
		/// Removes a cached item (with first 100 fragments) from cache storage
		/// </summary>
		/// <param name="key">The string that presents key of fragmented items need to be removed</param>
		public Task RemoveFragmentsAsync(string key)
		{
			try
			{
				this.RemoveFragments(key);
				return Task.CompletedTask;
			}
			catch (Exception ex)
			{
				return Task.FromException(ex);
			}
		}

		/// <summary>
		/// Removes all fragmented items from cache storage
		/// </summary>
		/// <param name="fragment">The <see cref="Fragment">Fragment</see> object that presents information of all fragmented items in the cache storage need to be removed</param>
		public Task RemoveFragmentsAsync(Fragment fragment)
		{
			try
			{
				this.RemoveFragments(fragment);
				return Task.CompletedTask;
			}
			catch (Exception ex)
			{
				return Task.FromException(ex);
			}
		}
		#endregion

		#region [Public Async] Exists methods
		/// <summary>
		/// Determines whether an item exists in the cache
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to check</param>
		/// <returns>Returns a boolean value indicating if the object that associates with the key is cached or not</returns>
		public Task<bool> ExistsAsync(string key)
		{
			try
			{
				return Task.FromResult(this.Exists(key));
			}
			catch (Exception ex)
			{
				return Task.FromException<bool>(ex);
			}
		}
		#endregion

		#region [Public] Clear methods
		/// <summary>
		/// Clears the cache storage of this isolated region
		/// </summary>
		public Task ClearAsync()
		{
			try
			{
				this.Clear();
				return Task.CompletedTask;
			}
			catch (Exception ex)
			{
				return Task.FromException(ex);
			}
		}

		/// <summary>
		/// Clears all the cache storages (current in-process cache storage and distributed cache - mean forces the system to reload everything)
		/// </summary>
		public Task ClearAllAsync()
		{
			return this._ClearAllAsync();
		}
		#endregion

		// -----------------------------------------------------

		#region [Static] Helper methods
		static HashSet<string> Merge(params HashSet<string>[] sets)
		{
			return CacheManager.Merge(true, sets);
		}

		static HashSet<string> Merge(bool doClone, params HashSet<string>[] sets)
		{
			if (sets == null || sets.Length < 1)
				return null;

			else if (sets.Length < 2)
				return doClone
					? CacheManager.Clone(sets[0])
					: sets[0];

			var @object = doClone
				? CacheManager.Clone(sets[0])
				: sets[0];

			for (int index = 1; index < sets.Length; index++)
			{
				var set = doClone
					? CacheManager.Clone(sets[index])
					: sets[index];

				if (set == null || set.Count < 1)
					continue;

				foreach (var @string in set)
					if (!string.IsNullOrWhiteSpace(@string) && !@object.Contains(@string))
						@object.Add(@string);
			}

			return @object;
		}

		static List<byte[]> SplitIntoFragments(byte[] data, int sizeOfOneFragment)
		{
			var fragments = new List<byte[]>();
			int index = 0, length = data.Length;
			while (index < data.Length)
			{
				var size = sizeOfOneFragment > length
					? length
					: sizeOfOneFragment;

				var fragment = new byte[size];
				Array.Copy(data, index, fragment, 0, size);
				fragments.Add(fragment);

				index += size;
				length -= size;
			}
			return fragments;
		}

		static byte[] SerializeAsBinary(object @object)
		{
			using (var stream = new MemoryStream())
			{
				(new BinaryFormatter()).Serialize(stream, @object);
				return stream.GetBuffer();
			}
		}

		static object DeserializeFromBinary(byte[] data)
		{
			using (var stream = new MemoryStream(data))
			{
				return (new BinaryFormatter()).Deserialize(stream);
			}
		}

		static T Clone<T>(T @object)
		{
			return (T)CacheManager.DeserializeFromBinary(CacheManager.SerializeAsBinary(@object));
		}

		static byte[] Compress(byte[] data)
		{
			using (var stream = new MemoryStream())
			{
				using (var deflate = new DeflateStream(stream, CompressionMode.Compress))
				{
					deflate.Write(data, 0, data.Length);
					deflate.Close();
					return stream.ToArray();
				}
			}
		}

		static byte[] Decompress(byte[] data)
		{
			using (var input = new MemoryStream(data))
			{
				using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
				{
					using (var output = new MemoryStream())
					{
						var buffer = new byte[64];
						int readBytes = -1;
						readBytes = deflate.Read(buffer, 0, buffer.Length);
						while (readBytes > 0)
						{
							output.Write(buffer, 0, readBytes);
							readBytes = deflate.Read(buffer, 0, buffer.Length);
						}
						deflate.Close();
						return output.ToArray();
					}
				}
			}
		}

		static bool UpdateDistributedKeys(string key, HashSet<string> keys)
		{
			var fragments = CacheManager.SplitIntoFragments(CacheManager.SerializeAsBinary(keys), CacheManager.DefaultFragmentSize);
			if (DistributedCache.Client.Store(
					StoreMode.Set,
					CacheManager.RegionsKey,
					new Fragment()
					{
						Key = key,
						Type = keys.GetType().ToString() + "," + keys.GetType().Assembly.FullName,
						TotalFragments = fragments.Count
					},
					TimeSpan.Zero
				)
			)
			{
				for (int index = 0; index < fragments.Count; index++)
					DistributedCache.Client.Store(StoreMode.Set, key + ":" + index, fragments[index], TimeSpan.Zero);
				return true;
			}
			return false;
		}

		static HashSet<string> FetchDistributedKeys(string key)
		{
			var info = DistributedCache.Client.Get<Fragment>(key);
			if (object.ReferenceEquals(info, null))
				return new HashSet<string>();

			// get all fragments
			byte[] fragments = new byte[0];
			int length = 0;
			for (int index = 0; index < info.TotalFragments; index++)
			{
				byte[] bytes = null;
				try
				{
					bytes = DistributedCache.Get<byte[]>(key + ":" + index);
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
				return CacheManager.DeserializeFromBinary(fragments) as HashSet<string>;
			}
			catch
			{
				return new HashSet<string>();
			}
		}
		#endregion

		// -----------------------------------------------------

		#region [Static] Region properties & methods
		static readonly string RegionsKey = "VIEApps-Caching-Regions";

		/// <summary>
		/// Gets the collection of all registered regions (in distributed cache)
		/// </summary>
		public static HashSet<string> AllRegions
		{
			get
			{
				return CacheManager.FetchDistributedKeys(CacheManager.RegionsKey);
			}
		}

		static async Task RegisterRegionAsync(string name)
		{
			// set flag
			var attempt = 0;
			string distributedFlag = null;
			try
			{
				distributedFlag = await DistributedCache.GetAsync<string>(CacheManager.RegionsKey + "-Registering");
			}
			catch { }

			while (distributedFlag != null && attempt < 3)
			{
				attempt++;
				await Task.Delay(313);
				try
				{
					distributedFlag = await DistributedCache.GetAsync<string>(CacheManager.RegionsKey + "-Registering");
				}
				catch (Exception ex)
				{
					CacheManager.WriteLogs(name, "Error occurred while fetching flag to register new region", ex);
					distributedFlag = null;
				}
			}

			try
			{
				await DistributedCache.SetAsync(CacheManager.RegionsKey + "-Registering", "v");
			}
			catch (Exception ex)
			{
				CacheManager.WriteLogs(name, "Error occurred while updating flag when register new region", ex);
			}

			// fetch region-keys
			var regions = CacheManager.FetchDistributedKeys(CacheManager.RegionsKey);

			// register new region
			if (!regions.Contains(name))
			{
				regions.Add(name);
				try
				{
#if DEBUG
					string debug = "[" + name + " > " + Process.GetCurrentProcess().Id.ToString() + " : " + AppDomain.CurrentDomain.Id.ToString() + " : " + Thread.CurrentThread.ManagedThreadId.ToString() + "]";
#endif

					if (!CacheManager.UpdateDistributedKeys(CacheManager.RegionsKey, regions))
					{
#if DEBUG
						Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <INIT>: Cannot update regions into cache storage");
#endif

						CacheManager.WriteLogs(name, "Cannot update regions into cache storage", null);
					}
#if DEBUG
					else
						Debug.WriteLine(debug + " (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") <INIT>: Update regions into cache storage successful");
#endif
				}
				catch (Exception ex)
				{
					CacheManager.WriteLogs(name, "Error occurred while updating regions", ex);
				}
			}

			// remove flag
			try
			{
				await DistributedCache.RemoveAsync(CacheManager.RegionsKey + "-Registering");
			}
			catch (Exception ex)
			{
				CacheManager.WriteLogs(name, "Error occurred while removing flag when register new region", ex);
			}
		}
		#endregion

		// -----------------------------------------------------

		#region [Static] Working with logs
		static string LogsPath = null;

		static async Task WriteLogs(string filePath, string region, List<string> logs, Exception ex)
		{
			// prepare
			var info = DateTime.Now.ToString("HH:mm:ss.fff") + "\t" + "[" + Process.GetCurrentProcess().Id.ToString()
				+ " : " + AppDomain.CurrentDomain.Id.ToString() + " : " + Thread.CurrentThread.ManagedThreadId.ToString() + "]" + "\t" + region + "\t";

			var content = "";
			if (logs != null)
				logs.ForEach(log =>
				{
					if (!string.IsNullOrWhiteSpace(log))
						content += info + log + "\r\n";
				});

			if (ex != null)
			{
				content += info + "- " + (ex.Message != null ? ex.Message : "No error message") + " [" + ex.GetType().ToString() + "]" + "\r\n"
					+ info + "- " + (ex.StackTrace != null ? ex.StackTrace : "No stack trace");

				ex = ex.InnerException;
				var counter = 1;
				while (ex != null)
				{
					content += info + "- Inner (" + counter.ToString() + "): ----------------------------------" + "\r\n"
						+ info + "- " + (ex.Message != null ? ex.Message : "No error message") + " [" + ex.GetType().ToString() + "]" + "\r\n"
						+ info + "- " + (ex.StackTrace != null ? ex.StackTrace : "No stack trace");

					counter++;
					ex = ex.InnerException;
				}

				content += "\r\n";
			}

			// write logs into file
			try
			{
				using (var stream =  new FileStream(filePath, System.IO.FileMode.Append, System.IO.FileAccess.Write, System.IO.FileShare.ReadWrite, 4096, true))
				{
					using (var writer =  new StreamWriter(stream, System.Text.Encoding.UTF8))
					{
						await writer.WriteLineAsync(content + "\r\n");
					}
				}
			}
			catch { }
		}

		static void WriteLogs(string region, List<string> logs, Exception ex)
		{
			// prepare path of all log files
			if (CacheManager.LogsPath == null)
				try
				{
					CacheManager.LogsPath = !string.IsNullOrWhiteSpace(System.Web.HttpRuntime.AppDomainAppPath)
						? System.Web.HttpRuntime.AppDomainAppPath
						: Directory.GetCurrentDirectory();
					if (!CacheManager.LogsPath.EndsWith(@"\"))
						CacheManager.LogsPath += @"\";
				}
				catch { }

			// write logs
			if (CacheManager.LogsPath != null)
			{
				var filePath = CacheManager.LogsPath + @"Logs\" + DateTime.Now.ToString("yyyy-MM-dd") + ".CacheManager.txt";
				Task.Run(async () =>
				{
					try
					{
						await CacheManager.WriteLogs(filePath, region, logs, ex).ConfigureAwait(false);
					}
					catch { }
				}).ConfigureAwait(false);
			}
		}

		static void WriteLogs(string region, string log, Exception ex)
		{
			CacheManager.WriteLogs(region, string.IsNullOrWhiteSpace(log) ? null : new List<string>() { log }, ex);
		}
		#endregion

	}
}