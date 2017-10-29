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
	/// <summary>
	/// Helper methods for working with distributed cache
	/// </summary>
	public static class Helper
	{

		#region Merge & Split
		internal static HashSet<string> Merge(params HashSet<string>[] sets)
		{
			return Helper.Merge(true, sets);
		}

		internal static HashSet<string> Merge(bool doClone, params HashSet<string>[] sets)
		{
			if (sets == null || sets.Length < 1)
				return null;

			else if (sets.Length < 2)
				return doClone
					? Helper.Clone(sets[0])
					: sets[0];

			var @object = doClone
				? Helper.Clone(sets[0])
				: sets[0];

			for (int index = 1; index < sets.Length; index++)
			{
				var set = doClone
					? Helper.Clone(sets[index])
					: sets[index];

				if (set == null || set.Count < 1)
					continue;

				foreach (var @string in set)
					if (!string.IsNullOrWhiteSpace(@string) && !@object.Contains(@string))
						@object.Add(@string);
			}

			return @object;
		}

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
		#endregion

		#region Serialize/Deserialize
		internal static byte[] SerializeAsBinary(object @object)
		{
			return (new Enyim.Caching.Memcached.DefaultTranscoder()).SerializeObject(@object).Array;
		}

		internal static object DeserializeFromBinary(byte[] data)
		{
			return (new Enyim.Caching.Memcached.DefaultTranscoder()).DeserializeObject(new ArraySegment<byte>(data));
		}

		internal static T Clone<T>(T @object)
		{
			return (T)Helper.DeserializeFromBinary(Helper.SerializeAsBinary(@object));
		}
		#endregion

		#region Task executions
		internal static Task ExecuteTask(Action action, CancellationToken cancellationToken = default(CancellationToken))
		{
			var tcs = new TaskCompletionSource<object>();
			ThreadPool.QueueUserWorkItem(_ =>
			{
				if (cancellationToken == null)
					cancellationToken = default(CancellationToken);
				cancellationToken.Register(() =>
				{
					tcs.SetCanceled();
					return;
				});

				try
				{
					action?.Invoke();
					tcs.SetResult(null);
				}
				catch (Exception ex)
				{
					tcs.SetException(ex);
				}
			});
			return tcs.Task;
		}

		internal static Task<TResult> ExecuteTask<TResult>(Func<TResult> func, CancellationToken cancellationToken = default(CancellationToken))
		{
			var tcs = new TaskCompletionSource<TResult>();
			ThreadPool.QueueUserWorkItem(_ =>
			{
				if (cancellationToken == null)
					cancellationToken = default(CancellationToken);
				cancellationToken.Register(() =>
				{
					tcs.SetCanceled();
					return;
				});

				try
				{
					var result = func != null
						? func.Invoke()
						: default(TResult);
					tcs.SetResult(result);
				}
				catch (Exception ex)
				{
					tcs.SetException(ex);
				}
			});
			return tcs.Task;
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