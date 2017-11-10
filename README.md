# VIEApps.Components.Caching
A .NET Standard 2.0 wrapper library for working with distributed cache
- Ready with .NET Core 2.0 and .NET Framework 4.6.1 (and higher)
- Supported: Memcached & Redis
## Nuget
- Package ID: VIEApps.Components.Caching
- Details: https://www.nuget.org/packages/VIEApps.Components.Caching
## Dependencies
- Memcached: [VIEApps.Enyim.Caching](https://github.com/vieapps/Enyim.Caching)
- Redis: [StackExchange.Redis](https://github.com/StackExchange/StackExchange.Redis)
## Configuration
Add the configuration settings into your app.config/web.config file
```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
	<configSections>
		<section name="memcached" type="Enyim.Caching.Configuration.MemcachedClientConfigurationSectionHandler, Enyim.Caching" />
		<section name="redis" type="net.vieapps.Components.Caching.RedisClientConfigurationSectionHandler, VIEApps.Components.Caching" />
	</configSections>
	<memcached>
		<servers>
			<add address="127.0.0.1" port="11211" />
			<add address="192.168.1.2" port="11211" />
		</servers>
		<socketPool minPoolSize="10" maxPoolSize="250" deadTimeout="00:01:00" connectionTimeout="00:00:05" receiveTimeout="00:00:01" />
	</memcached>
	<redis>
		<servers>
			<add address="127.0.0.1" port="6379" />
			<add address="192.168.1.2" port="6379" />
		</servers>
		<options allowAdmin="false" version="4.0" connectTimeout="4000" syncTimeout="2000" />
	</redis>
</configuration>
```
## Example usage
```cs
public class CreativeService
{	
	using net.vieapps.Components.Caching;

	private Cache _memcached;
	private Cache _redis;

	public CreativeService()
	{
		this._memcached = new Cache("Region-Name", "memcached");
		this._redis = new Cache("Region-Name", "redis");
	}

	public async Task<IList<CreativeDTO>> GetMemcachedCreatives(string unitName)
	{
		return await this._memcached.GetAsync<IList<CreativeDTO>>($"creatives_{unitName}");
	}

	public async Task<IList<CreativeDTO>> GetRedisCreatives(string unitName)
	{
		return await this._redis.GetAsync<IList<CreativeDTO>>($"creatives_{unitName}");
	}
}
```
## Constructors
```cs
/// <summary>
/// Create new an instance of  distributed cache with isolated region
/// </summary>
/// <param name="name">The string that presents name of isolated region</param>
/// <param name="expirationTime">Time for caching an item (in minutes)</param>
/// <param name="storeKeys">true to active store keys of the region (to clear or using with other purpose further)</param>
public Cache(string name = null, int expirationTime = 0, bool storeKeys = false);

/// <summary>
/// Create new an instance of  distributed cache with isolated region
/// </summary>
/// <param name="name">The string that presents name of isolated region</param>
/// <param name="provider">The string that presents the caching provider ('memcached' or 'redis') - the default provider is 'memcached')</param>
public Cache(string name, string provider);

/// <summary>
/// Create new an instance of  distributed cache with isolated region
/// </summary>
/// <param name="name">The string that presents name of isolated region</param>
/// <param name="expirationTime">Time for caching an item (in minutes)</param>
/// <param name="provider">The string that presents the caching provider ('memcached' or 'redis') - the default provider is 'memcached')</param>
public Cache(string name, int expirationTime, string provider);

/// <summary>
/// Create new an instance of  distributed cache with isolated region
/// </summary>
/// <param name="name">The string that presents name of isolated region</param>
/// <param name="expirationTime">Time for caching an item (in minutes)</param>
/// <param name="storeKeys">true to active store keys of the region (to clear or using with other purpose further)</param>
/// <param name="provider">The string that presents the caching provider ('memcached' or 'redis') - the default provider is 'memcached')</param>
public Cache(string name, int expirationTime, bool storeKeys, string provider);
```
## Listing of all methods
```cs
bool Set(string key, object value, int expirationTime = 0);
bool Set(string key, object value, TimeSpan validFor);
bool Set(string key, object value, DateTime expiresAt);
Task<bool> SetAsync(string key, object value, int expirationTime = 0);
Task<bool> SetAsync(string key, object value, TimeSpan validFor);
Task<bool> SetAsync(string key, object value, DateTime expiresAt);
void Set(IDictionary<string, object> items, string keyPrefix = null, int expirationTime = 0);
void Set<T>(IDictionary<string, T> items, string keyPrefix = null, int expirationTime = 0);
Task SetAsync(IDictionary<string, object> items, string keyPrefix = null, int expirationTime = 0);
Task SetAsync<T>(IDictionary<string, T> items, string keyPrefix = null, int expirationTime = 0);
bool SetFragments(string key, List<byte[]> fragments, int expirationTime = 0);
Task<bool> SetFragmentsAsync(string key, List<byte[]> fragments, int expirationTime = 0);
bool SetAsFragments(string key, object value, int expirationTime = 0);
Task<bool> SetAsFragmentsAsync(string key, object value, int expirationTime = 0);
bool Add(string key, object value, int expirationTime = 0);
bool Add(string key, object value, TimeSpan validFor);
bool Add(string key, object value, DateTime expiresAt);
Task<bool> AddAsync(string key, object value, int expirationTime = 0);
Task<bool> AddAsync(string key, object value, TimeSpan validFor);
Task<bool> AddAsync(string key, object value, DateTime expiresAt);
bool Replace(string key, object value, int expirationTime = 0);
bool Replace(string key, object value, TimeSpan validFor);
bool Replace(string key, object value, DateTime expiresAt);
Task<bool> ReplaceAsync(string key, object value, int expirationTime = 0);
Task<bool> ReplaceAsync(string key, object value, TimeSpan validFor);
Task<bool> ReplaceAsync(string key, object value, DateTime expiresAt);
object Get(string key);
T Get<T>(string key);
Task<object> GetAsync(string key);
Task<T> GetAsync<T>(string key);
IDictionary<string, object> Get(IEnumerable<string> keys);
Task<IDictionary<string, object>> GetAsync(IEnumerable<string> keys);
IDictionary<string, T> Get<T>(IEnumerable<string> keys);
Task<IDictionary<string, T>> GetAsync<T>(IEnumerable<string> keys);
Tuple<int, int> GetFragments(string key);
Task<Tuple<int, int>> GetFragmentsAsync(string key);
List<byte[]> GetAsFragments(string key, List<int> indexes);
Task<List<byte[]>> GetAsFragmentsAsync(string key, List<int> indexes);
List<byte[]> GetAsFragments(string key, params int[] indexes);
Task<List<byte[]>> GetAsFragmentsAsync(string key, params int[] indexes);
bool Remove(string key);
Task<bool> RemoveAsync(string key);
void Remove(IEnumerable<string> keys, string keyPrefix = null);
Task RemoveAsync(IEnumerable<string> keys, string keyPrefix = null);
void RemoveFragments(string key);
Task RemoveFragmentsAsync(string key);
bool Exists(string key);
Task<bool> ExistsAsync(string key);
void Clear();
Task ClearAsync();
HashSet<string> GetKeys();
Task<HashSet<string>> GetKeysAsync();
```
## Session State Provider
- See [VIEApps.Components.Caching.AspNet](https://github.com/vieapps/Components.Caching.AspNet)
