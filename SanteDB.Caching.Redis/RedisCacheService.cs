﻿/*
 * Portions Copyright 2019-2020, Fyfe Software Inc. and the SanteSuite Contributors (See NOTICE)
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
 * User: fyfej (Justin Fyfe)
 * Date: 2019-11-27
 */
using SanteDB.Caching.Redis.Configuration;
using SanteDB.Core;
using SanteDB.Core.Diagnostics;
using SanteDB.Core.Interfaces;
using SanteDB.Core.Model;
using SanteDB.Core.Model.Acts;
using SanteDB.Core.Model.Attributes;
using SanteDB.Core.Model.Constants;
using SanteDB.Core.Model.Entities;
using SanteDB.Core.Model.Serialization;
using SanteDB.Core.Services;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Serialization;

namespace SanteDB.Caching.Redis
{
    /// <summary>
    /// Redis memory caching service
    /// </summary>
    [ServiceProvider("REDIS Data Caching Service", Configuration = typeof(RedisConfigurationSection))]
    public class RedisCacheService : IDataCachingService, IDaemonService
    {

        /// <summary>
        /// Gets the service name
        /// </summary>
        public string ServiceName => "REDIS Data Caching Service";

        // Redis trace source
        private Tracer m_tracer = new Tracer(RedisCacheConstants.TraceSourceName);

        // Configuration
        private RedisConfigurationSection m_configuration = ApplicationServiceContext.Current.GetService<IConfigurationManager>().GetSection<RedisConfigurationSection>();

        // Binder
        private ModelSerializationBinder m_binder = new ModelSerializationBinder();

        // Non cached types
        private HashSet<Type> m_nonCached = new HashSet<Type>();

        /// <summary>
        /// Is the service running 
        /// </summary>
        public bool IsRunning
        {
            get
            {
                return RedisConnectionManager.Current.Connection != null;
            }
        }

        // Data was added to the cache
        public event EventHandler<DataCacheEventArgs> Added;
        // Data was removed from the cache
        public event EventHandler<DataCacheEventArgs> Removed;
        // Started 
        public event EventHandler Started;
        // Starting
        public event EventHandler Starting;
        // Stopped
        public event EventHandler Stopped;
        // Stopping
        public event EventHandler Stopping;
        // Data was updated on the cache
        public event EventHandler<DataCacheEventArgs> Updated;


        /// <summary>
        /// Serialize objects
        /// </summary>
        private HashEntry[] SerializeObject(IdentifiedData data)
        {
            XmlSerializer xsz = XmlModelSerializerFactory.Current.CreateSerializer(data.GetType());
            HashEntry[] retVal = new HashEntry[3];
            retVal[0] = new HashEntry("type", data.GetType().AssemblyQualifiedName);
            retVal[1] = new HashEntry("loadState", (int)data.LoadState);
            using (var sw = new StringWriter())
            {
                xsz.Serialize(sw, data);
                retVal[2] = new HashEntry("value", sw.ToString());
            }
            return retVal;
        }

        /// <summary>
        /// Serialize objects
        /// </summary>
        private IdentifiedData DeserializeObject(HashEntry[] data)
        {

            if (data == null || data.Length == 0) return null;

            Type type = Type.GetType(data.FirstOrDefault(o => o.Name == "type").Value);
            LoadState ls = (LoadState)(int)data.FirstOrDefault(o => o.Name == "loadState").Value;
            String value = data.FirstOrDefault(o => o.Name == "value").Value;

            // Find serializer
            XmlSerializer xsz = XmlModelSerializerFactory.Current.CreateSerializer(type);
            using (var sr = new StringReader(value))
            {
                var retVal = xsz.Deserialize(sr) as IdentifiedData;
                retVal.LoadState = ls;
                return retVal;
            }

        }

