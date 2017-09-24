#region Related components
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

using Enyim.Caching;
using Enyim.Caching.Memcached;
#endregion

namespace net.vieapps.Components.Caching
{
	/// <summary>
	/// The wrapper for working with distributed cache directly (using Enyim Memcached)
	/// </summary>
	public static class DistributedCache
	{

		#region Common
		static MemcachedClient _Client = new MemcachedClient();

		internal static MemcachedClient Client
		{
			get
			{
				return DistributedCache._Client;
			}
		}

		static long _ExpirationTime = 900000;

		internal static long ExpirationTime
		{
			get
			{
				return DistributedCache._ExpirationTime;
			}
		}
		#endregion

		#region Set
		/// <summary>
		/// Inserts an item into cache storage with absolute expiration
		/// </summary>
		/// <param name="key">The string that presents key of cache item</param>
		/// <param name="value">The object that presents value of cache item</param>
		/// <param name="expiresAt">The time that presents absolute expired time</param>
		/// <param name="mode">Inidicates the mode how the items are stored in Memcached (default is Set, means override if the item is existed)</param>
		/// <returns>true if set successful</returns>
		public static bool Set(string key, object value, DateTime expiresAt, StoreMode mode = StoreMode.Set)
		{
			return !string.IsNullOrWhiteSpace(key)
				? DistributedCache.Client.Store(mode, key, value, expiresAt)
				: false;
		}

		/// <summary>
		/// Inserts an item into cache storage with absolute expiration
		/// </summary>
		/// <param name="key">The string that presents key of cache item</param>
		/// <param name="value">The object that presents value of cache item</param>
		/// <param name="expiresAt">The time that presents absolute expired time</param>
		/// <param name="mode">Inidicates the mode how the items are stored in Memcached (default is Set, means override if the item is existed)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns>true if set successful</returns>
		public static Task<bool> SetAsync(string key, object value, DateTime expiresAt, StoreMode mode = StoreMode.Set, CancellationToken cancellationToken = default(CancellationToken))
		{
			return CacheManager.ExecuteTask<bool>(() => DistributedCache.Set(key, value, expiresAt, mode), cancellationToken);
		}

		/// <summary>
		/// Inserts an item into cache storage with absolute expiration
		/// </summary>
		/// <param name="key">The string that presents key of cache item</param>
		/// <param name="value">The object that presents value of cache item</param>
		/// <param name="expiresAt">The date-time number that presents absolute expired time (in mili-seconds)</param>
		/// <param name="mode">Inidicates the mode how the items are stored in Memcached (default is Set, means override if the item is existed)</param>
		/// <returns>true if set successful</returns>
		public static bool Set(string key, object value, long expiresAt, StoreMode mode = StoreMode.Set)
		{
			return DistributedCache.Set(key, value, DateTime.Now.AddMilliseconds(expiresAt), mode);
		}

		/// <summary>
		/// Inserts an item into cache storage with absolute expiration
		/// </summary>
		/// <param name="key">The string that presents key of cache item</param>
		/// <param name="value">The object that presents value of cache item</param>
		/// <param name="expiresAt">The number that presents absolute expired time (in mili-seconds)</param>
		/// <param name="mode">Inidicates the mode how the items are stored in Memcached (default is Set, means override if the item is existed)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns>true if set successful</returns>
		public static Task<bool> SetAsync(string key, object value, long expiresAt, StoreMode mode = StoreMode.Set, CancellationToken cancellationToken = default(CancellationToken))
		{
			return CacheManager.ExecuteTask<bool>(() => DistributedCache.Set(key, value, expiresAt, mode), cancellationToken);
		}

		/// <summary>
		/// Inserts an item into cache storage with sliding expiration
		/// </summary>
		/// <param name="key">The string that presents key of cache item</param>
		/// <param name="value">The object that presents value of cache item</param>
		/// <param name="validFor">The TimeSpan that presents sliding expiration time</param>
		/// <param name="mode">Inidicates the mode how the items are stored in Memcached (default is Set, means override if the item is existed)</param>
		/// <returns>true if set successful</returns>
		public static bool Set(string key, object value, TimeSpan validFor, StoreMode mode = StoreMode.Set)
		{
			return !string.IsNullOrWhiteSpace(key)
				? DistributedCache.Client.Store(mode, key, value, validFor)
				: false;
		}

