#region Related components
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using StackExchange.Redis;
#endregion

namespace net.vieapps.Components.Caching
{
	internal static class RedisExtensions
	{
		internal static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1);

		internal static TimeSpan ToTimeSpan(this DateTime value)
		{
			return value - RedisExtensions.UnixEpoch;
		}

		internal static bool Set(this IDatabase redis, string key, object value)
		{
			return redis.StringSet(key, Helper.Serialize(value));
		}

		internal static bool Set(this IDatabase redis, string key, object value, TimeSpan validFor)
		{
			return redis.StringSet(key, Helper.Serialize(value), validFor);
		}

		internal static bool Set(this IDatabase redis, string key, object value, DateTime expiresAt)
		{
			return redis.StringSet(key, Helper.Serialize(value), expiresAt.ToTimeSpan());
		}

		internal static Task<bool> SetAsync(this IDatabase redis, string key, object value)
		{
			return redis.StringSetAsync(key, Helper.Serialize(value));
		}

		internal static Task<bool> SetAsync(this IDatabase redis, string key, object value, TimeSpan validFor)
		{
			return redis.StringSetAsync(key, Helper.Serialize(value), validFor);
		}

		internal static Task<bool> SetAsync(this IDatabase redis, string key, object value, DateTime expiresAt)
		{
			return redis.StringSetAsync(key, Helper.Serialize(value), expiresAt.ToTimeSpan());
		}

		internal static bool Add(this IDatabase redis, string key, object value)
		{
			return redis.Exists(key)
				? false
				: redis.Set(key, value);
		}

		internal static bool Add(this IDatabase redis, string key, object value, TimeSpan validFor)
		{
			return redis.Exists(key)
				? false
				: redis.Set(key, value, validFor);
		}

		internal static bool Add(this IDatabase redis, string key, object value, DateTime expiresAt)
		{
			return redis.Exists(key)
				? false
				: redis.Set(key, value, expiresAt);
		}

		internal static async Task<bool> AddAsync(this IDatabase redis, string key, object value)
		{
			return await redis.ExistsAsync(key)
				? false
				: await redis.SetAsync(key, value);
		}

		internal static async Task<bool> AddAsync(this IDatabase redis, string key, object value, TimeSpan validFor)
		{
			return await redis.ExistsAsync(key)
				? false
				: await redis.SetAsync(key, value, validFor);
		}

		internal static async Task<bool> AddAsync(this IDatabase redis, string key, object value, DateTime expiresAt)
		{
			return await redis.ExistsAsync(key)
				? false
				: await redis.SetAsync(key, value, expiresAt);
		}

		internal static bool Replace(this IDatabase redis, string key, object value)
		{
			return redis.Exists(key)
				? redis.Set(key, value)
				: false;
		}

		internal static bool Replace(this IDatabase redis, string key, object value, TimeSpan validFor)
		{
			return redis.Exists(key)
				? redis.Set(key, value, validFor)
				: false;
		}

		internal static bool Replace(this IDatabase redis, string key, object value, DateTime expiresAt)
		{
			return redis.Exists(key)
				? redis.Set(key, value, expiresAt)
				: false;
		}

		internal static async Task<bool> ReplaceAsync(this IDatabase redis, string key, object value)
		{
			return await redis.ExistsAsync(key)
				? await redis.SetAsync(key, value)
				: false;
		}

		internal static async Task<bool> ReplaceAsync(this IDatabase redis, string key, object value, TimeSpan validFor)
		{
			return await redis.ExistsAsync(key)
				? await redis.SetAsync(key, value, validFor)
				: false;
		}

		internal static async Task<bool> ReplaceAsync(this IDatabase redis, string key, object value, DateTime expiresAt)
		{
			return await redis.ExistsAsync(key)
				? await redis.SetAsync(key, value, expiresAt)
				: false;
		}

		internal static object Get(this IDatabase redis, string key)
		{
			var value = (byte[])redis.StringGet(key);
			return value != null
				? Helper.Deserialize(value)
				: null;
		}

		internal static T Get<T>(this IDatabase redis, string key)
		{
			var value = redis.Get(key);
			return value != null && value is T
				? (T)value
				: default(T);
		}

		internal static async Task<object> GetAsync(this IDatabase redis, string key)
		{
			var value = (byte[])await redis.StringGetAsync(key);
			return value != null
				? Helper.Deserialize(value)
				: null;
		}

		internal static async Task<T> GetAsync<T>(this IDatabase redis, string key)
		{
			var value = await redis.GetAsync(key);
			return value != null && value is T
				? (T)value
				: default(T);
		}

		internal static IDictionary<string, object> Get(this IDatabase redis, IEnumerable<string> keys)
		{
			var objects = new Dictionary<string, object>();
			if (keys != null)
				foreach (var key in keys)
					objects[key] = redis.Get(key);
			return objects;
		}

		internal static async Task<IDictionary<string, object>> GetAsync(this IDatabase redis, IEnumerable<string> keys)
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

		internal static bool Exists(this IDatabase redis, string key)
		{
			return redis.KeyExists(key);
		}

		internal static Task<bool> ExistsAsync(this IDatabase redis, string key)
		{
			return redis.KeyExistsAsync(key);
		}

		internal static bool Remove(this IDatabase redis, string key)
		{
			return redis.KeyDelete(key);
		}

		internal static Task<bool> RemoveAsync(this IDatabase redis, string key)
		{
			return redis.KeyDeleteAsync(key);
		}

	}
}