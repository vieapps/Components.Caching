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
		internal static bool Set(this IDatabase redis, string key, byte[] value, TimeSpan validFor)
		{
			return string.IsNullOrWhiteSpace(key)
				? false
				: redis.StringSet(key, value, validFor);
		}

		public static bool Set(this IDatabase redis, string key, object value, TimeSpan validFor)
		{
			return redis.Set(key, Helper.Serialize(value), validFor);
		}

		public static bool Set(this IDatabase redis, string key, object value, DateTime expiresAt)
		{
			return redis.Set(key, value, expiresAt.ToTimeSpan());
		}

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

		public static Task<bool> SetAsync(this IDatabase redis, string key, object value, TimeSpan validFor)
		{
			return redis.SetAsync(key, Helper.Serialize(value), validFor);
		}

		public static Task<bool> SetAsync(this IDatabase redis, string key, object value, DateTime expiresAt)
		{
			return redis.SetAsync(key, value, expiresAt.ToTimeSpan());
		}

		public static Task<bool> SetAsync(this IDatabase redis, string key, object value, int expirationTime = 0)
		{
			return redis.SetAsync(key, value, expirationTime > 0 ? TimeSpan.FromMinutes(expirationTime) : TimeSpan.Zero);
		}

		public static void Set<T>(this IDatabase redis, IDictionary<string, T> values, TimeSpan validFor)
		{
			if (values != null)
				values.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key)).ToList().ForEach(kvp => redis.StringSet(kvp.Key, Helper.Serialize(kvp.Value), validFor));
		}

		public static Task SetAsync<T>(this IDatabase redis, IDictionary<string, T> values, TimeSpan validFor)
		{
			var tasks = values != null
				? values.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key)).Select(async (kvp) => await redis.StringSetAsync(kvp.Key, Helper.Serialize(kvp.Value), validFor))
				: new List<Task<bool>>();
			return Task.WhenAll(tasks);
		}

		internal static void Set(this IDatabase redis, IDictionary<string, byte[]> values, TimeSpan validFor)
		{
			if (values != null)
				values.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key)).ToList().ForEach(kvp => redis.StringSet(kvp.Key, kvp.Value, validFor));
		}

		internal static Task SetAsync(this IDatabase redis, IDictionary<string, byte[]> values, TimeSpan validFor)
		{
			var tasks = values != null
				? values.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key)).Select(async (kvp) => await redis.StringSetAsync(kvp.Key, kvp.Value, validFor))
				: new List<Task<bool>>();
			return Task.WhenAll(tasks);
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

		internal static object Get(this IDatabase redis, string key, bool doDeserialize)
		{
			var value = !string.IsNullOrWhiteSpace(key)
				? (byte[])redis.StringGet(key)
				: null;
			return value != null && doDeserialize
				? Helper.Deserialize(value)
				: value;
		}

		public static object Get(this IDatabase redis, string key)
		{
			return redis.Get(key, true);
		}

		internal static async Task<object> GetAsync(this IDatabase redis, string key, bool doDeserialize)
		{
			var value = !string.IsNullOrWhiteSpace(key)
				? (byte[])await redis.StringGetAsync(key)
				: null;
			return value != null && doDeserialize
				? Helper.Deserialize(value)
				: value;
		}

		public static Task<object> GetAsync(this IDatabase redis, string key)
		{
			return redis.GetAsync(key, true);
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
			if (keys != null)
				keys.Where(key => !string.IsNullOrWhiteSpace(key)).ToList().ForEach(key => objects[key] = redis.Get(key));
			return objects;
		}

		public static async Task<IDictionary<string, object>> GetAsync(this IDatabase redis, IEnumerable<string> keys)
		{
			var objects = new Dictionary<string, object>();
			await Task.WhenAll(keys != null
				? keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(async (key) => objects[key] = await redis.GetAsync(key))
				: new List<Task<object>>()
			);
			return objects;
		}

		public static IDictionary<string, T> Get<T>(this IDatabase redis, IEnumerable<string> keys)
		{
			var objects = new Dictionary<string, T>();
			if (keys != null)
				keys.Where(key => !string.IsNullOrWhiteSpace(key)).ToList().ForEach(key => objects[key] = redis.Get<T>(key));
			return objects;
		}

		public static async Task<IDictionary<string, T>> GetAsync<T>(this IDatabase redis, IEnumerable<string> keys)
		{
			var objects = new Dictionary<string, T>();
			await Task.WhenAll(keys != null
				? keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(async (key) => objects[key] = await redis.GetAsync<T>(key))
				: new List<Task<T>>()
			);
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

		internal static bool UpdateSetMembers(this IDatabase redis, string key, string[] values)
		{
			if (!string.IsNullOrWhiteSpace(key) && values != null && values.Length > 0)
				try
				{
					return redis.SetAdd(key, values.Select(v => (RedisValue)v).ToArray()) > 0;
				}
				catch (RedisServerException ex)
				{
					if (ex.Message.Contains("WRONGTYPE"))
					{
						redis.KeyDelete(key);
						return redis.SetAdd(key, values.Select(v => (RedisValue)v).ToArray()) > 0;
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
					return (await redis.SetAddAsync(key, values.Select(v => (RedisValue)v).ToArray())) > 0;
				}
				catch (RedisServerException ex)
				{
					if (ex.Message.Contains("WRONGTYPE"))
					{
						await redis.KeyDeleteAsync(key);
						return (await redis.SetAddAsync(key, values.Select(v => (RedisValue)v).ToArray())) > 0;
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