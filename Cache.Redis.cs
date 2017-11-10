#region Related components
using System;
using System.Linq;
using System.Xml;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Configuration;
using System.Diagnostics;
#endregion

namespace net.vieapps.Components.Caching
{
	/// <summary>
	/// Manipulates cached objects in isolated regions with redis
	/// </summary>
	[DebuggerDisplay("Redis: {Name} ({ExpirationTime} minutes)")]
	public sealed class Redis : ICacheProvider
	{
		/// <summary>
		/// Create new an instance of redis
		/// </summary>
		/// <param name="name">The string that presents name of isolated region of the cache</param>
		/// <param name="expirationTime">The number that presents times (in minutes) for caching an item</param>
		/// <param name="storeKeys">true to active store keys of the region (to clear or using with other purpose further)</param>
		public Redis(string name, int expirationTime, bool storeKeys)
		{
			// region name
			this._name = string.IsNullOrWhiteSpace(name)
				? Helper.RegionName
				: System.Text.RegularExpressions.Regex.Replace(name, "[^0-9a-zA-Z:-]+", "");

			// expiration time
			this._expirationTime = expirationTime > 0
				? expirationTime
				: Helper.ExpirationTime;

			// store keys
			this._storeKeys = storeKeys;

			// register the region
			Task.Run(async () => await Redis.RegisterRegionAsync(this.Name).ConfigureAwait(false)).ConfigureAwait(false);
		}

		#region Attributes
		static StackExchange.Redis.IDatabase _Client = null;

		/// <summary>
		/// Gets the instance of redis client
		/// </summary>
		public static StackExchange.Redis.IDatabase Client
		{
			get
			{
				return Redis._Client ?? (Redis._Client = Helper.GetRedisClient());
			}
		}

		string _name;
		int _expirationTime;
		bool _storeKeys;
		#endregion

		#region Keys
		void _UpdateKey(string key)
		{
			if (this._storeKeys)
				Redis.Client.UpdateSetMembers(this._RegionKey, this._GetKey(key));
		}

		Task _UpdateKeyAsync(string key)
		{
			return this._storeKeys
				? Redis.Client.UpdateSetMembersAsync(this._RegionKey, this._GetKey(key))
				: Task.CompletedTask;
		}

		void _UpdateKeys<T>(IDictionary<string, T> items, string keyPrefix = null)
		{
			if (this._storeKeys)
				Redis.Client.UpdateSetMembers(
					this._RegionKey,
					items != null
						? items.Where(kvp => kvp.Key != null).Select(kvp => this._GetKey((string.IsNullOrWhiteSpace(keyPrefix) ? "" : keyPrefix) + kvp.Key)).ToArray()
						: new string[] { }
				);
		}

		Task _UpdateKeysAsync<T>(IDictionary<string, T> items, string keyPrefix = null)
		{
			return this._storeKeys
				? Redis.Client.UpdateSetMembersAsync(
					this._RegionKey,
					items != null
						? items.Where(kvp => kvp.Key != null).Select(kvp => this._GetKey((string.IsNullOrWhiteSpace(keyPrefix) ? "" : keyPrefix) + kvp.Key)).ToArray()
						: new string[] { }
					)
				: Task.CompletedTask;
		}

		void _RemoveKey(string key)
		{
			if (this._storeKeys)
				Redis.Client.RemoveSetMembers(this._RegionKey, this._GetKey(key));
		}

		Task _RemoveKeyAsync(string key)
		{
			return this._storeKeys
				? Redis.Client.RemoveSetMembersAsync(this._RegionKey, this._GetKey(key))
				: Task.CompletedTask;
		}

		HashSet<string> _GetKeys()
		{
			return Redis.Client.GetSetMembers(this._RegionKey);
		}

		Task<HashSet<string>> _GetKeysAsync()
		{
			return Redis.Client.GetSetMembersAsync(this._RegionKey);
		}
		#endregion

		#region Set
		bool _Set(string key, object value, TimeSpan validFor)
		{
			var success = false;
			if (!string.IsNullOrWhiteSpace(key) && value != null)
				try
				{
					success = Redis.Client.Set(this._GetKey(key), value, validFor);
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
				this._UpdateKey(key);

			return success;
		}

		bool _Set(string key, object value, int expirationTime = 0)
		{
			return this._Set(key, value, TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime));
		}

