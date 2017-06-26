# Components.Caching
A wrapper for working with .NET cache, using in-process memory cache and distributed cache (memcached - via Enyim Memcached)
- Default mode: Distributed cache (memcached)
- Session State Provider with memcached (prefix is provider name - in app.config/web.config)
- Async version of Set/Get/Remove methods

Enyim Memcached: https://github.com/enyim/EnyimMemcached
