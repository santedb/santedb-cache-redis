/*
 * Copyright (C) 2021 - 2023, SanteSuite Inc. and the SanteSuite Contributors (See NOTICE.md for full copyright notices)
 * Copyright (C) 2019 - 2021, Fyfe Software Inc. and the SanteSuite Contributors
 * Portions Copyright (C) 2015-2018 Mohawk College of Applied Arts and Technology
 * 
 * Licensed under the Apache License, Version 2.0 (the "License"); you 
 * may not use this file except in compliance with the License. You may 
 * obtain a copy of the License at 
 * 
 * http://www.apache.org/licenses/LICENSE-2.0 
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS, WITHOUT
 * WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the 
 * License for the specific language governing permissions and limitations under 
 * the License.
 * 
 * User: fyfej
 * Date: 2023-3-10
 */
using Newtonsoft.Json;
using SanteDB.Caching.Redis.Configuration;
using SanteDB.Core;
using SanteDB.Core.Diagnostics;
using SanteDB.Core.Services;
using StackExchange.Redis;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace SanteDB.Caching.Redis
{
    /// <summary>
    /// An implementation of the <see cref="IAdhocCacheService"/> which uses REDIS as the cache provider
    /// </summary>
    /// <remarks>
    /// <para>This implementation of the REDIS ad-hoc cache provider serializes any data passed via <see cref="Add{T}(string, T, TimeSpan?)"/> to a JSON representation, then
    /// compresses (optional) the data and stores it in REDIS as a simple string</para>
    /// <para>The data is stored in database 3 of the REDIS server</para>
    /// </remarks>
    [ExcludeFromCodeCoverage] // Unit testing on REDIS is not possible in unit tests
    public class RedisAdhocCache : IAdhocCacheService, IDaemonService
    {
        /// <inheritdoc/>
        public bool IsRunning => this.m_configuration != null;

        /// <inheritdoc/>
        public string ServiceName => "REDIS Ad-Hoc Caching Service";

        // Redis trace source
        private readonly Tracer m_tracer = new Tracer(RedisCacheConstants.TraceSourceName);

        // Configuration
        private RedisConfigurationSection m_configuration = ApplicationServiceContext.Current.GetService<IConfigurationManager>().GetSection<RedisConfigurationSection>();

        /// <inheritdoc/>
        public event EventHandler Starting;

        /// <inheritdoc/>
        public event EventHandler Started;

        /// <inheritdoc/>
        public event EventHandler Stopping;

        /// <inheritdoc/>
        public event EventHandler Stopped;

        /// <inheritdoc/>
        public void Add<T>(string key, T value, TimeSpan? timeout = null)
        {
            try
            {
                var db = RedisConnectionManager.Current.Connection.GetDatabase(RedisCacheConstants.AdhocCacheDatabaseId);
                db.StringSet(key, JsonConvert.SerializeObject(value), expiry: timeout ?? this.m_configuration.TTL, flags: CommandFlags.FireAndForget);
            }
            catch (Exception e)
            {
                this.m_tracer.TraceError("Error adding {0} to cache {1}", value, e.Message);
                //throw new Exception($"Error adding {value} to cache", e);
            }
        }


        /// <inheritdoc/>
        public T Get<T>(string key)
        {
            if (this.TryGet<T>(key, out var t)) {
                return t;
            }
            else
            {
                return default(T);
            }
        }

        /// <inheritdoc/>
        public bool TryGet<T>(string key, out T value)
        {
            try
            {
                var db = RedisConnectionManager.Current.Connection?.GetDatabase(RedisCacheConstants.AdhocCacheDatabaseId);
                var str = db?.StringGet(key);
                if (!String.IsNullOrEmpty(str))
                {
                    value = JsonConvert.DeserializeObject<T>(str);
                }
                else
                {
                    value = default(T);
                }
                return db?.KeyExists(key) == true;
            }
            catch (Exception e)
            {
                this.m_tracer.TraceError("Error fetch {0} from cache {1}", key, e.Message);
                //throw new Exception($"Error fetching {key} ({typeof(T).FullName}) from cache", e);
                value = default(T);
                return false;
            }
        }

        /// <inheritdoc/>
        public bool Start()
        {
            try
            {
                this.Starting?.Invoke(this, EventArgs.Empty);

                this.m_tracer.TraceInfo("Starting REDIS ad-hoc cache service to hosts {0}...", String.Join(";", this.m_configuration.Servers));
                this.m_tracer.TraceInfo("Using shared REDIS cache {0}", RedisConnectionManager.Current.Connection);

                this.Started?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch (Exception e)
            {
                this.m_tracer.TraceError("Error starting REDIS query persistence, will switch to query persister : {0}", e);
                ApplicationServiceContext.Current.GetService<IServiceManager>().RemoveServiceProvider(typeof(RedisAdhocCache));
                ApplicationServiceContext.Current.GetService<IServiceManager>().RemoveServiceProvider(typeof(IDataCachingService));
                return false;
            }
        }

        /// <inheritdoc/>
        public bool Stop()
        {
            this.Stopping?.Invoke(this, EventArgs.Empty);
            RedisConnectionManager.Current.Dispose();
            this.Stopped?.Invoke(this, EventArgs.Empty);
            return true;
        }

        /// <inheritdoc/>
        public bool Remove(string key)
        {
            try
            {
                var db = RedisConnectionManager.Current.Connection?.GetDatabase(RedisCacheConstants.AdhocCacheDatabaseId);
                db?.KeyDelete(key);
                return true;
            }
            catch (Exception e)
            {
                this.m_tracer.TraceError("Error removing entity from ad-hoc cache : {0}", e);
                return false;
            }
        }

        /// <inheritdoc/>
        public bool Exists(string key)
        {
            try
            {
                var db = RedisConnectionManager.Current.Connection?.GetDatabase(RedisCacheConstants.AdhocCacheDatabaseId);
                return db?.KeyExists(key) == true;
            }
            catch (Exception e)
            {
                this.m_tracer.TraceError("Error exists {0} from cache {1}", key, e.Message);
                //throw new Exception($"Error fetching {key} ({typeof(T).FullName}) from cache", e);
                return false;
            }
        }

        /// <summary>
        /// Remove all keys with pattern matching <paramref name="pattern"/>
        /// </summary>
        public void RemoveAll(string pattern)
        {
            try
            {
                var regex = new Regex(pattern, RegexOptions.Compiled);
                var db = RedisConnectionManager.Current.Connection?.GetDatabase(RedisCacheConstants.AdhocCacheDatabaseId);
                var server = RedisConnectionManager.Current.Connection?.GetServers()[0];
                foreach(var key in server.Keys(database: RedisCacheConstants.AdhocCacheDatabaseId))
                {
                    if (regex.IsMatch(key))
                    {
                        db.KeyDelete(key);
                    }
                }
            }
            catch (Exception)
            {
            }
        }
    }
}