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
			return redis.StringSet(key, Helper.Serialize(value));
		}

		public static bool Set(this IDatabase redis, string key, object value, TimeSpan validFor)
		{
			return redis.StringSet(key, Helper.Serialize(value), validFor);
		}

		public static bool Set(this IDatabase redis, string key, object value, DateTime expiresAt)
		{
			return redis.StringSet(key, Helper.Serialize(value), expiresAt.ToTimeSpan());
		}

		public static Task<bool> SetAsync(this IDatabase redis, string key, object value)
		{
			return redis.StringSetAsync(key, Helper.Serialize(value));
		}

		public static Task<bool> SetAsync(this IDatabase redis, string key, object value, TimeSpan validFor)
		{
			return redis.StringSetAsync(key, Helper.Serialize(value), validFor);
		}

		public static Task<bool> SetAsync(this IDatabase redis, string key, object value, DateTime expiresAt)
		{
			return redis.StringSetAsync(key, Helper.Serialize(value), expiresAt.ToTimeSpan());
		}

		public static bool Add(this IDatabase redis, string key, object value)
		{
			return redis.Exists(key)
				? false
				: redis.Set(key, value);
		}

		public static bool Add(this IDatabase redis, string key, object value, TimeSpan validFor)
		{
			return redis.Exists(key)
				? false
				: redis.Set(key, value, validFor);
		}

		public static bool Add(this IDatabase redis, string key, object value, DateTime expiresAt)
		{
			return redis.Exists(key)
				? false
				: redis.Set(key, value, expiresAt);
		}

		public static async Task<bool> AddAsync(this IDatabase redis, string key, object value)
		{
			return await redis.ExistsAsync(key)
				? false
				: await redis.SetAsync(key, value);
		}

		public static async Task<bool> AddAsync(this IDatabase redis, string key, object value, TimeSpan validFor)
		{
			return await redis.ExistsAsync(key)
				? false
				: await redis.SetAsync(key, value, validFor);
		}

		public static async Task<bool> AddAsync(this IDatabase redis, string key, object value, DateTime expiresAt)
		{
			return await redis.ExistsAsync(key)
				? false
				: await redis.SetAsync(key, value, expiresAt);
		}

		public static bool Replace(this IDatabase redis, string key, object value)
		{
			return redis.Exists(key)
				? redis.Set(key, value)
				: false;
		}

		public static bool Replace(this IDatabase redis, string key, object value, TimeSpan validFor)
		{
			return redis.Exists(key)
				? redis.Set(key, value, validFor)
				: false;
		}

		public static bool Replace(this IDatabase redis, string key, object value, DateTime expiresAt)
		{
			return redis.Exists(key)
				? redis.Set(key, value, expiresAt)
				: false;
		}

		public static async Task<bool> ReplaceAsync(this IDatabase redis, string key, object value)
		{
			return await redis.ExistsAsync(key)
				? await redis.SetAsync(key, value)
				: false;
		}

		public static async Task<bool> ReplaceAsync(this IDatabase redis, string key, object value, TimeSpan validFor)
		{
			return await redis.ExistsAsync(key)
				? await redis.SetAsync(key, value, validFor)
				: false;
		}

		public static async Task<bool> ReplaceAsync(this IDatabase redis, string key, object value, DateTime expiresAt)
		{
			return await redis.ExistsAsync(key)
				? await redis.SetAsync(key, value, expiresAt)
				: false;
		}

		public static object Get(this IDatabase redis, string key)
		{
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
				foreach (var key in keys)
					objects[key] = redis.Get(key);
			return objects;
		}

		public static async Task<IDictionary<string, object>> GetAsync(this IDatabase redis, IEnumerable<string> keys)
		{
			var objects = new Dictionary<string, object>();
			if (keys != null)
			{
				Func<string, Task> func = async (key) =>
				{
					objects[key] = await redis.GetAsync(key);
				};
				var tasks = new List<Task>();
				foreach (var key in keys)
					tasks.Add(func(key));
				await Task.WhenAll(tasks);
			}
			return objects;
		}

		public static bool Exists(this IDatabase redis, string key)
		{
			return redis.KeyExists(key);
		}

		public static Task<bool> ExistsAsync(this IDatabase redis, string key)
		{
			return redis.KeyExistsAsync(key);
		}

		public static bool Remove(this IDatabase redis, string key)
		{
			return redis.KeyDelete(key);
		}

		public static Task<bool> RemoveAsync(this IDatabase redis, string key)
		{
			return redis.KeyDeleteAsync(key);
		}

		public static bool HashSetUpdate(this IDatabase redis, RedisKey setKey, string itemKey)
		{
			try
			{
				return redis.SetAdd(setKey, itemKey);
			}
			catch (RedisServerException ex)
			{
				if (ex.Message.Contains("WRONGTYPE Operation against a key holding the wrong kind of value"))
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

		public static async Task<bool> HashSetUpdateAsync(this IDatabase redis, string setKey, string itemKey)
		{
			try
			{
				return await redis.SetAddAsync(setKey, itemKey);
			}
			catch (RedisServerException ex)
			{
				if (ex.Message.Contains("WRONGTYPE Operation against a key holding the wrong kind of value"))
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

		public static bool HashSetRemove(this IDatabase redis, string setKey, string itemKey)
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

		public static async Task<bool> HashSetRemoveAsync(this IDatabase redis, string setKey, string itemKey)
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

		public static HashSet<string> HashSetGet(this IDatabase redis, string setKey)
		{
			try
			{
				var keys = redis.SetMembers(setKey);
				return keys != null && keys.Length > 0
					? new HashSet<string>(keys.Where(key => !key.IsNull).Select(key => key.ToString()))
					: new HashSet<string>();
			}
			catch
			{
				return new HashSet<string>();
			}
		}

		public static async Task<HashSet<string>> HashSetGetAsync(this IDatabase redis, string setKey)
		{
			try
			{
				var keys = await redis.SetMembersAsync(setKey);
				return keys != null && keys.Length > 0
					? new HashSet<string>(keys.Where(key => !key.IsNull).Select(key => key.ToString()))
					: new HashSet<string>();
			}
			catch
			{
				return new HashSet<string>();
			}
		}

	}
}