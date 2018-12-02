#region Related components
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Dynamic;
using System.Diagnostics;
using System.Configuration;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Bson;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

using net.vieapps.Components.Caching;
#endregion

namespace net.vieapps.Components.Caching
{
	public static class Helper
	{

		#region Data
		public const int ExpirationTime = 30;
		public const int FlagOfJsonObject = 0xfb52;
		public const int FlagOfJsonArray = 0xfc52;
		public const int FlagOfExpandoObject = 0xfd52;
		public const int FlagOfFirstFragmentBlock = 0xfe52;
		internal static readonly int FragmentSize = (1024 * 1024) - 128;
		internal static readonly string RegionsKey = "VIEApps-NGX-Regions";
		internal static readonly string RegionName = "VIEApps-NGX-Cache";

		internal static string GetRegionName(string name)
			=> string.IsNullOrWhiteSpace(name)
				? Helper.RegionName
				: Regex.Replace(name, "[^0-9a-zA-Z:-]+", "");

		internal static string GetCacheKey(string region, string key) => region + "@" + key.Replace(" ", "-");

		internal static string GetFragmentKey(string key, int index)
		{
			var fragmentKey = "0" + index.ToString();
			return key.Replace(" ", "-") + "$[Fragment<" + fragmentKey.Substring(fragmentKey.Length - 2) + ">]";
		}

		internal static List<string> GetFragmentKeys(string key, int max)
		{
			var keys = new List<string> { key };
			for (var index = 1; index <= max; index++)
				keys.Add(Helper.GetFragmentKey(key, index));
			return keys;
		}
		#endregion

		#region Split & Combine
		internal static byte[] Combine(byte[] first, IEnumerable<byte[]> arrays)
		{
			var combined = new byte[first.Length + arrays.Where(a => a != null).Sum(a => a.Length)];
			var offset = first.Length;
			Buffer.BlockCopy(first, 0, combined, 0, offset);
			arrays.Where(a => a != null).ToList().ForEach(a =>
			{
				Buffer.BlockCopy(a, 0, combined, offset, a.Length);
				offset += a.Length;
			});
			return combined;
		}

		internal static List<byte[]> Split(byte[] data, int size = 0)
		{
			var blocks = new List<byte[]>();
			if (data != null && data.Length > 0)
			{
				size = size > 0 ? size : Helper.FragmentSize;
				var offset = 0;
				var length = data.Length;
				while (offset < data.Length)
				{
					var count = size > length ? length : size;
					var block = new byte[count];
					Buffer.BlockCopy(data, offset, block, 0, count);
					blocks.Add(block);
					offset += count;
					length -= count;
				}
			}
			return blocks;
		}
		#endregion

		#region Fragments
		internal static Tuple<int, int> GetFlags(byte[] data, bool getLength = false)
		{
			if (data == null || data.Length < 4)
				return null;

			var tmp = new byte[4];
			Buffer.BlockCopy(data, 0, tmp, 0, 4);
			var typeFlag = BitConverter.ToInt32(tmp, 0);

			var length = data.Length - 4;
			if (getLength && data.Length > 7)
			{
				Buffer.BlockCopy(data, 4, tmp, 0, 4);
				length = BitConverter.ToInt32(tmp, 0);
			}

			return new Tuple<int, int>(typeFlag, length);
		}

		internal static byte[] GetFirstBlock(List<byte[]> fragments)
			=> CacheUtils.Helper.Combine(BitConverter.GetBytes(Helper.FlagOfFirstFragmentBlock), BitConverter.GetBytes(fragments.Sum(f => f.Length)), fragments[0]);

		internal static Tuple<int, int> GetFragments(byte[] data)
		{
			var info = Helper.GetFlags(data, true);
			if (info == null)
				return null;

			var blocks = 0;
			var offset = 0;
			var length = info.Item2;
			while (offset < length)
			{
				blocks++;
				offset += Helper.FragmentSize;
			}
			return new Tuple<int, int>(blocks, length);
		}
		#endregion

		#region Serialize & Deserialize
		/// <summary>
		/// Serializes an object into array of bytes
		/// </summary>
		/// <param name="value"></param>
		/// <param name="addFlags"></param>
		/// <returns></returns>
		public static byte[] Serialize(object value, bool addFlags = true)
		{
			var typeFlag = 0;
			var data = new byte[0];

			if (value != null && (value is JToken || value is ExpandoObject))
			{
				typeFlag = value is JToken
					? value is JArray ? Helper.FlagOfJsonArray : Helper.FlagOfJsonObject
					: Helper.FlagOfExpandoObject;
				using (var stream = CacheUtils.Helper.CreateMemoryStream())
				{
					using (var writer = new BsonDataWriter(stream))
					{
						new JsonSerializer().Serialize(writer, value);
						if (stream.TryGetBuffer(out ArraySegment<byte> buffer))
						{
							data = new byte[buffer.Count];
							Buffer.BlockCopy(buffer.Array, buffer.Offset, data, 0, buffer.Count);
						}
						else
							data = stream.ToArray();
					}
				}
			}
			else
			{
				var info = CacheUtils.Helper.Serialize(value);
				typeFlag = info.Item1;
				data = info.Item2;
			}

			return addFlags
				? CacheUtils.Helper.Combine(BitConverter.GetBytes(typeFlag), data)
				: data;
		}

