#region Related components
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;

using StackExchange.Redis;
#endregion

namespace net.vieapps.Components.Caching
{
	public static class RedisExtensions
	{
		public static bool Set(this IDatabase redis, string key, object value)
		{
			return string.IsNullOrWhiteSpace(key)
				? false
				: redis.StringSet(key, Helper.Serialize(value));
		}

		public static bool Set(this IDatabase redis, string key, object value, TimeSpan validFor)
		{
			return string.IsNullOrWhiteSpace(key)
				? false
				: redis.StringSet(key, Helper.Serialize(value), validFor);
		}

		public static bool Set(this IDatabase redis, string key, object value, DateTime expiresAt)
		{
			return string.IsNullOrWhiteSpace(key)
				? false
				: redis.StringSet(key, Helper.Serialize(value), expiresAt.ToTimeSpan());
		}

		public static Task<bool> SetAsync(this IDatabase redis, string key, object value)
		{
			return string.IsNullOrWhiteSpace(key)
				? Task.FromResult(false)
				: redis.StringSetAsync(key, Helper.Serialize(value));
		}

		public static Task<bool> SetAsync(this IDatabase redis, string key, object value, TimeSpan validFor)
		{
			return string.IsNullOrWhiteSpace(key)
				? Task.FromResult(false)
				: redis.StringSetAsync(key, Helper.Serialize(value), validFor);
		}

		public static Task<bool> SetAsync(this IDatabase redis, string key, object value, DateTime expiresAt)
		{
			return string.IsNullOrWhiteSpace(key)
				? Task.FromResult(false)
				: redis.StringSetAsync(key, Helper.Serialize(value), expiresAt.ToTimeSpan());
		}

		public static bool Add(this IDatabase redis, string key, object value)
		{
			return string.IsNullOrWhiteSpace(key) || redis.Exists(key)
				? false
				: redis.Set(key, value);
		}

		public static bool Add(this IDatabase redis, string key, object value, TimeSpan validFor)
		{
			return string.IsNullOrWhiteSpace(key) || redis.Exists(key)
				? false
				: redis.Set(key, value, validFor);
		}

		public static bool Add(this IDatabase redis, string key, object value, DateTime expiresAt)
		{
			return string.IsNullOrWhiteSpace(key) || redis.Exists(key)
				? false
				: redis.Set(key, value, expiresAt);
		}

		public static async Task<bool> AddAsync(this IDatabase redis, string key, object value)
		{
			return string.IsNullOrWhiteSpace(key) || await redis.ExistsAsync(key)
				? false
				: await redis.SetAsync(key, value);
		}

		public static async Task<bool> AddAsync(this IDatabase redis, string key, object value, TimeSpan validFor)
		{
			return string.IsNullOrWhiteSpace(key) || await redis.ExistsAsync(key)
				? false
				: await redis.SetAsync(key, value, validFor);
		}

		public static async Task<bool> AddAsync(this IDatabase redis, string key, object value, DateTime expiresAt)
		{
			return string.IsNullOrWhiteSpace(key) || await redis.ExistsAsync(key)
				? false
				: await redis.SetAsync(key, value, expiresAt);
		}

		public static bool Replace(this IDatabase redis, string key, object value)
		{
			return string.IsNullOrWhiteSpace(key) || !redis.Exists(key)
				? false
				: redis.Set(key, value);
		}

		public static bool Replace(this IDatabase redis, string key, object value, TimeSpan validFor)
		{
			return string.IsNullOrWhiteSpace(key) || !redis.Exists(key)
				? false
				: redis.Set(key, value, validFor);
		}

		public static bool Replace(this IDatabase redis, string key, object value, DateTime expiresAt)
		{
			return string.IsNullOrWhiteSpace(key) || !redis.Exists(key)
				? false
				: redis.Set(key, value, expiresAt);
		}

		public static async Task<bool> ReplaceAsync(this IDatabase redis, string key, object value)
		{
			return string.IsNullOrWhiteSpace(key) || !await redis.ExistsAsync(key)
				? false
				: await redis.SetAsync(key, value);
		}

		public static async Task<bool> ReplaceAsync(this IDatabase redis, string key, object value, TimeSpan validFor)
		{
			return string.IsNullOrWhiteSpace(key) || !await redis.ExistsAsync(key)
				? false
				: await redis.SetAsync(key, value, validFor);
		}

		public static async Task<bool> ReplaceAsync(this IDatabase redis, string key, object value, DateTime expiresAt)
		{
			return string.IsNullOrWhiteSpace(key) || !await redis.ExistsAsync(key)
				? false
				: await redis.SetAsync(key, value, expiresAt);
		}