        /// <summary>
        /// Ensure cache consistency
        /// </summary>
        private void EnsureCacheConsistency(DataCacheEventArgs e, bool remove = false)
        {
            // If someone inserts a relationship directly, we need to unload both the source and target so they are re-loaded 
            if (remove)
            {
                if (e.Object is ActParticipation ptcpt)
                {
                    this.Remove(ptcpt.SourceEntityKey.GetValueOrDefault());
                    this.Remove(ptcpt.PlayerEntityKey.GetValueOrDefault());
                    //MemoryCache.Current.RemoveObject(ptcpt.PlayerEntity?.GetType() ?? typeof(Entity), ptcpt.PlayerEntityKey);
                }
                else if (e.Object is ActRelationship actrel)
                {
                    this.Remove(actrel.SourceEntityKey.GetValueOrDefault());
                    this.Remove(actrel.TargetActKey.GetValueOrDefault());
                }
                else if (e.Object is EntityRelationship entrel)
                {
                    this.Remove(entrel.SourceEntityKey.GetValueOrDefault());
                    this.Remove(entrel.TargetEntityKey.GetValueOrDefault());
                }
            }
        }

        /// <summary>
        /// Add an object to the REDIS cache
        /// </summary>
        /// <remarks>
        /// Serlializes <paramref name="data"/> into XML and then persists the 
        /// result in a configured REDIS cache.
        /// </remarks>
        public void Add(IdentifiedData data)
        {
            try
            {

                // We want to add only those when the connection is present
                if (RedisConnectionManager.Current.Connection == null || data == null || !data.Key.HasValue ||
                    (data as BaseEntityData)?.ObsoletionTime.HasValue == true ||
                    this.m_nonCached.Contains(data.GetType()))
                {
                    this.m_tracer.TraceVerbose("Skipping caching of {0} (OBS:{1}, NCC:{2})",
                        data, (data as BaseEntityData)?.ObsoletionTime.HasValue == true, this.m_nonCached.Contains(data.GetType()));
                    return;
                }

                // Only add data which is an entity, act, or relationship
                //if (data is Act || data is Entity || data is ActRelationship || data is ActParticipation || data is EntityRelationship || data is Concept)
                //{
                // Add

                var redisDb = RedisConnectionManager.Current.Connection.GetDatabase(RedisCacheConstants.CacheDatabaseId);
                
                redisDb.HashSet(data.Key.Value.ToString(), this.SerializeObject(data), CommandFlags.FireAndForget);
                redisDb.KeyExpire(data.Key.Value.ToString(), this.m_configuration.TTL, CommandFlags.FireAndForget);

                this.EnsureCacheConsistency(new DataCacheEventArgs(data));
                if (this.m_configuration.PublishChanges)
                {
                    var existing = redisDb.KeyExists(data.Key.Value.ToString());
#if DEBUG
                    this.m_tracer.TraceVerbose("HashSet {0} (EXIST: {1}; @: {2})", data, existing, new System.Diagnostics.StackTrace(true).GetFrame(1));
#endif

                    if (existing)
                        RedisConnectionManager.Current.Connection.GetSubscriber().Publish("oiz.events", $"PUT http://{Environment.MachineName}/cache/{data.Key.Value}");
                    else
                        RedisConnectionManager.Current.Connection.GetSubscriber().Publish("oiz.events", $"POST http://{Environment.MachineName}/cache/{data.Key.Value}");
                    //}
                }
            }
            catch (Exception e)
            {
                this.m_tracer.TraceError("REDIS CACHE ERROR (CACHING SKIPPED): {0}", e);
            }
        }

        /// <summary>
        /// Get a cache item
        /// </summary>
        public object GetCacheItem(Guid key)
        {
            try
            {
                // We want to add
                if (RedisConnectionManager.Current.Connection == null)
                    return null;

                // Add
                var redisDb = RedisConnectionManager.Current.Connection.GetDatabase(RedisCacheConstants.CacheDatabaseId);
                redisDb.KeyExpire(key.ToString(), this.m_configuration.TTL, CommandFlags.FireAndForget);
                return this.DeserializeObject(redisDb.HashGetAll(key.ToString()));
            }
            catch (Exception e)
            {
                this.m_tracer.TraceWarning("REDIS CACHE ERROR (FETCHING SKIPPED): {0}", e);
                return null;
            }

        }