		/// <summary>
		/// Inserts an item into cache storage with sliding expiration
		/// </summary>
		/// <param name="key">The string that presents key of cache item</param>
		/// <param name="value">The object that presents value of cache item</param>
		/// <param name="validFor">The TimeSpan that presents sliding expiration time</param>
		/// <param name="mode">Inidicates the mode how the items are stored in Memcached (default is Set, means override if the item is existed)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns>true if set successful</returns>
		public static Task<bool> SetAsync(string key, object value, TimeSpan validFor, StoreMode mode = StoreMode.Set, CancellationToken cancellationToken = default(CancellationToken))
		{
			return CacheManager.ExecuteTask<bool>(() => DistributedCache.Set(key, value, validFor, mode), cancellationToken);
		}

		/// <summary>
		/// Inserts an item into cache storage with sliding expiration
		/// </summary>
		/// <param name="key">The string that presents key of cache item</param>
		/// <param name="value">The object that presents value of cache item</param>
		/// <param name="expiresAt">The number that presents absolute expired time (in minutes)</param>
		/// <param name="mode">Inidicates the mode how the items are stored in Memcached (default is Set, means override if the item is existed)</param>
		/// <returns>true if set successful</returns>
		public static bool Set(string key, object value, int expiresAt, StoreMode mode = StoreMode.Set)
		{
			return DistributedCache.Set(key, value, TimeSpan.FromMinutes(expiresAt), mode);
		}

		/// <summary>
		/// Inserts an item into cache storage with sliding expiration
		/// </summary>
		/// <param name="key">The string that presents key of cache item</param>
		/// <param name="value">The object that presents value of cache item</param>
		/// <param name="expiresAt">The number that presents absolute expired time (in minutes)</param>
		/// <param name="mode">Inidicates the mode how the items are stored in Memcached (default is Set, means override if the item is existed)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns>true if set successful</returns>
		public static Task<bool> SetAsync(string key, object value, int expiresAt, StoreMode mode = StoreMode.Set, CancellationToken cancellationToken = default(CancellationToken))
		{
			return CacheManager.ExecuteTask<bool>(() => DistributedCache.Set(key, value, expiresAt, mode), cancellationToken);
		}

		/// <summary>
		/// Inserts an item into cache storage with sliding expiration
		/// </summary>
		/// <param name="key">The string that presents key of cache item</param>
		/// <param name="value">The object that presents value of cache item</param>
		/// <param name="mode">Inidicates the mode how the items are stored in Memcached (default is Set, means override if the item is existed)</param>
		/// <returns>true if set successful</returns>
		public static bool Set(string key, object value, StoreMode mode = StoreMode.Set)
		{
			return DistributedCache.Set(key, value, TimeSpan.FromMilliseconds(DistributedCache.ExpirationTime), mode);
		}

		/// <summary>
		/// Inserts an item into cache storage with sliding expiration
		/// </summary>
		/// <param name="key">The string that presents key of cache item</param>
		/// <param name="value">The object that presents value of cache item</param>
		/// <param name="mode">Inidicates the mode how the items are stored in Memcached (default is Set, means override if the item is existed)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns>true if set successful</returns>
		public static Task<bool> SetAsync(string key, object value, StoreMode mode = StoreMode.Set, CancellationToken cancellationToken = default(CancellationToken))
		{
			return CacheManager.ExecuteTask<bool>(() => DistributedCache.Set(key, value, mode), cancellationToken);
		}
		#endregion

		#region Get
		/// <summary>
		/// Gets the specified item from cache storage
		/// </summary>
		/// <param name="key">The string that presents key of cache item</param>
		/// <returns></returns>
		public static object Get(string key)
		{
			if (string.IsNullOrWhiteSpace(key))
				throw new ArgumentNullException("key");
			return DistributedCache.Client.Get(key);
		}

		/// <summary>
		/// Gets the specified item from cache storage
		/// </summary>
		/// <param name="key">The string that presents key of cache item</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static Task<object> GetAsync(string key, CancellationToken cancellationToken = default(CancellationToken))
		{
			return CacheManager.ExecuteTask<object>(() => DistributedCache.Get(key), cancellationToken);
		}

