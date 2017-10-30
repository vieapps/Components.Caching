#region Related components
using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Configuration;
#endregion

namespace net.vieapps.Components.Caching
{
	internal static class Helper
	{

		#region Split & Serialize
		internal static List<byte[]> Split(byte[] data, int sizeOfOneFragment)
		{
			var fragments = new List<byte[]>();
			int index = 0, length = data.Length;
			while (index < data.Length)
			{
				var size = sizeOfOneFragment > length
					? length
					: sizeOfOneFragment;

				var fragment = new byte[size];
				Array.Copy(data, index, fragment, 0, size);
				fragments.Add(fragment);

				index += size;
				length -= size;
			}
			return fragments;
		}

		internal static byte[] Serialize(object @object)
		{
			return (new Enyim.Caching.Memcached.DefaultTranscoder()).SerializeObject(@object).Array;
		}

		internal static object Deserialize(byte[] data)
		{
			return (new Enyim.Caching.Memcached.DefaultTranscoder()).DeserializeObject(new ArraySegment<byte>(data));
		}

		internal static T Clone<T>(T @object)
		{
			return (T)Helper.Deserialize(Helper.Serialize(@object));
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
				content += info + "- " + (ex.Message != null ? ex.Message : "No error message") + " [" + ex.GetType().ToString() + "]" + "\r\n"
					+ info + "- " + (ex.StackTrace != null ? ex.StackTrace : "No stack trace");

				ex = ex.InnerException;
				var counter = 1;
				while (ex != null)
				{
					content += info + "- Inner (" + counter.ToString() + "): ----------------------------------" + "\r\n"
						+ info + "- " + (ex.Message != null ? ex.Message : "No error message") + " [" + ex.GetType().ToString() + "]" + "\r\n"
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