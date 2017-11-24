#region Related components
using System;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

using net.vieapps.Components.Caching;
#endregion

namespace Microsoft.Extensions.DependencyInjection
{
	public static partial class ServiceCollectionExtensions
	{
		/// <summary>
		/// Adds the service of <see cref="net.vieapps.Components.Caching.Cache">VIEApps Cache</see> into the collection of services for using with dependency injection
		/// </summary>
		/// <param name="services"></param>
		/// <param name="setupAction">The action to bind options of 'Cache' section from appsettings.json file</param>
		/// <param name="addInstanceOfIDistributedCache">true to add the cache service as an instance of IDistributedCache</param>
		/// <returns></returns>
		public static IServiceCollection AddCache(this IServiceCollection services, Action<CacheOptions> setupAction, bool addInstanceOfIDistributedCache = true)
		{
			if (setupAction == null)
				throw new ArgumentNullException(nameof(setupAction));

			services.AddOptions();
			services.Configure(setupAction);
			services.Add(ServiceDescriptor.Singleton<CacheConfiguration, CacheConfiguration>());
			services.Add(ServiceDescriptor.Singleton<Cache, Cache>(s => Cache.GetInstance(s)));
			if (addInstanceOfIDistributedCache)
				services.Add(ServiceDescriptor.Singleton<IDistributedCache, Cache>(s => Cache.GetInstance(s)));

			return services;
		}
	}
}

namespace Microsoft.AspNetCore.Builder
{
	public static partial class ApplicationBuilderExtensions
	{
		/// <summary>
		/// Calls to use the service of <see cref="net.vieapps.Components.Caching.Cache">VIEApps Cache</see>
		/// </summary>
		/// <param name="appBuilder"></param>
		/// <returns></returns>
		public static IApplicationBuilder UseCache(this IApplicationBuilder appBuilder)
		{
			try
			{
				appBuilder.ApplicationServices.GetService<ILogger<Cache>>().LogInformation($"VIEApps Cache is {(appBuilder.ApplicationServices.GetService<Cache>() != null ? "" : "not-")}started");
			}
			catch (Exception ex)
			{
				appBuilder.ApplicationServices.GetService<ILogger<Cache>>().LogError(ex, "VIEApps Cache is failed to start");
			}
			return appBuilder;
		}
	}
}
