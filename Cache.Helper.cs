#region Related components
using System;
using System.IO;
using System.Xml;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Configuration;

using Newtonsoft.Json.Linq;

using Enyim.Caching;
using Enyim.Caching.Configuration;
using Enyim.Caching.Memcached;

using StackExchange.Redis;
#endregion

namespace net.vieapps.Components.Caching
{
	public static class Helper
	{

		#region Data
		public static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1);
		public static readonly int ExpirationTime = 30;
		internal static readonly int FragmentSize = (1024 * 1024) - 512;
		internal static readonly string RegionsKey = "VIEApps-NGX-Regions";

		public static TimeSpan ToTimeSpan(this DateTime value)
		{
			return value - Helper.UnixEpoch;
		}
		#endregion

		#region Split into fragments
		internal static List<byte[]> Split(byte[] data, int fragmentSize = 0)
		{
			var fragments = new List<byte[]>();
			if (data != null && data.Length > 0)
			{
				fragmentSize = fragmentSize > 0 ? fragmentSize : Helper.FragmentSize;
				int index = 0, length = data.Length;
				while (index < data.Length)
				{
					var size = fragmentSize > length
						? length
						: fragmentSize;

					var fragment = new byte[size];
					Array.Copy(data, index, fragment, 0, size);
					fragments.Add(fragment);

					index += size;
					length -= size;
				}
			}
			return fragments;
		}

		internal static List<byte[]> Split(object @object, int fragmentSize = 0)
		{
			return Helper.Split(Helper.Serialize(@object), fragmentSize);
		}
		#endregion

		#region Serialize & Deserialize
		static DefaultTranscoder _Transcoder = null;

		internal static DefaultTranscoder Transcoder
		{
			get
			{
				return Helper._Transcoder ?? (Helper._Transcoder = new DefaultTranscoder());
			}
		}

		public static byte[] Serialize(object value)
		{
			return value == null
				? new byte[0]
				: value is byte[]
					? (byte[])value
					: value is ArraySegment<byte>
						? ((ArraySegment<byte>)value).Array
						: Helper.Transcoder.SerializeObject(value).Array;
		}

		public static object Deserialize(byte[] data)
		{
			return data == null || data.Length < 1
				? null
				: Helper.Transcoder.DeserializeObject(new ArraySegment<byte>(data));
		}

		public static T Deserialize<T>(byte[] value)
		{
			if (typeof(T).Equals(typeof(byte[])))
				return (T)((object)value);

			if (typeof(T).Equals(typeof(ArraySegment<byte>)))
				return (T)((object)new ArraySegment<byte>(value));

			var data = Helper.Deserialize(value);
			return data != null && data is T
				? (T)data
				: default(T);
		}

		internal static T Clone<T>(T @object)
		{
			return Helper.Deserialize<T>(Helper.Serialize(@object));
		}
		#endregion

		#region Get client of a cache provider
		internal static MemcachedClient GetMemcachedClient()
		{
			var configuration = ConfigurationManager.GetSection("memcached") as MemcachedClientConfigurationSectionHandler;
			if (configuration == null)
				throw new ConfigurationErrorsException("The section named 'memcached' is not found, please check your configuration file (app.config or web.config");
			return new Enyim.Caching.MemcachedClient(configuration);
		}

		internal static ConnectionMultiplexer RedisConnection = null;

		internal static IDatabase GetRedisClient()
		{
			var configuration = ConfigurationManager.GetSection("redis") as RedisClientConfigurationSectionHandler;
			if (configuration == null)
				throw new ConfigurationErrorsException("The section named 'redis' is not found, please check your configuration file (app.config or web.config");

			var connectionString = "";
			if (configuration.Section.SelectNodes("servers/add") is XmlNodeList nodes)
				foreach (XmlNode server in nodes)
				{
					var info = configuration.GetJson(server);
					var address = (info["address"] as JValue).Value as string;
					var port = Convert.ToInt32(info["port"] != null ? (info["port"] as JValue).Value as string : "6379");
					connectionString += (connectionString != "" ? "," : "") + address + ":" + port.ToString();
				}

			if (configuration.Section.SelectSingleNode("options") is XmlNode node)
				foreach (XmlAttribute option in node.Attributes)
					if (!string.IsNullOrWhiteSpace(option.Value))
						connectionString += (connectionString != "" ? "," : "") + option.Name + "=" + option.Value;

			Helper.RedisConnection = Helper.RedisConnection ?? ConnectionMultiplexer.Connect(connectionString);
			return Helper.RedisConnection.GetDatabase();
		}
		#endregion

		#region Working with logs
		internal static string GetLogPrefix(string label, string seperator = ":")
		{
			return label + seperator + "[" + Process.GetCurrentProcess().Id.ToString() + " : " + AppDomain.CurrentDomain.Id.ToString() + " : " + Thread.CurrentThread.ManagedThreadId.ToString() + "]";
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

	[Serializable]
	public struct Fragment
	{
		public string Key;
		public string Type;
		public int TotalFragments;
	}
}