		public static object Get(this IDatabase redis, string key)
		{
			if (string.IsNullOrWhiteSpace(key))
				return null;
			var value = (byte[])redis.StringGet(key);
			return value != null
				? Helper.Deserialize(value)
				: null;
		}

		public static T Get<T>(this IDatabase redis, string key)
		{
			var value = redis.Get(key);
			return value != null && value is T
				? (T)value
				: default(T);
		}

		public static async Task<object> GetAsync(this IDatabase redis, string key)
		{
			if (string.IsNullOrWhiteSpace(key))
				return null;
			var value = (byte[])await redis.StringGetAsync(key);
			return value != null
				? Helper.Deserialize(value)
				: null;
		}

		public static async Task<T> GetAsync<T>(this IDatabase redis, string key)
		{
			var value = await redis.GetAsync(key);
			return value != null && value is T
				? (T)value
				: default(T);
		}

		public static IDictionary<string, object> Get(this IDatabase redis, IEnumerable<string> keys)
		{
			var objects = new Dictionary<string, object>();
			if (keys != null)
				keys.Where(key => !string.IsNullOrWhiteSpace(key)).ToList().ForEach(key => objects[key] = redis.Get(key));
			return objects;
		}

		public static async Task<IDictionary<string, object>> GetAsync(this IDatabase redis, IEnumerable<string> keys)
		{
			var objects = new Dictionary<string, object>();
			if (keys != null)
				await Task.WhenAll(keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(async (key) => objects[key] = await redis.GetAsync(key)));
			return objects;
		}

		public static bool Exists(this IDatabase redis, string key)
		{
			return string.IsNullOrWhiteSpace(key)
				? false
				: redis.KeyExists(key);
		}

		public static Task<bool> ExistsAsync(this IDatabase redis, string key)
		{
			return string.IsNullOrWhiteSpace(key)
				? Task.FromResult(false)
				: redis.KeyExistsAsync(key);
		}

		public static bool Remove(this IDatabase redis, string key)
		{
			return string.IsNullOrWhiteSpace(key)
				? false
				: redis.KeyDelete(key);
		}

		public static Task<bool> RemoveAsync(this IDatabase redis, string key)
		{
			return string.IsNullOrWhiteSpace(key)
				? Task.FromResult(false)
				: redis.KeyDeleteAsync(key);
		}

		public static bool UpdateSetMembers(this IDatabase redis, RedisKey setKey, string itemKey)
		{
			try
			{
				return redis.SetAdd(setKey, itemKey);
			}
			catch (RedisServerException ex)
			{
				if (ex.Message.Contains("WRONGTYPE"))
				{
					redis.KeyDelete(setKey);
					return redis.SetAdd(setKey, itemKey);
				}
				else
					throw;
			}
			catch (Exception)
			{
				throw;
			}
		}

		public static async Task<bool> UpdateSetMembersAsync(this IDatabase redis, string setKey, string itemKey)
		{
			try
			{
				return await redis.SetAddAsync(setKey, itemKey);
			}
			catch (RedisServerException ex)
			{
				if (ex.Message.Contains("WRONGTYPE"))
				{
					await redis.KeyDeleteAsync(setKey);
					return await redis.SetAddAsync(setKey, itemKey);
				}
				else
					throw;
			}
			catch (Exception)
			{
				throw;
			}
		}

		public static bool RemoveSetMembers(this IDatabase redis, string setKey, string itemKey)
		{
			try
			{
				return redis.SetRemove(setKey, itemKey);
			}
			catch
			{
				return false;
			}
		}

		public static async Task<bool> RemoveSetMembersAsync(this IDatabase redis, string setKey, string itemKey)
		{
			try
			{
				return await redis.SetRemoveAsync(setKey, itemKey);
			}
			catch
			{
				return false;
			}
		}

		public static HashSet<string> GetSetMembers(this IDatabase redis, string setKey)
		{
			try
			{
				var keys = redis.SetMembers(setKey);
				return new HashSet<string>(keys != null ? keys.Where(key => !key.IsNull).Select(key => key.ToString()) : new string[] { });
			}
			catch
			{
				return new HashSet<string>();
			}
		}

		public static async Task<HashSet<string>> GetSetMembersAsync(this IDatabase redis, string setKey)
		{
			try
			{
				var keys = await redis.SetMembersAsync(setKey);
				return new HashSet<string>(keys != null ? keys.Where(key => !key.IsNull).Select(key => key.ToString()) : new string[] { });
			}
			catch
			{
				return new HashSet<string>();
			}
		}

	}
}