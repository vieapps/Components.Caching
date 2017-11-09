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
		public static bool Set(this IDatabase redis, string key, object value, TimeSpan validFor)
		{
			return string.IsNullOrWhiteSpace(key)
				? false
				: redis.StringSet(key, Helper.Serialize(value), validFor);
		}

		public static bool Set(this IDatabase redis, string key, object value, DateTime expiresAt)
		{
			return redis.Set(key, value, expiresAt.ToTimeSpan());
		}

		public static bool Set(this IDatabase redis, string key, object value, int expirationTime = 0)
		{
			return redis.Set(key, value, expirationTime > 0 ? TimeSpan.FromMinutes(expirationTime) : TimeSpan.Zero);
		}

		public static Task<bool> SetAsync(this IDatabase redis, string key, object value, TimeSpan validFor)
		{
			return string.IsNullOrWhiteSpace(key)
				? Task.FromResult(false)
				: redis.StringSetAsync(key, Helper.Serialize(value), validFor);
		}

		public static Task<bool> SetAsync(this IDatabase redis, string key, object value, DateTime expiresAt)
		{
			return redis.SetAsync(key, value, expiresAt.ToTimeSpan());
		}

		public static Task<bool> SetAsync(this IDatabase redis, string key, object value, int expirationTime = 0)
		{
			return redis.SetAsync(key, value, expirationTime > 0 ? TimeSpan.FromMinutes(expirationTime) : TimeSpan.Zero);
		}

		public static bool Add(this IDatabase redis, string key, object value, TimeSpan validFor)
		{
			return string.IsNullOrWhiteSpace(key) || redis.Exists(key)
				? false
				: redis.Set(key, value, validFor);
		}

		public static bool Add(this IDatabase redis, string key, object value, DateTime expiresAt)
		{
			return redis.Add(key, value, expiresAt.ToTimeSpan());
		}
		public static bool Add(this IDatabase redis, string key, object value, int expirationTime = 0)
		{
			return redis.Add(key, value, expirationTime > 0 ? TimeSpan.FromMinutes(expirationTime) : TimeSpan.Zero);
		}

		public static async Task<bool> AddAsync(this IDatabase redis, string key, object value, TimeSpan validFor)
		{
			return string.IsNullOrWhiteSpace(key) || await redis.ExistsAsync(key)
				? false
				: await redis.SetAsync(key, value, validFor);
		}

		public static Task<bool> AddAsync(this IDatabase redis, string key, object value, DateTime expiresAt)
		{
			return redis.AddAsync(key, value, expiresAt.ToTimeSpan());
		}

		public static Task<bool> AddAsync(this IDatabase redis, string key, object value, int expirationTime = 0)
		{
			return redis.AddAsync(key, value, expirationTime > 0 ? TimeSpan.FromMinutes(expirationTime) : TimeSpan.Zero);
		}

		public static bool Replace(this IDatabase redis, string key, object value, TimeSpan validFor)
		{
			return string.IsNullOrWhiteSpace(key) || !redis.Exists(key)
				? false
				: redis.Set(key, value, validFor);
		}

		public static bool Replace(this IDatabase redis, string key, object value, DateTime expiresAt)
		{
			return redis.Replace(key, value, expiresAt.ToTimeSpan());
		}

		public static bool Replace(this IDatabase redis, string key, object value, int expirationTime = 0)
		{
			return redis.Replace(key, value, expirationTime > 0 ? TimeSpan.FromMinutes(expirationTime) : TimeSpan.Zero);
		}

		public static async Task<bool> ReplaceAsync(this IDatabase redis, string key, object value, TimeSpan validFor)
		{
			return string.IsNullOrWhiteSpace(key) || !await redis.ExistsAsync(key)
				? false
				: await redis.SetAsync(key, value, validFor);
		}

		public static Task<bool> ReplaceAsync(this IDatabase redis, string key, object value, DateTime expiresAt)
		{
			return redis.ReplaceAsync(key, value, expiresAt.ToTimeSpan());
		}

		public static Task<bool> ReplaceAsync(this IDatabase redis, string key, object value, int expirationTime = 0)
		{
			return redis.ReplaceAsync(key, value, expirationTime > 0 ? TimeSpan.FromMinutes(expirationTime) : TimeSpan.Zero);
		}

		public static object Get(this IDatabase redis, string key)
		{
			var value = !string.IsNullOrWhiteSpace(key)
				? (byte[])redis.StringGet(key)
				: null;
			return value != null
				? Helper.Deserialize(value)
				: null;
		}

		public static async Task<object> GetAsync(this IDatabase redis, string key)
		{
			var value = !string.IsNullOrWhiteSpace(key)
				? (byte[])await redis.StringGetAsync(key)
				: null;
			return value != null
				? Helper.Deserialize(value)
				: null;
		}

		public static T Get<T>(this IDatabase redis, string key)
		{
			var value = !string.IsNullOrWhiteSpace(key)
				? (byte[])redis.StringGet(key)
				: null;
			return value != null
				? Helper.Deserialize<T>(value)
				: default(T);
		}

		public static async Task<T> GetAsync<T>(this IDatabase redis, string key)
		{
			var value = !string.IsNullOrWhiteSpace(key)
				? (byte[])await redis.StringGetAsync(key)
				: null;
			return value != null
				? Helper.Deserialize<T>(value)
				: default(T);
		}

		public static IDictionary<string, object> Get(this IDatabase redis, IEnumerable<string> keys)
		{
			var objects = new Dictionary<string, object>();
			var tasks = keys != null
				? keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(async (key) => objects[key] = await redis.GetAsync(key))
				: new List<Task<object>>();
			Task.WaitAll(tasks.ToArray(), 13000);
			return objects;
		}

		public static async Task<IDictionary<string, object>> GetAsync(this IDatabase redis, IEnumerable<string> keys)
		{
			var objects = new Dictionary<string, object>();
			var tasks = keys != null
				? keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(async (key) => objects[key] = await redis.GetAsync(key))
				: new List<Task<object>>();
			await Task.WhenAll(tasks);
			return objects;
		}

		public static IDictionary<string, T> Get<T>(this IDatabase redis, IEnumerable<string> keys)
		{
			var objects = new Dictionary<string, T>();
			var tasks = keys != null
				? keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(async (key) => objects[key] = await redis.GetAsync<T>(key))
				: new List<Task<T>>();
			Task.WaitAll(tasks.ToArray(), 13000);
			return objects;
		}

		public static async Task<IDictionary<string, T>> GetAsync<T>(this IDatabase redis, IEnumerable<string> keys)
		{
			var objects = new Dictionary<string, T>();
			var tasks = keys != null
				? keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(async (key) => objects[key] = await redis.GetAsync<T>(key))
				: new List<Task<T>>();
			await Task.WhenAll(tasks);
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

		internal static bool UpdateSetMembers(this IDatabase redis, string key, string value)
		{
			try
			{
				return redis.SetAdd(key, value);
			}
			catch (RedisServerException ex)
			{
				if (ex.Message.Contains("WRONGTYPE"))
				{
					redis.KeyDelete(key);
					return redis.SetAdd(key, value);
				}
				else
					throw;
			}
			catch (Exception)
			{
				throw;
			}
		}

		internal static async Task<bool> UpdateSetMembersAsync(this IDatabase redis, string key, string value)
		{
			try
			{
				return await redis.SetAddAsync(key, value);
			}
			catch (RedisServerException ex)
			{
				if (ex.Message.Contains("WRONGTYPE"))
				{
					await redis.KeyDeleteAsync(key);
					return await redis.SetAddAsync(key, value);
				}
				else
					throw;
			}
			catch (Exception)
			{
				throw;
			}
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
				return await redis.SetRemoveAsync(key, value);
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
				return new HashSet<string>(keys != null ? keys.Where(k => !k.IsNull).Select(k => k.ToString()) : new string[] { });
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
				var keys = await redis.SetMembersAsync(key);
				return new HashSet<string>(keys != null ? keys.Where(k => !k.IsNull).Select(k => k.ToString()) : new string[] { });
			}
			catch
			{
				return new HashSet<string>();
			}
		}
	}
}