		/// <summary>
		/// Gets the specified item from cache storage
		/// </summary>
		/// <typeparam name="T">Type for casting</typeparam>
		/// <param name="key">The string that presents key of cache item</param>
		/// <returns></returns>
		public static T Get<T>(string key)
		{
			if (string.IsNullOrWhiteSpace(key))
				throw new ArgumentNullException("key");
			return DistributedCache.Client.Get<T>(key);
		}

		/// <summary>
		/// Gets the specified item from cache storage
		/// </summary>
		/// <typeparam name="T">Type for casting</typeparam>
		/// <param name="key">The string that presents key of cache item</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static Task<T> GetAsync<T>(string key, CancellationToken cancellationToken = default(CancellationToken))
		{
			return CacheManager.ExecuteTask<T>(() => DistributedCache.Get<T>(key), cancellationToken);
		}

		/// <summary>
		/// Gets the collection of items from cache storage
		/// </summary>
		/// <param name="keys">The collection of items' keys</param>
		/// <returns></returns>
		public static IDictionary<string, object> Get(IEnumerable<string> keys)
		{
			if (keys == null)
				throw new ArgumentNullException("keys");
			return DistributedCache.Client.Get(keys);
		}

		/// <summary>
		/// Gets the collection of items from cache storage
		/// </summary>
		/// <param name="keys">The collection of items' keys</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static Task<IDictionary<string, object>> GetAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default(CancellationToken))
		{
			return CacheManager.ExecuteTask<IDictionary<string, object>>(() => DistributedCache.Get(keys), cancellationToken);
		}
		#endregion

		#region Remove
		/// <summary>
		/// Removes an item from cache storage
		/// </summary>
		/// <param name="key">The string that presents key of cache item that be removed</param>
		/// <returns>true if item is found and removed from cache storage</returns>
		public static bool Remove(string key)
		{
			if (string.IsNullOrWhiteSpace(key))
				throw new ArgumentNullException("key");
			return DistributedCache.Client.Remove(key);
		}

		/// <summary>
		/// Removes an item from cache storage
		/// </summary>
		/// <param name="key">The string that presents key of cache item that be removed</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns>true if item is found and removed from cache storage</returns>
		public static Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default(CancellationToken))
		{
			return CacheManager.ExecuteTask<bool>(() => DistributedCache.Remove(key), cancellationToken);
		}

		/// <summary>
		/// Removes the collection of items from cache storage
		/// </summary>
		/// <param name="keys">The collection of string that presents key of cache items that be removed</param>
		public static void Remove(IEnumerable<string> keys)
		{
			if (keys == null)
				throw new ArgumentNullException("keys");

			foreach (var key in keys)
				if (!string.IsNullOrWhiteSpace(key))
					DistributedCache.Client.Remove(key);
		}

		/// <summary>
		/// Removes the collection of items from cache storage
		/// </summary>
		/// <param name="keys">The collection of string that presents key of cache items that be removed</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static Task RemoveAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default(CancellationToken))
		{
			return CacheManager.ExecuteTask(() => DistributedCache.Remove(keys), cancellationToken);
		}

		/// <summary>
		/// Removes all items from cache storage
		/// </summary>
		public static void RemoveAll()
		{
			DistributedCache.Client.FlushAll();
		}

		/// <summary>
		/// Removes all items from cache storage
		/// </summary>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static Task RemoveAllAsync(CancellationToken cancellationToken = default(CancellationToken))
		{
			return CacheManager.ExecuteTask(() => DistributedCache.RemoveAll(), cancellationToken);
		}
		#endregion

		#region Exists
		/// <summary>
		/// Determines whether a key is exists or not
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to check</param>
		/// <returns>Returns a boolean value indicating if the object that associates with the key is cached or not</returns>
		public static bool Exists(string key)
		{
			if (!DistributedCache.Client.Append(key, new ArraySegment<byte>(new byte[0])))
			{
				DistributedCache.Client.Remove(key);
				return false;
			}
			return true;
		}

		/// <summary>
		/// Determines whether a key is exists or not
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to check</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns>Returns a boolean value indicating if the object that associates with the key is cached or not</returns>
		public static Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default(CancellationToken))
		{
			return CacheManager.ExecuteTask<bool>(() => DistributedCache.Exists(key), cancellationToken);
		}
		#endregion

	}
}