		internal static object Deserialize(byte[] data, int start, int count) => CacheUtils.Helper.Deserialize(data, (int)TypeCode.Object | 0x0100, start, count);

		/// <summary>
		/// Deserializes an object from the array of bytes
		/// </summary>
		/// <param name="data"></param>
		/// <returns></returns>
		public static object Deserialize(byte[] data)
		{
			if (data == null || data.Length < 4)
				return null;

			var typeFlag = Helper.GetFlags(data).Item1;
			if (typeFlag.Equals(Helper.FlagOfJsonObject) || typeFlag.Equals(Helper.FlagOfJsonArray) || typeFlag.Equals(Helper.FlagOfExpandoObject))
				using (var stream = CacheUtils.Helper.CreateMemoryStream(data, 4, data.Length - 4))
				{
					using (var reader = new BsonDataReader(stream))
					{
						if (typeFlag.Equals(Helper.FlagOfJsonArray))
							reader.ReadRootValueAsArray = true;
						return typeFlag.Equals(Helper.FlagOfExpandoObject)
							? new JsonSerializer().Deserialize<ExpandoObject>(reader)
							: new JsonSerializer().Deserialize(reader);
					}
				}
			else
				return CacheUtils.Helper.Deserialize(data, typeFlag, 4, data.Length - 4);
		}

		public static T Deserialize<T>(byte[] data)
		{
			var value = data != null ? Helper.Deserialize(data) : null;
			return value != null && value is T ? (T)value : default(T);
		}
		#endregion

		#region Working with logs
		internal static ILogger Logger { get; set; } = Enyim.Caching.Logger.CreateLogger<Cache>();

		internal static void WriteLogs(string region, List<string> logs, Exception ex)
		{
			if (ex != null)
			{
				logs.ForEach(log => Helper.Logger.LogInformation($"<{region}>: {log}"));
				Helper.Logger.LogError(ex, ex.Message);
			}
			else if (Helper.Logger.IsEnabled(LogLevel.Debug))
				logs.ForEach(log => Helper.Logger.LogInformation($"<{region}>: {log}"));
		}

		internal static void WriteLogs(string region, string log, Exception ex)
		{
			if (ex != null)
				Logger.LogError(ex, $"<{region}>: {log}");
			else if (Logger.IsEnabled(LogLevel.Debug))
				Logger.LogInformation(ex, $"<{region}>: {log}");
		}
		#endregion

	}
}

namespace Microsoft.Extensions.DependencyInjection
{
	public static partial class ServiceCollectionExtensions
	{
		/// <summary>
		/// Adds the <see cref="ICache">VIEApps NGX Caching</see> service into the collection of services for using with dependency injection
		/// </summary>
		/// <param name="services"></param>
		/// <param name="setupAction">The action to bind options of 'Cache' section from appsettings.json file</param>
		/// <param name="addInstanceOfIDistributedCache">true to add the cache service as an instance of IDistributedCache</param>
		/// <returns></returns>
		public static IServiceCollection AddCache(this IServiceCollection services, Action<CacheOptions> setupAction, bool addInstanceOfIDistributedCache = true)
		{
			if (setupAction == null)
				throw new ArgumentNullException(nameof(setupAction));

			services.AddOptions().Configure(setupAction);
			services.Add(ServiceDescriptor.Singleton<ICacheConfiguration, CacheConfiguration>());
			services.Add(ServiceDescriptor.Singleton<ICache, Cache>(svcProvider => Cache.GetInstance(svcProvider)));
			if (addInstanceOfIDistributedCache)
				services.Add(ServiceDescriptor.Singleton<IDistributedCache, Cache>(svcProvider => Cache.GetInstance(svcProvider)));

			return services;
		}
	}
}

namespace Microsoft.AspNetCore.Builder
{
	public static partial class ApplicationBuilderExtensions
	{
		/// <summary>
		/// Calls to use the <see cref="ICache">VIEApps NGX Caching</see> service
		/// </summary>
		/// <param name="appBuilder"></param>
		/// <returns></returns>
		public static IApplicationBuilder UseCache(this IApplicationBuilder appBuilder)
		{
			try
			{
				appBuilder.ApplicationServices.GetService<ILogger<ICache>>().LogInformation($"The service of VIEApps NGX Distributed Caching was{(appBuilder.ApplicationServices.GetService<ICache>() != null ? " " : " not ")}registered with application service providers");
			}
			catch (Exception ex)
			{
				appBuilder.ApplicationServices.GetService<ILogger<ICache>>().LogError(ex, $"Error occurred while collecting information of the service of VIEApps NGX Distributed Caching => {ex.Message}");
			}
			return appBuilder;
		}
	}
}
