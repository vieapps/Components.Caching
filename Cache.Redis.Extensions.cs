#region Related components
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;

using StackExchange.Redis;
using CacheUtils;
#endregion

namespace net.vieapps.Components.Caching
{
	public static class RedisExtensions
	{
		internal static bool Set(this IDatabase redis, string key, byte[] value, TimeSpan validFor)
		{
			return string.IsNullOrWhiteSpace(key)
				? false
				: redis.StringSet(key, value, validFor);
		}

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="redis"></param>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <param name="validFor"></param>
		/// <returns></returns>
		public static bool Set(this IDatabase redis, string key, object value, TimeSpan validFor)
		{
			return string.IsNullOrWhiteSpace(key)
				? false
				: redis.Set(key, Helper.Serialize(value), validFor);
		}

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="redis"></param>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <param name="expiresAt"></param>
		/// <returns></returns>
		public static bool Set(this IDatabase redis, string key, object value, DateTime expiresAt)
		{
			return redis.Set(key, value, expiresAt.ToTimeSpan());
		}

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="redis"></param>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <param name="expirationTime"></param>
		/// <returns></returns>
		public static bool Set(this IDatabase redis, string key, object value, int expirationTime = 0)
		{
			return redis.Set(key, value, expirationTime > 0 ? TimeSpan.FromMinutes(expirationTime) : TimeSpan.Zero);
		}

