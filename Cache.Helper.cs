#region Related components
using System;
using System.IO;
using System.Text;
using System.Xml;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Configuration;
using System.Runtime.Serialization.Formatters.Binary;

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
		public const int FlagOfFirstFragmentBlock = 0xfd52;
		internal static readonly int FragmentSize = (1024 * 1024) - 128;
		internal static readonly string RegionsKey = "VIEApps-NGX-Regions";
		internal static readonly string RegionName = "VIEApps-NGX-Cache";

		internal static string GetRegionName(string name)
		{
			return string.IsNullOrWhiteSpace(name)
				? Helper.RegionName
				: System.Text.RegularExpressions.Regex.Replace(name, "[^0-9a-zA-Z:-]+", "");
		}
		#endregion

		#region Split & Combine
		internal static byte[] Combine(byte[] first, IEnumerable<byte[]> arrays)
		{
			var combined = new byte[first.Length + arrays.Sum(a => a.Length)];
			var offset = first.Length;
			Buffer.BlockCopy(first, 0, combined, 0, offset);
			foreach (var array in arrays)
			{
				Buffer.BlockCopy(array, 0, combined, offset, array.Length);
				offset += array.Length;
			}
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

		#region Serialize & Deserialize
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
		{
			return CacheUtils.Helper.Combine(BitConverter.GetBytes(Helper.FlagOfFirstFragmentBlock), BitConverter.GetBytes(fragments.Sum(f => f.Length)), fragments[0]);
		}

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

			if (value != null && value is JToken)
			{
				typeFlag = value is JArray ? Helper.FlagOfJsonArray : Helper.FlagOfJsonObject;
				using (var stream = new MemoryStream())
				{
					using (var writer = new BsonDataWriter(stream))
					{
						(new JsonSerializer()).Serialize(writer, value);
						data = stream.GetBuffer();
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

		internal static object Deserialize(byte[] data, int start, int count)
		{
			return CacheUtils.Helper.Deserialize(data, (int)TypeCode.Object | 0x0100, start, count);
		}

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
			if (typeFlag.Equals(Helper.FlagOfJsonObject) || typeFlag.Equals(Helper.FlagOfJsonArray))
				using (var stream = new MemoryStream(data, 4, data.Length - 4))
				{
					using (var reader = new BsonDataReader(stream))
					{
						if (typeFlag.Equals(Helper.FlagOfJsonArray))
							reader.ReadRootValueAsArray = true;
						return (new JsonSerializer()).Deserialize(reader);
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
		internal static string GetLogPrefix(string label, string seperator = ":")
		{
			return $"{label}{seperator}[{Process.GetCurrentProcess().Id} : {AppDomain.CurrentDomain.Id} : {Thread.CurrentThread.ManagedThreadId}]";
		}

		static string LogsPath = null;

		internal static async Task WriteLogs(string filePath, string region, List<string> logs, Exception ex)
		{
			// prepare
			var info = Helper.GetLogPrefix(DateTime.Now.ToString("HH:mm:ss.fff"), "\t") + "\t" + region + "\t";

			var content = "";
			if (logs != null)
				logs.ForEach(log =>
				{
					if (!string.IsNullOrWhiteSpace(log))
						content += info + log + "\r\n";
				});

			if (ex != null)
			{
				content += info + "- " + (ex.Message != null ? ex.Message : "No error message") + $" [{ex.GetType()}]" + "\r\n"
					+ info + "- " + (ex.StackTrace != null ? ex.StackTrace : "No stack trace");

				ex = ex.InnerException;
				var counter = 1;
				while (ex != null)
				{
					content += info + "- Inner (" + counter.ToString() + "): ----------------------------------" + "\r\n"
						+ info + "- " + (ex.Message != null ? ex.Message : "No error message") + $" [{ex.GetType()}]" + "\r\n"
						+ info + "- " + (ex.StackTrace != null ? ex.StackTrace : "No stack trace");

					counter++;
					ex = ex.InnerException;
				}

				content += "\r\n";
			}

			// write logs into file
			try
			{
				using (var stream =  new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, true))
				{
					using (var writer =  new StreamWriter(stream, System.Text.Encoding.UTF8))
					{
						await writer.WriteLineAsync(content + "\r\n");
					}
				}
			}
			catch { }
		}

		internal static void WriteLogs(string region, List<string> logs, Exception ex)
		{
			// prepare path of all log files
			if (string.IsNullOrWhiteSpace(Helper.LogsPath))
				try
				{
					Helper.LogsPath = ConfigurationManager.AppSettings["vieapps:LogsPath"];
					if (!Helper.LogsPath.EndsWith(@"\"))
						Helper.LogsPath += @"\";
				}
				catch { }

			if (string.IsNullOrWhiteSpace(Helper.LogsPath))
				try
				{
					Helper.LogsPath = Directory.GetCurrentDirectory() + @"\Logs\";
				}
				catch { }

			// stop if a valid path is not found
			if (string.IsNullOrWhiteSpace(Helper.LogsPath))
				return;

			// build file path and write logs via other thread
			var filePath = Helper.LogsPath + DateTime.Now.ToString("yyyy-MM-dd") + ".cache.txt";
			Task.Run(async () =>
			{
				try
				{
					await Helper.WriteLogs(filePath, region, logs, ex).ConfigureAwait(false);
				}
				catch { }
			}).ConfigureAwait(false);
		}

		internal static void WriteLogs(string region, string log, Exception ex)
		{
			Helper.WriteLogs(region, string.IsNullOrWhiteSpace(log) ? null : new List<string>() { log }, ex);
		}
		#endregion

	}
}