        /// <summary>
        /// Get cache item of type
        /// </summary>
        public TData GetCacheItem<TData>(Guid key) where TData : IdentifiedData
        {
            var retVal = this.GetCacheItem(key);
            if (retVal is TData)
                return (TData)retVal;
            else return null;
        }

        /// <summary>
        /// Remove a hash key item
        /// </summary>
        public void Remove(Guid key)
        {
            // We want to add
            if (RedisConnectionManager.Current.Connection == null)
                return;
            // Add
            var existing = this.GetCacheItem(key);
            var redisDb = RedisConnectionManager.Current.Connection.GetDatabase(RedisCacheConstants.CacheDatabaseId);
            redisDb.KeyDelete(key.ToString(), CommandFlags.FireAndForget);
            this.EnsureCacheConsistency(new DataCacheEventArgs(existing), true);

            RedisConnectionManager.Current.Connection.GetSubscriber().Publish("oiz.events", $"DELETE http://{Environment.MachineName}/cache/{key}");
        }

        /// <summary>
        /// Start the connection manager
        /// </summary>
        public bool Start()
        {
            try
            {
                this.Starting?.Invoke(this, EventArgs.Empty);

                this.m_tracer.TraceInfo("Starting REDIS cache service to hosts {0}...", String.Join(";", this.m_configuration.Servers));

                // Look for non-cached types
                foreach (var itm in typeof(IdentifiedData).Assembly.GetTypes().Where(o => o.GetCustomAttribute<NonCachedAttribute>() != null || o.GetCustomAttribute<XmlRootAttribute>() == null))
                    this.m_nonCached.Add(itm);

                // Subscribe to SanteDB events
                if(this.m_configuration.PublishChanges)
                    RedisConnectionManager.Current.Subscriber.Subscribe("oiz.events", (channel, message) =>
                    {

                        this.m_tracer.TraceVerbose("Received event {0} on {1}", message, channel);

                        var messageParts = ((string)message).Split(' ');
                        var verb = messageParts[0];
                        var uri = new Uri(messageParts[1]);

                        string resource = uri.AbsolutePath.Replace("hdsi/", ""),
                            id = uri.AbsolutePath.Substring(uri.AbsolutePath.LastIndexOf("/") + 1);

                        switch (verb.ToLower())
                        {
                            case "post":
                                this.Added?.Invoke(this, new DataCacheEventArgs(this.GetCacheItem(Guid.Parse(id))));
                                break;
                            case "put":
                                this.Updated?.Invoke(this, new DataCacheEventArgs(this.GetCacheItem(Guid.Parse(id))));
                                break;
                            case "delete":
                                this.Removed?.Invoke(this, new DataCacheEventArgs(id));
                                break;
                        }
                    });

                this.Started?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch (Exception e)
            {
                this.m_tracer.TraceError("Error starting REDIS caching, will switch to no-caching : {0}", e);
                ApplicationServiceContext.Current.GetService<IServiceManager>().RemoveServiceProvider(typeof(RedisCacheService));
                ApplicationServiceContext.Current.GetService<IServiceManager>().RemoveServiceProvider(typeof(IDataCachingService));
                return false;
            }
        }

        /// <summary>
        /// Stop the connection
        /// </summary>
        public bool Stop()
        {
            this.Stopping?.Invoke(this, EventArgs.Empty);
            RedisConnectionManager.Current.Dispose();
            this.Stopped?.Invoke(this, EventArgs.Empty);
            return true;
        }

        /// <summary>
        /// Clear the cache
        /// </summary>
        public void Clear()
        {
            try
            {
                RedisConnectionManager.Current.Connection.GetServer(this.m_configuration.Servers.First()).FlushAllDatabases();
            }
            catch(Exception e)
            {
                this.m_tracer.TraceWarning("Could not flush REDIS cache: {0}", e);
            }
        }

        /// <summary>
        /// Size of the database
        /// </summary>
        public long Size
        {
            get
            {
                return RedisConnectionManager.Current.Connection.GetServer(this.m_configuration.Servers.First()).DatabaseSize();
            }
        }
    }
}
