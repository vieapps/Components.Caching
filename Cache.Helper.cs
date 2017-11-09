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
#endregion

namespace net.vieapps.Components.Caching
{
	public static class Helper
	{

		#region Data
		public static readonly int RawDataFlag = 0xfa52;
		public static readonly int FragmentDataFlag = 0xfb52;
		public static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1);
		public static readonly int ExpirationTime = 30;
		internal static readonly int FragmentSize = (1024 * 1024) - 512;
		internal static readonly string RegionsKey = "VIEApps-NGX-Regions";

		public static TimeSpan ToTimeSpan(this DateTime value)
		{
			return value - Helper.UnixEpoch;
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

		public static byte[] Combine(params byte[][] arrays)
		{
			var combined = new byte[arrays.Sum(a => a.Length)];
			var offset = 0;
			foreach (var array in arrays)
			{
				Buffer.BlockCopy(array, 0, combined, offset, array.Length);
				offset += array.Length;
			}
			return combined;
		}

		public static List<byte[]> Split(byte[] data, int size = 0)
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

		public static List<byte[]> Split(object @object, int size = 0)
		{
			return Helper.Split(Helper.Serialize(@object), size);
		}
		#endregion

		#region Serialize & Deserialize
		public static Tuple<int, int> GetFlags(byte[] data)
		{
			if (data == null || data.Length < 4)
				return null;

			var tmp = new byte[4];
			Buffer.BlockCopy(data, 0, tmp, 0, 4);
			var typeFlag = BitConverter.ToInt32(tmp, 0);

			var length = 0;
			if (data.Length > 7)
			{
				Buffer.BlockCopy(data, 4, tmp, 0, 4);
				length = BitConverter.ToInt32(tmp, 0);
			}

			return new Tuple<int, int>(typeFlag, length);
		}

		internal static byte[] Serialize(object value, bool addFlags)
		{
			var data = new byte[0];
			var typeCode = value == null ? TypeCode.DBNull : Type.GetTypeCode(value.GetType());
			var typeFlag = (int)typeCode | 0x0100;
			switch (typeCode)
			{
				case TypeCode.Empty:
				case TypeCode.DBNull:
					break;

				case TypeCode.Boolean:
					data = BitConverter.GetBytes((bool)value);
					break;

				case TypeCode.DateTime:
					data = BitConverter.GetBytes(((DateTime)value).ToBinary());
					break;

				case TypeCode.Char:
					data = BitConverter.GetBytes((char)value);
					break;

				case TypeCode.String:
					data = Encoding.UTF8.GetBytes((string)value);
					break;

				case TypeCode.Byte:
					data = BitConverter.GetBytes((byte)value);
					break;

				case TypeCode.SByte:
					data = BitConverter.GetBytes((sbyte)value);
					break;

				case TypeCode.Int16:
					data = BitConverter.GetBytes((short)value);
					break;

				case TypeCode.UInt16:
					data = BitConverter.GetBytes((ushort)value);
					break;

				case TypeCode.Int32:
					data = BitConverter.GetBytes((int)value);
					break;

				case TypeCode.UInt32:
					data = BitConverter.GetBytes((uint)value);
					break;

				case TypeCode.Int64:
					data = BitConverter.GetBytes((long)value);
					break;

				case TypeCode.UInt64:
					data = BitConverter.GetBytes((ulong)value);
					break;

				case TypeCode.Single:
					data = BitConverter.GetBytes((float)value);
					break;

				case TypeCode.Double:
					data = BitConverter.GetBytes((double)value);
					break;

				default:
					if (value is byte[] || value is ArraySegment<byte>)
					{
						typeFlag = Helper.RawDataFlag;
						data = value is byte[] ? value as byte[] : ((ArraySegment<byte>)value).Array;
					}
					else
					{
						if (value.GetType().IsSerializable)
							using (var stream = new MemoryStream())
							{
								(new BinaryFormatter()).Serialize(stream, value);
								data = stream.GetBuffer();
							}
						else
							throw new ArgumentException($"The type '{value.GetType()}' must have Serializable attribute or implemented the ISerializable interface");
					}
					break;
			}

			return addFlags
				? Helper.Combine(BitConverter.GetBytes(typeFlag), BitConverter.GetBytes(data.Length), data)
				: data;
		}

		public static byte[] Serialize(object value)
		{
			return Helper.Serialize(value, true);
		}

		internal static object Deserialize(byte[] data, int start, int count)
		{
			using (var stream = new MemoryStream(data, start, count))
			{
				return (new BinaryFormatter()).Deserialize(stream);
			}
		}

		public static object Deserialize(byte[] data)
		{
			if (data == null || data.Length < 9)
				return null;

			var info = Helper.GetFlags(data);
			var typeFlag = info.Item1;
			var dataLength = info.Item2;

			var tmp = new byte[0];
			if (typeFlag.Equals(Helper.RawDataFlag))
			{
				tmp = new byte[info.Item2];
				Buffer.BlockCopy(data, 8, tmp, 0, dataLength);
				return tmp;
			}

			var typeCode = (TypeCode)(typeFlag & 0xff);
			if (!typeCode.Equals(TypeCode.Empty) && !typeCode.Equals(TypeCode.DBNull) && !typeCode.Equals(TypeCode.Decimal) && !typeCode.Equals(TypeCode.Object))
			{
				tmp = new byte[info.Item2];
				Buffer.BlockCopy(data, 8, tmp, 0, dataLength);
			}

			switch (typeCode)
			{
				case TypeCode.Empty:
				case TypeCode.DBNull:
					return null;

				case TypeCode.Boolean:
					return BitConverter.ToBoolean(tmp, 0);

				case TypeCode.DateTime:
					return DateTime.FromBinary(BitConverter.ToInt64(tmp, 0));

				case TypeCode.Char:
					return BitConverter.ToChar(tmp, 0);

				case TypeCode.String:
					return Encoding.UTF8.GetString(tmp, 0, tmp.Length);

				case TypeCode.Byte:
					return tmp[0];

				case TypeCode.SByte:
					return (sbyte)tmp[0];

				case TypeCode.Int16:
					return BitConverter.ToInt16(tmp, 0);

				case TypeCode.UInt16:
					return BitConverter.ToUInt16(tmp, 0);

				case TypeCode.Int32:
					return BitConverter.ToInt32(tmp, 0);

				case TypeCode.UInt32:
					return BitConverter.ToUInt32(tmp, 0);

				case TypeCode.Int64:
					return BitConverter.ToInt64(tmp, 0);

				case TypeCode.UInt64:
					return BitConverter.ToUInt64(tmp, 0);

				case TypeCode.Single:
					return BitConverter.ToSingle(tmp, 0);

				case TypeCode.Double:
					return BitConverter.ToDouble(tmp, 0);

				default:
					return data.Length > 8
						? Helper.Deserialize(data, 8, dataLength)
						: null;
			}
		}

		public static T Deserialize<T>(byte[] data)
		{
			var value = data != null && data.Length > 8
				? Helper.Deserialize(data)
				: default(T);
			return value != null && value is T
				? (T)value
				: default(T);
		}
		#endregion

		#region Get client of a cache provider
		internal static Enyim.Caching.MemcachedClient GetMemcachedClient()
		{
			var configuration = ConfigurationManager.GetSection("memcached") as Enyim.Caching.Configuration.MemcachedClientConfigurationSectionHandler;
			if (configuration == null)
				throw new ConfigurationErrorsException("The section named 'memcached' is not found, please check your configuration file (app.config or web.config");
			return new Enyim.Caching.MemcachedClient(configuration);
		}

		internal static StackExchange.Redis.ConnectionMultiplexer RedisConnection = null;

		internal static StackExchange.Redis.IDatabase GetRedisClient()
		{
			var configuration = ConfigurationManager.GetSection("redis") as RedisClientConfigurationSectionHandler;
			if (configuration == null)
				throw new ConfigurationErrorsException("The section named 'redis' is not found, please check your configuration file (app.config or web.config");

			var connectionString = "";
			if (configuration.Section.SelectNodes("servers/add") is XmlNodeList nodes)
				foreach (XmlNode server in nodes)
				{
					var address = server.Attributes["address"]?.Value ?? "localhost";
					var port = Convert.ToInt32(server.Attributes["port"]?.Value ?? "6379");
					connectionString += (connectionString != "" ? "," : "") + address + ":" + port.ToString();
				}

			if (configuration.Section.SelectSingleNode("options") is XmlNode node)
				foreach (XmlAttribute option in node.Attributes)
					if (!string.IsNullOrWhiteSpace(option.Value))
						connectionString += (connectionString != "" ? "," : "") + option.Name + "=" + option.Value;

			Helper.RedisConnection = Helper.RedisConnection ?? StackExchange.Redis.ConnectionMultiplexer.Connect(connectionString);
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
}