		bool _Set(string key, object value, DateTime expiresAt)
		{
			return this._Set(key, value, expiresAt.ToTimeSpan());
		}

		async Task<bool> _SetAsync(string key, object value, TimeSpan validFor)
		{
			var success = false;
			if (!string.IsNullOrWhiteSpace(key) && value != null)
				try
				{
					success = await Redis.Client.SetAsync(this._GetKey(key), value, validFor);
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
				await this._UpdateKeyAsync(key);

			return success;
		}

		Task<bool> _SetAsync(string key, object value, int expirationTime = 0)
		{
			return this._SetAsync(key, value, TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime));
		}

		Task<bool> _SetAsync(string key, object value, DateTime expiresAt)
		{
			return this._SetAsync(key, value, expiresAt.ToTimeSpan());
		}
		#endregion

		#region Set (Multiple)
		void _Set<T>(IDictionary<string, T> items, string keyPrefix = null, int expirationTime = 0)
		{
			Redis.Client.Set(items != null
					? items.Where(kvp => kvp.Key != null).ToDictionary(kvp => this._GetKey((string.IsNullOrWhiteSpace(keyPrefix) ? "" : keyPrefix) + kvp.Key), kvp => kvp.Value)
					: new Dictionary<string, T>(),
				TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime)
			);
			this._UpdateKeys(items, keyPrefix);
		}

		void _Set(IDictionary<string, object> items, string keyPrefix = null, int expirationTime = 0)
		{
			this._Set<object>(items, keyPrefix, expirationTime);
		}