		internal static Task<bool> SetAsync(this IDatabase redis, string key, byte[] value, TimeSpan validFor)
		{
			return string.IsNullOrWhiteSpace(key)
				? Task.FromResult(false)
				: redis.StringSetAsync(key, value, validFor);
		}

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="redis"></param>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <param name="validFor"></param>
		/// <returns></returns>
		public static Task<bool> SetAsync(this IDatabase redis, string key, object value, TimeSpan validFor)
		{
			return string.IsNullOrWhiteSpace(key)
				? Task.FromResult(false)
				: redis.SetAsync(key, Helper.Serialize(value), validFor);
		}

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="redis"></param>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <param name="expiresAt"></param>
		/// <returns></returns>
		public static Task<bool> SetAsync(this IDatabase redis, string key, object value, DateTime expiresAt)
		{
			return redis.SetAsync(key, value, expiresAt.ToTimeSpan());
		}

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <param name="redis"></param>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <param name="expirationTime"></param>
		/// <returns></returns>
		public static Task<bool> SetAsync(this IDatabase redis, string key, object value, int expirationTime = 0)
		{
			return redis.SetAsync(key, value, expirationTime > 0 ? TimeSpan.FromMinutes(expirationTime) : TimeSpan.Zero);
		}

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="redis"></param>
		/// <param name="values"></param>
		/// <param name="validFor"></param>
		public static void Set<T>(this IDatabase redis, IDictionary<string, T> values, TimeSpan validFor)
		{
			values?.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key)).ToList().ForEach(kvp => redis.StringSet(kvp.Key, Helper.Serialize(kvp.Value), validFor));
		}

		/// <summary>
		/// Adds an item into cache with a specified key (if the key is already existed, then old cached item will be overriden)
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="redis"></param>
		/// <param name="values"></param>
		/// <param name="validFor"></param>
		/// <returns></returns>
		public static Task SetAsync<T>(this IDatabase redis, IDictionary<string, T> values, TimeSpan validFor)
		{
			return Task.WhenAll(values?.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key)).Select(kvp => redis.StringSetAsync(kvp.Key, Helper.Serialize(kvp.Value), validFor)) ?? new List<Task<bool>>());
		}

		internal static void Set(this IDatabase redis, IDictionary<string, byte[]> values, TimeSpan validFor)
		{
			values?.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key)).ToList().ForEach(kvp => redis.StringSet(kvp.Key, kvp.Value, validFor));
		}

		internal static Task SetAsync(this IDatabase redis, IDictionary<string, byte[]> values, TimeSpan validFor)
		{
			return Task.WhenAll(values?.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key)).Select(kvp => redis.StringSetAsync(kvp.Key, kvp.Value, validFor)) ?? new List<Task<bool>>());
		}

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="redis"></param>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <param name="validFor"></param>
		/// <returns></returns>
		public static bool Add(this IDatabase redis, string key, object value, TimeSpan validFor)
		{
			return string.IsNullOrWhiteSpace(key) || redis.Exists(key)
				? false
				: redis.Set(key, value, validFor);
		}

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="redis"></param>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <param name="expiresAt"></param>
		/// <returns></returns>
		public static bool Add(this IDatabase redis, string key, object value, DateTime expiresAt)
		{
			return redis.Add(key, value, expiresAt.ToTimeSpan());
		}

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="redis"></param>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <param name="expirationTime"></param>
		/// <returns></returns>
		public static bool Add(this IDatabase redis, string key, object value, int expirationTime = 0)
		{
			return redis.Add(key, value, expirationTime > 0 ? TimeSpan.FromMinutes(expirationTime) : TimeSpan.Zero);
		}

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="redis"></param>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <param name="validFor"></param>
		/// <returns></returns>
		public static async Task<bool> AddAsync(this IDatabase redis, string key, object value, TimeSpan validFor)
		{
			return string.IsNullOrWhiteSpace(key) || await redis.ExistsAsync(key).ConfigureAwait(false)
				? false
				: await redis.SetAsync(key, value, validFor).ConfigureAwait(false);
		}

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="redis"></param>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <param name="expiresAt"></param>
		/// <returns></returns>
		public static Task<bool> AddAsync(this IDatabase redis, string key, object value, DateTime expiresAt)
		{
			return redis.AddAsync(key, value, expiresAt.ToTimeSpan());
		}

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="redis"></param>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <param name="expirationTime"></param>
		/// <returns></returns>
		public static Task<bool> AddAsync(this IDatabase redis, string key, object value, int expirationTime = 0)
		{
			return redis.AddAsync(key, value, expirationTime > 0 ? TimeSpan.FromMinutes(expirationTime) : TimeSpan.Zero);
		}

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="redis"></param>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <param name="validFor"></param>
		/// <returns></returns>
		public static bool Replace(this IDatabase redis, string key, object value, TimeSpan validFor)
		{
			return string.IsNullOrWhiteSpace(key) || !redis.Exists(key)
				? false
				: redis.Set(key, value, validFor);
		}

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="redis"></param>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <param name="expiresAt"></param>
		/// <returns></returns>
		public static bool Replace(this IDatabase redis, string key, object value, DateTime expiresAt)
		{
			return redis.Replace(key, value, expiresAt.ToTimeSpan());
		}

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="redis"></param>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <param name="expirationTime"></param>
		/// <returns></returns>
		public static bool Replace(this IDatabase redis, string key, object value, int expirationTime = 0)
		{
			return redis.Replace(key, value, expirationTime > 0 ? TimeSpan.FromMinutes(expirationTime) : TimeSpan.Zero);
		}

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="redis"></param>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <param name="validFor"></param>
		/// <returns></returns>
		public static async Task<bool> ReplaceAsync(this IDatabase redis, string key, object value, TimeSpan validFor)
		{
			return string.IsNullOrWhiteSpace(key) || !await redis.ExistsAsync(key).ConfigureAwait(false)
				? false
				: await redis.SetAsync(key, value, validFor).ConfigureAwait(false);
		}

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="redis"></param>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <param name="expiresAt"></param>
		/// <returns></returns>
		public static Task<bool> ReplaceAsync(this IDatabase redis, string key, object value, DateTime expiresAt)
		{
			return redis.ReplaceAsync(key, value, expiresAt.ToTimeSpan());
		}

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="redis"></param>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <param name="expirationTime"></param>
		/// <returns></returns>
		public static Task<bool> ReplaceAsync(this IDatabase redis, string key, object value, int expirationTime = 0)
		{
			return redis.ReplaceAsync(key, value, expirationTime > 0 ? TimeSpan.FromMinutes(expirationTime) : TimeSpan.Zero);
		}

		internal static object Get(this IDatabase redis, string key, bool doDeserialize)
		{
			var value = !string.IsNullOrWhiteSpace(key)
				? (byte[])redis.StringGet(key)
				: null;
			return value != null && doDeserialize
				? Helper.Deserialize(value)
				: value;
		}

		/// <summary>
		/// Retreives a cached item
		/// </summary>
		/// <param name="redis"></param>
		/// <param name="key"></param>
		/// <returns></returns>
		public static object Get(this IDatabase redis, string key)
		{
			return redis.Get(key, true);
		}

		internal static async Task<object> GetAsync(this IDatabase redis, string key, bool doDeserialize)
		{
			var value = !string.IsNullOrWhiteSpace(key)
				? (byte[])await redis.StringGetAsync(key).ConfigureAwait(false)
				: null;
			return value != null && doDeserialize
				? Helper.Deserialize(value)
				: value;
		}

		/// <summary>
		/// Retreives a cached item
		/// </summary>
		/// <param name="redis"></param>
		/// <param name="key"></param>
		/// <returns></returns>
		public static Task<object> GetAsync(this IDatabase redis, string key)
		{
			return redis.GetAsync(key, true);
		}

		/// <summary>
		/// Retreives a cached item
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="redis"></param>
		/// <param name="key"></param>
		/// <returns></returns>
		public static T Get<T>(this IDatabase redis, string key)
		{
			var value = !string.IsNullOrWhiteSpace(key)
				? (byte[])redis.StringGet(key)
				: null;
			return value != null
				? Helper.Deserialize<T>(value)
				: default(T);
		}

		/// <summary>
		/// Retreives a cached item
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="redis"></param>
		/// <param name="key"></param>
		/// <returns></returns>
		public static async Task<T> GetAsync<T>(this IDatabase redis, string key)
		{
			var value = !string.IsNullOrWhiteSpace(key)
				? (byte[])await redis.StringGetAsync(key).ConfigureAwait(false)
				: null;
			return value != null
				? Helper.Deserialize<T>(value)
				: default(T);
		}

		internal static IDictionary<string, object> Get(this IDatabase redis, IEnumerable<string> keys, bool doDeserialize)
		{
			var objects = new Dictionary<string, object>();
			if (keys != null)
			{
				var redisKeys = keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => (RedisKey)key).ToArray();
				var redisValues = redis.StringGet(redisKeys);

				for (var index = 0; index < redisKeys.Length; index++)
					objects[redisKeys[index]] = redisValues[index].IsNull
						? null
						: doDeserialize
							? Helper.Deserialize((byte[])redisValues[index])
							: (byte[])redisValues[index];
			}
			return objects;
		}

		/// <summary>
		/// Retreives a collection of cached items
		/// </summary>
		/// <param name="redis"></param>
		/// <param name="keys"></param>
		/// <returns></returns>
		public static IDictionary<string, object> Get(this IDatabase redis, IEnumerable<string> keys)
		{
			return redis.Get(keys, true);
		}

		internal static async Task<IDictionary<string, object>> GetAsync(this IDatabase redis, IEnumerable<string> keys, bool doDeserialize)
		{
			var objects = new Dictionary<string, object>();
			if (keys != null)
			{
				var redisKeys = keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => (RedisKey)key).ToArray();
				var redisValues = await redis.StringGetAsync(redisKeys).ConfigureAwait(false);

				for (var index = 0; index < redisKeys.Length; index++)
					objects[redisKeys[index]] = redisValues[index].IsNull
						? null
						: doDeserialize
							? Helper.Deserialize((byte[])redisValues[index])
							: (byte[])redisValues[index];
			}
			return objects;
		}

		/// <summary>
		/// Retreives a collection of cached items
		/// </summary>
		/// <param name="redis"></param>
		/// <param name="keys"></param>
		/// <returns></returns>
		public static Task<IDictionary<string, object>> GetAsync(this IDatabase redis, IEnumerable<string> keys)
		{
			return redis.GetAsync(keys, true);
		}

		/// <summary>
		/// Retreives a collection of cached items
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="redis"></param>
		/// <param name="keys"></param>
		/// <returns></returns>
		public static IDictionary<string, T> Get<T>(this IDatabase redis, IEnumerable<string> keys)
		{
			var objects = new Dictionary<string, T>();
			if (keys != null)
			{
				var redisKeys = keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => (RedisKey)key).ToArray();
				var redisValues = redis.StringGet(redisKeys);

				for (var index = 0; index < redisKeys.Length; index++)
					objects[redisKeys[index]] = redisValues[index].IsNull
						? default(T)
						: Helper.Deserialize<T>((byte[])redisValues[index]);
			}
			return objects;
		}

		/// <summary>
		/// Retreives a collection of cached items
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="redis"></param>
		/// <param name="keys"></param>
		/// <returns></returns>
		public static async Task<IDictionary<string, T>> GetAsync<T>(this IDatabase redis, IEnumerable<string> keys)
		{
			var objects = new Dictionary<string, T>();
			if (keys != null)
			{
				var redisKeys = keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => (RedisKey)key).ToArray();
				var redisValues = await redis.StringGetAsync(redisKeys).ConfigureAwait(false);

				for (var index = 0; index < redisKeys.Length; index++)
					objects[redisKeys[index]] = redisValues[index].IsNull
						? default(T)
						: Helper.Deserialize<T>((byte[])redisValues[index]);
			}
			return objects;
		}

		/// <summary>
		/// Determines whether an item exists in the cache
		/// </summary>
		/// <param name="redis"></param>
		/// <param name="key"></param>
		/// <returns></returns>
		public static bool Exists(this IDatabase redis, string key)
		{
			return string.IsNullOrWhiteSpace(key)
				? false
				: redis.KeyExists(key);
		}

		/// <summary>
		/// Determines whether an item exists in the cache
		/// </summary>
		/// <param name="redis"></param>
		/// <param name="key"></param>
		/// <returns></returns>
		public static Task<bool> ExistsAsync(this IDatabase redis, string key)
		{
			return string.IsNullOrWhiteSpace(key)
				? Task.FromResult(false)
				: redis.KeyExistsAsync(key);
		}

		/// <summary>
		/// Removes a cached item
		/// </summary>
		/// <param name="redis"></param>
		/// <param name="key"></param>
		/// <returns></returns>
		public static bool Remove(this IDatabase redis, string key)
		{
			return string.IsNullOrWhiteSpace(key)
				? false
				: redis.KeyDelete(key);
		}

		/// <summary>
		/// Removes a cached item
		/// </summary>
		/// <param name="redis"></param>
		/// <param name="key"></param>
		/// <returns></returns>
		public static Task<bool> RemoveAsync(this IDatabase redis, string key)
		{
			return string.IsNullOrWhiteSpace(key)
				? Task.FromResult(false)
				: redis.KeyDeleteAsync(key);
		}

		internal static bool UpdateSetMembers(this IDatabase redis, string key, string[] values)
		{
			if (!string.IsNullOrWhiteSpace(key) && values != null && values.Length > 0)
				try
				{
					return redis.SetAdd(key, values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => (RedisValue)v).ToArray()) > 0;
				}
				catch (RedisServerException ex)
				{
					if (ex.Message.Contains("WRONGTYPE"))
					{
						redis.KeyDelete(key);
						return redis.SetAdd(key, values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => (RedisValue)v).ToArray()) > 0;
					}
					throw;
				}
				catch (Exception)
				{
					throw;
				}
			return false;
		}

		internal static bool UpdateSetMembers(this IDatabase redis, string key, string value)
		{
			return redis.UpdateSetMembers(key, new[] { value });
		}

		internal static async Task<bool> UpdateSetMembersAsync(this IDatabase redis, string key, string[] values)
		{
			if (!string.IsNullOrWhiteSpace(key) && values != null && values.Length > 0)
				try
				{
					return await redis.SetAddAsync(key, values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => (RedisValue)v).ToArray()).ConfigureAwait(false) > 0;
				}
				catch (RedisServerException ex)
				{
					if (ex.Message.Contains("WRONGTYPE"))
					{
						await redis.KeyDeleteAsync(key).ConfigureAwait(false);
						return await redis.SetAddAsync(key, values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => (RedisValue)v).ToArray()).ConfigureAwait(false) > 0;
					}
					throw;
				}
				catch (Exception)
				{
					throw;
				}
			return false;
		}

		internal static Task<bool> UpdateSetMembersAsync(this IDatabase redis, string key, string value)
		{
			return redis.UpdateSetMembersAsync(key, new[] { value });
		}

		internal static bool RemoveSetMembers(this IDatabase redis, string key, string value)
		{
			try
			{
				return redis.SetRemove(key, value);
			}
			catch
			{
				return false;
			}
		}

		internal static async Task<bool> RemoveSetMembersAsync(this IDatabase redis, string key, string value)
		{
			try
			{
				return await redis.SetRemoveAsync(key, value).ConfigureAwait(false);
			}
			catch
			{
				return false;
			}
		}

		internal static HashSet<string> GetSetMembers(this IDatabase redis, string key)
		{
			try
			{
				var keys = redis.SetMembers(key);
				return new HashSet<string>(keys?.Where(k => !k.IsNull).Select(k => k.ToString()) ?? new string[] { });
			}
			catch
			{
				return new HashSet<string>();
			}
		}

		internal static async Task<HashSet<string>> GetSetMembersAsync(this IDatabase redis, string key)
		{
			try
			{
				var keys = await redis.SetMembersAsync(key).ConfigureAwait(false);
				return new HashSet<string>(keys?.Where(k => !k.IsNull).Select(k => k.ToString()) ?? new string[] { });
			}
			catch
			{
				return new HashSet<string>();
			}
		}
	}
}