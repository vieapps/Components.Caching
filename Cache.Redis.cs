#region Related components
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
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
	public sealed class Redis
	{
		/// <summary>
		/// Create new an instance of redis
		/// </summary>
		/// <param name="name">The string that presents name of isolated region of the cache</param>
		/// <param name="expirationTime">The number that presents times (in minutes) for caching an item</param>
		/// <param name="updateKeys">true to active update keys of the region (to clear or using with other purpose further)</param>
		public Redis(string name, int expirationTime, bool updateKeys)
		{
			// region name
			this._name = string.IsNullOrWhiteSpace(name)
				? "VIEApps-NGX-Cache"
				: System.Text.RegularExpressions.Regex.Replace(name, "[^0-9a-zA-Z:-]+", "");

			// expiration time
			this._expirationTime = expirationTime > 0
				? expirationTime
				: Helper.ExpirationTime;

			// update keys
			if (updateKeys)
			{
				this._updateKeys = true;
				this._addedKeys = new HashSet<string>();
				this._removedKeys = new HashSet<string>();
			}
		}

		#region Attributes
		string _name;
		int _expirationTime;
		bool _updateKeys, _isUpdating = false;
		HashSet<string> _addedKeys, _removedKeys;
		ReaderWriterLockSlim _locker = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
		#endregion

	}
}