		Task _SetAsync<T>(IDictionary<string, T> items, string keyPrefix = null, int expirationTime = 0)
		{
			return Task.WhenAll(
				Redis.Client.SetAsync(items != null
					? items.Where(kvp => kvp.Key != null).ToDictionary(kvp => this._GetKey((string.IsNullOrWhiteSpace(keyPrefix) ? "" : keyPrefix) + kvp.Key), kvp => kvp.Value)
					: new Dictionary<string, T>(),
					TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime)
				),
				this._UpdateKeysAsync(items, keyPrefix)
			);
		}

		Task _SetAsync(IDictionary<string, object> items, string keyPrefix = null, int expirationTime = 0)
		{
			return this._SetAsync<object>(items, keyPrefix, expirationTime);
		}
		#endregion

		#region Set (Fragment)
		bool _SetFragments(string key, List<byte[]> fragments, int expirationTime = 0)
		{
			var validFor = TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime);
			var success = fragments != null && fragments.Count > 0
				? Redis.Client.Set(this._GetKey(key), Helper.Combine(BitConverter.GetBytes(Helper.FlagOfFirstFragmentBlock), BitConverter.GetBytes(fragments.Sum(f => f.Length)), fragments[0]), validFor)
				: false;

			if (success && fragments.Count > 1)
			{
				var items = new Dictionary<string, byte[]>();
				for (var index = 1; index < fragments.Count; index++)
					items[this._GetKey(this._GetFragmentKey(key, index))] = fragments[index];
				Redis.Client.Set(items, validFor);
			}

			return success;
		}

		async Task<bool> _SetFragmentsAsync(string key, List<byte[]> fragments, int expirationTime = 0)
		{
			var validFor = TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime);
			var success = fragments != null && fragments.Count > 0
				? await Redis.Client.SetAsync(this._GetKey(key), Helper.Combine(BitConverter.GetBytes(Helper.FlagOfFirstFragmentBlock), BitConverter.GetBytes(fragments.Sum(f => f.Length)), fragments[0]), validFor)
				: false;

			if (success && fragments.Count > 1)
			{
				var items = new Dictionary<string, byte[]>();
				for (var index = 1; index < fragments.Count; index++)
					items[this._GetKey(this._GetFragmentKey(key, index))] = fragments[index];
				await Redis.Client.SetAsync(items, validFor);
			}

			return success;
		}

		bool _SetAsFragments(string key, object value, int expirationTime = 0)
		{
			return string.IsNullOrWhiteSpace(key) || value == null
				? false
				: this._SetFragments(key, Helper.Split(Helper.Serialize(value, false)), expirationTime);
		}

		Task<bool> _SetAsFragmentsAsync(string key, object value, int expirationTime = 0)
		{
			return string.IsNullOrWhiteSpace(key) || value == null
				? Task.FromResult(false)
				: this._SetFragmentsAsync(key, Helper.Split(Helper.Serialize(value, false)), expirationTime);
		}
		#endregion

		#region Add
		bool _Add(string key, object value, TimeSpan validFor)
		{
			var success = false;
			if (!string.IsNullOrWhiteSpace(key) && value != null)
				try
				{
					success = Redis.Client.Add(this._GetKey(key), value, validFor);
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
				this._UpdateKey(key);

			return success;
		}

		bool _Add(string key, object value, int expirationTime = 0)
		{
			return this._Add(key, value, TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime));
		}

		bool _Add(string key, object value, DateTime expiresAt)
		{
			return this._Add(key, value, expiresAt.ToTimeSpan());
		}

		async Task<bool> _AddAsync(string key, object value, TimeSpan validFor)
		{
			var success = false;
			if (!string.IsNullOrWhiteSpace(key) && value != null)
				try
				{
					success = await Redis.Client.AddAsync(this._GetKey(key), value, validFor);
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
				await this._UpdateKeyAsync(key);

			return success;
		}

		Task<bool> _AddAsync(string key, object value, int expirationTime = 0)
		{
			return this._AddAsync(key, value, TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime));
		}

		Task<bool> _AddAsync(string key, object value, DateTime expiresAt)
		{
			return this._AddAsync(key, value, expiresAt.ToTimeSpan());
		}
		#endregion

		#region Replace
		bool _Replace(string key, object value, TimeSpan validFor)
		{
			var success = false;
			if (!string.IsNullOrWhiteSpace(key) && value != null)
				try
				{
					success = Redis.Client.Replace(this._GetKey(key), value, validFor);
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
				this._UpdateKey(key);

			return success;
		}

		bool _Replace(string key, object value, int expirationTime = 0)
		{
			return this._Replace(key, value, TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime));
		}

		bool _Replace(string key, object value, DateTime expiresAt)
		{
			return this._Replace(key, value, expiresAt.ToTimeSpan());
		}

		async Task<bool> _ReplaceAsync(string key, object value, TimeSpan validFor)
		{
			var success = false;
			if (!string.IsNullOrWhiteSpace(key) && value != null)
				try
				{
					success = await Redis.Client.ReplaceAsync(this._GetKey(key), value, validFor);
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
				await this._UpdateKeyAsync(key);

			return success;
		}

		Task<bool> _ReplaceAsync(string key, object value, int expirationTime = 0)
		{
			return this._ReplaceAsync(key, value, TimeSpan.FromMinutes(expirationTime > 0 ? expirationTime : this.ExpirationTime));
		}

		Task<bool> _ReplaceAsync(string key, object value, DateTime expiresAt)
		{
			return this._ReplaceAsync(key, value, expiresAt.ToTimeSpan());
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
				value = Redis.Client.Get(this._GetKey(key), false);
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this.Name, $"Error occurred while fetching an object from cache storage [{key}]", ex);
			}

			if (value != null && (value as byte[]).Length > 8)
			{
				if (autoGetFragments && Helper.GetFlags(value as byte[]).Item1.Equals(Helper.FlagOfFirstFragmentBlock))
					try
					{
						value = this._GetFromFragments(key, value as byte[]);
					}
					catch (Exception ex)
					{
						Helper.WriteLogs(this.Name, $"Error occurred while fetching an objects' fragments from cache storage [{key}]", ex);
						value = null;
					}
				else
					try
					{
						value = Helper.Deserialize(value as byte[]);
					}
					catch (Exception ex)
					{
						Helper.WriteLogs(this.Name, $"Error occurred while fetching an object from cache storage [{key}]", ex);
						value = null;
					}
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
				value = await Redis.Client.GetAsync(this._GetKey(key), false);
			}
			catch (Exception ex)
			{
				Helper.WriteLogs(this.Name, $"Error occurred while fetching an object from cache storage [{key}]", ex);
			}

			if (value != null && (value as byte[]).Length > 8)
			{
				if (autoGetFragments && Helper.GetFlags(value as byte[]).Item1.Equals(Helper.FlagOfFirstFragmentBlock))
					try
					{
						value = await this._GetFromFragmentsAsync(key, value as byte[]);
					}
					catch (Exception ex)
					{
						Helper.WriteLogs(this.Name, $"Error occurred while fetching an objects' fragments from cache storage [{key}]", ex);
						value = null;
					}
				else
					try
					{
						value = Helper.Deserialize(value as byte[]);
					}
					catch (Exception ex)
					{
						Helper.WriteLogs(this.Name, $"Error occurred while fetching an object from cache storage [{key}]", ex);
						value = null;
					}
			}

			return value;
		}
		#endregion

		#region Get (Multiple)
		IDictionary<string, object> _Get(IEnumerable<string> keys)
		{
			if (keys == null)
				return null;

			IDictionary<string, object> items = null;
			try
			{
				items = Redis.Client.Get(keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => this._GetKey(key)));
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
			if (keys == null)
				return null;

			IDictionary<string, T> items = null;
			try
			{
				items = Redis.Client.Get<T>(keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => this._GetKey(key)));
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

		async Task<IDictionary<string, object>> _GetAsync(IEnumerable<string> keys)
		{
			if (keys == null)
				return null;

			IDictionary<string, object> items = null;
			try
			{
				items = await Redis.Client.GetAsync(keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => this._GetKey(key)));
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
			if (keys == null)
				return null;

			IDictionary<string, T> items = null;
			try
			{
				items = await Redis.Client.GetAsync<T>(keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => this._GetKey(key)));
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
		#endregion

		#region Get (Fragment)
		Tuple<int, int> _GetFragments(byte[] data)
		{
			var info = Helper.GetFlags(data);
			if (info == null)
				return null;

			var blocks = 0;
			var offset = 0;
			while (offset < info.Item2)
			{
				blocks++;
				offset += Helper.FragmentSize;
			}
			return new Tuple<int, int>(blocks, info.Item2);
		}

		Tuple<int, int> _GetFragments(string key)
		{
			var data = this._Get(key, false) as byte[];
			return data != null
				? this._GetFragments(data)
				: null;
		}

		async Task<Tuple<int, int>> _GetFragmentsAsync(string key)
		{
			var data = await this._GetAsync(key, false) as byte[];
			return data != null
				? this._GetFragments(data)
				: null;
		}

		List<byte[]> _GetAsFragments(string key, List<int> indexes)
		{
			if (string.IsNullOrWhiteSpace(key) || indexes == null || indexes.Count < 1)
				return new List<byte[]>();

			var fragments = Enumerable.Repeat(new byte[0], indexes.Count).ToList();
			for (var index = 0; index < indexes.Count; index++)
				fragments[index] = Redis.Client.Get(this._GetKey(indexes[index] > 0 ? this._GetFragmentKey(key, indexes[index]) : key), false) as byte[];
			return fragments;
		}

		async Task<List<byte[]>> _GetAsFragmentsAsync(string key, List<int> indexes)
		{
			if (string.IsNullOrWhiteSpace(key) || indexes == null || indexes.Count < 1)
				return new List<byte[]>();

			var fragments = Enumerable.Repeat(new byte[0], indexes.Count).ToList();
			Func<int, Task> func = async (index) =>
			{
				fragments[index] = await Redis.Client.GetAsync(this._GetKey(indexes[index] > 0 ? this._GetFragmentKey(key, indexes[index]) : key), false) as byte[];
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
				var info = this._GetFragments(firstBlock);
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
				var info = this._GetFragments(firstBlock);
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
		bool _Remove(string key)
		{
			var success = false;
			if (!string.IsNullOrWhiteSpace(key))
				try
				{
					success = Redis.Client.Remove(this._GetKey(key));
				}
				catch (Exception ex)
				{
					Helper.WriteLogs(this.Name, $"Error occurred while removing an object from cache storage [{key}]", ex);
				}

			if (success)
				this._RemoveKey(key);

			return success;
		}

		async Task<bool> _RemoveAsync(string key)
		{
			var success = false;
			if (!string.IsNullOrWhiteSpace(key))
				try
				{
					success = await Redis.Client.RemoveAsync(this._GetKey(key));
				}
				catch (Exception ex)
				{
					Helper.WriteLogs(this.Name, $"Error occurred while removing an object from cache storage [{key}]", ex);
				}

			if (success)
				await this._RemoveKeyAsync(key);

			return success;
		}
		#endregion

		#region Remove (Multiple)
		void _Remove(IEnumerable<string> keys, string keyPrefix = null)
		{
			if (keys != null)
				keys.Where(key => !string.IsNullOrWhiteSpace(key)).ToList().ForEach(key => this._Remove((string.IsNullOrWhiteSpace(keyPrefix) ? "" : keyPrefix) + key));
		}

		Task _RemoveAsync(IEnumerable<string> keys, string keyPrefix = null)
		{
			return keys != null
				? Task.WhenAll(keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => this._RemoveAsync((string.IsNullOrWhiteSpace(keyPrefix) ? "" : keyPrefix) + key)))
				: Task.CompletedTask;
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
			Redis.Client.Remove(this._RegionKey);
		}

		async Task _ClearAsync()
		{
			var keys = await this._GetKeysAsync();
			await Task.WhenAll(
				this._RemoveAsync(keys),
				Redis.Client.RemoveAsync(this._RegionKey)
			);
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
		/// <summary>
		/// Gets the collection of all registered regions (in distributed cache)
		/// </summary>
		public static HashSet<string> GetRegions()
		{
			return Redis.Client.GetSetMembers(Helper.RegionsKey);
		}

		/// <summary>
		/// Gets the collection of all registered regions (in distributed cache)
		/// </summary>
		public static Task<HashSet<string>> GetRegionsAsync()
		{
			return Redis.Client.GetSetMembersAsync(Helper.RegionsKey);
		}

		static async Task RegisterRegionAsync(string name)
		{
			try
			{
				await Redis.Client.UpdateSetMembersAsync(Helper.RegionsKey, name);
			}
			catch { }
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
			return this._SetAsync(key, value, expirationTime);
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
			return this._Add(key, value, expirationTime);
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
			return this._Add(key, value, validFor);
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
			return this._Add(key, value, expiresAt);
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
		/// Adds an item into cache with a specified key when the the key is not existed
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> AddAsync(string key, object value, TimeSpan validFor)
		{
			return this._AddAsync(key, value, validFor);
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
			return this._AddAsync(key, value, expiresAt);
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
			return this._Replace(key, value, expirationTime);
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
			return this._Replace(key, value, validFor);
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
			return this._Replace(key, value, expiresAt);
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

		/// <summary>
		/// Adds an item into cache with a specified key when the the key is existed (means update existed item)
		/// </summary>
		/// <param name="key">The string that presents key of item</param>
		/// <param name="value">The object that is to be cached</param>
		/// <param name="validFor">The time when the item is invalidated in the cache</param>
		/// <returns>Returns a boolean value indicating if the item is added into cache successful or not</returns>
		public Task<bool> ReplaceAsync(string key, object value, TimeSpan validFor)
		{
			return this._ReplaceAsync(key, value, validFor);
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
			return this._ReplaceAsync(key, value, expiresAt);
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
			return Redis.Client.Exists(this._GetKey(key));
		}

		/// <summary>
		/// Determines whether an item exists in the cache
		/// </summary>
		/// <param name="key">The string that presents key of cached item need to check</param>
		/// <returns>Returns a boolean value indicating if the object that associates with the key is cached or not</returns>
		public Task<bool> ExistsAsync(string key)
		{
			return Redis.Client.ExistsAsync(this._GetKey(key));
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

	// -----------------------------------------------------

	#region Configuration section handler
	public class RedisClientConfigurationSectionHandler : IConfigurationSectionHandler
	{
		public object Create(object parent, object configContext, XmlNode section)
		{
			this._section = section;
			return this;
		}

		XmlNode _section = null;
		public XmlNode Section { get { return this._section; } }
	}
	#endregion

}