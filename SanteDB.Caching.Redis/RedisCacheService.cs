/*
 * Copyright (C) 2021 - 2021, SanteSuite Inc. and the SanteSuite Contributors (See NOTICE.md for full copyright notices)
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
 * Date: 2021-8-5
 */

using SanteDB.Caching.Redis.Configuration;
using SanteDB.Core;
using SanteDB.Core.Diagnostics;
using SanteDB.Core.Model;
using SanteDB.Core.Model.Attributes;
using SanteDB.Core.Model.Interfaces;
using SanteDB.Core.Model.Serialization;
using SanteDB.Core.Services;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Xml.Serialization;

namespace SanteDB.Caching.Redis
{
    /// <summary>
    /// An implementation of the <see cref="IDataCachingService"/> which uses REDIS
    /// </summary>
    /// <remarks>
    /// <para>This implementation of the caching service uses the XMLSerializer in .NET to serialize any object passed in
    /// via the <see cref="Add(IdentifiedData)"/> method. The data is then compressed (if configured) and sent to the
    /// configured REDIS server.</para>
    /// <para>The use of the .NET XML serializer over the Newtonsoft JSON serializer for caching was chosen since the
    /// serializer operates on streams (saves string conversions) and pre-compiles the serialization classes on .NET Framework implementations
    /// (Mono implementations use relfection)</para>
    /// <para>The caching data is stored in database 1 of the REDIS server.</para>
    /// </remarks>
    [ServiceProvider("REDIS Data Caching Service", Configuration = typeof(RedisConfigurationSection))]
    public class RedisCacheService : IDataCachingService, IDaemonService
    {
        // The field in the REDIS cache for value
        private const string FIELD_VALUE = "value";

        // The field in the REDIS cache for state
        private const string FIELD_STATE = "state";

        // The field in the REDIS cache for type
        private const string FIELD_TYPE = "type";

        /// <inheritdoc/>
        public string ServiceName => "REDIS Data Caching Service";

        // Redis trace source
        private Tracer m_tracer = new Tracer(RedisCacheConstants.TraceSourceName);

        // Configuration
        private RedisConfigurationSection m_configuration = ApplicationServiceContext.Current.GetService<IConfigurationManager>().GetSection<RedisConfigurationSection>();

        // Binder
        private ModelSerializationBinder m_binder = new ModelSerializationBinder();

        // Non cached types - used to ignore cache requests
        private HashSet<Type> m_nonCached = new HashSet<Type>();

        /// <inheritdoc/>
        public bool IsRunning
        {
            get
            {
                return RedisConnectionManager.Current.Connection != null;
            }
        }

        /// <inheritdoc/>
        public event EventHandler<DataCacheEventArgs> Added;

        /// <inheritdoc/>
        public event EventHandler<DataCacheEventArgs> Removed;

        /// <inheritdoc/>
        public event EventHandler Started;

        /// <inheritdoc/>
        public event EventHandler Starting;

        /// <inheritdoc/>
        public event EventHandler Stopped;

        /// <inheritdoc/>
        public event EventHandler Stopping;

        /// <inheritdoc/>
        public event EventHandler<DataCacheEventArgs> Updated;

        /// <summary>
        /// Serialize objects from <paramref name="data"/> to a REDIS hash entry
        /// </summary>
        /// <param name="data">The data to be serialized</param>
        /// <returns>The REDIS hash entries which represent the <paramref name="data"/> to the REDIS server</returns>
        private HashEntry[] SerializeObject(IdentifiedData data)
        {
            data.BatchOperation = Core.Model.DataTypes.BatchOperationType.Auto;
            XmlSerializer xsz = XmlModelSerializerFactory.Current.CreateSerializer(data.GetType());
            HashEntry[] retVal = new HashEntry[3];
            retVal[0] = new HashEntry(FIELD_TYPE, data.GetType().AssemblyQualifiedName);
            retVal[1] = new HashEntry(FIELD_STATE, (int)data.LoadState);

            using (var ms = new MemoryStream())
            {
                if (this.m_configuration.Compress)
                {
                    using (var gzs = new GZipStream(ms, CompressionLevel.Fastest))
                        xsz.Serialize(gzs, data);
                }
                else
                {
                    xsz.Serialize(ms, data);
                }
                retVal[2] = new HashEntry(FIELD_VALUE, ms.ToArray());
            }
            return retVal;
        }

        /// <summary>
        /// Parse the REDIS hash values and de-serialize the data to an <see cref="IdentifiedData"/> instance
        /// </summary>
        /// <param name="rvState">The state hash value (full load, partial load, etc.)</param>
        /// <param name="rvType">The type of data which is being de-serialized</param>
        /// <param name="rvValue">The value (XML) data</param>
        private IdentifiedData DeserializeObject(RedisValue rvType, RedisValue rvState, RedisValue rvValue)
        {
            if (!rvValue.HasValue || !rvType.HasValue) return null;

            Type type = Type.GetType((String)rvType);
            LoadState ls = (LoadState)(int)rvState;

            // Find serializer
            XmlSerializer xsz = XmlModelSerializerFactory.Current.CreateSerializer(type);
            using (var sr = new MemoryStream((byte[])rvValue))
            {
                IdentifiedData retVal = null;
                if (this.m_configuration.Compress)
                {
                    using (var gzs = new GZipStream(sr, CompressionMode.Decompress))
                    {
                        retVal = xsz.Deserialize(gzs) as IdentifiedData;
                    }
                }
                else
                {
                    retVal = xsz.Deserialize(sr) as IdentifiedData;
                }
                retVal.LoadState = ls;
                return retVal;
            }
        }

        /// <inheritdoc/>
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

                // HACK: Remove all non-transient tags since the persistence layer doesn't persist them
                if (data is ITaggable taggable)
                {
                    // TODO: Put this as a constant
                    // Don't cache generated data
                    if (taggable.GetTag("$generated") == "true")
                    {
                        return;
                    }

                    foreach (var tag in taggable.Tags.Where(o => o.TagKey.StartsWith("$")).ToArray())
                    {
                        taggable.RemoveTag(tag.TagKey);
                    }
                }
                // Only add data which is an entity, act, or relationship
                //if (data is Act || data is Entity || data is ActRelationship || data is ActParticipation || data is EntityRelationship || data is Concept)
                //{
                // Add

                var redisDb = RedisConnectionManager.Current.Connection.GetDatabase(RedisCacheConstants.CacheDatabaseId);

                redisDb.HashSet(data.Key.Value.ToString(), this.SerializeObject(data), CommandFlags.FireAndForget);
                redisDb.KeyExpire(data.Key.Value.ToString(), this.m_configuration.TTL, CommandFlags.FireAndForget);

                // If this is a relationship class we remove the source entity from the cache
                if (data is ITargetedAssociation targetedAssociation)
                {
                    this.Remove(targetedAssociation.SourceEntityKey.GetValueOrDefault());
                    this.Remove(targetedAssociation.TargetEntityKey.GetValueOrDefault());
                }
                else if (data is ISimpleAssociation simpleAssociation)
                {
                    this.Remove(simpleAssociation.SourceEntityKey.GetValueOrDefault());
                }

                //this.EnsureCacheConsistency(new DataCacheEventArgs(data));
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

        /// <inheritdoc/>
        public IdentifiedData GetCacheItem(Guid key)
        {
            try
            {
                // We want to add
                if (RedisConnectionManager.Current.Connection == null)
                    return null;

                // Add
                var redisDb = RedisConnectionManager.Current.Connection.GetDatabase(RedisCacheConstants.CacheDatabaseId);
                redisDb.KeyExpire(key.ToString(), this.m_configuration.TTL, CommandFlags.FireAndForget);
                var hdata = redisDb.HashGetAll(key.ToString()).ToDictionary(o => (String)o.Name, o => o.Value);
                if (hdata.Count == 0)
                    return null;
                return this.DeserializeObject(hdata[FIELD_TYPE], hdata[FIELD_STATE], hdata[FIELD_VALUE]);
            }
            catch (Exception e)
            {
                this.m_tracer.TraceWarning("REDIS CACHE ERROR (FETCHING SKIPPED): {0}", e);
                RedisConnectionManager.Current.Dispose();

                return null;
            }
        }

        /// <inheritdoc/>
        public TData GetCacheItem<TData>(Guid key) where TData : IdentifiedData
        {
            var retVal = this.GetCacheItem(key);
            if (retVal is TData)
                return (TData)retVal;
            else return null;
        }

        /// <inheritdoc/>
        public void Remove(Guid key)
        {
            // We want to add
            if (RedisConnectionManager.Current.Connection == null)
                return;
            // Add
            var existing = this.GetCacheItem(key);
            this.Remove(existing as IdentifiedData);
        }

        /// <inheritdoc/>
        public void Remove(IdentifiedData entry)
        {
            if (entry == null) return;

            var redisDb = RedisConnectionManager.Current.Connection.GetDatabase(RedisCacheConstants.CacheDatabaseId);
            redisDb.KeyDelete(entry.Key.ToString(), CommandFlags.FireAndForget);
            //this.EnsureCacheConsistency(new DataCacheEventArgs(existing), true);
            if (entry is ISimpleAssociation sa)
            {
                this.Remove(sa.SourceEntityKey.GetValueOrDefault());
                if (sa is ITargetedAssociation ta)
                {
                    this.Remove(ta.TargetEntityKey.GetValueOrDefault());
                }
            }
            RedisConnectionManager.Current.Connection.GetSubscriber().Publish("oiz.events", $"DELETE http://{Environment.MachineName}/cache/{entry.Key}");
        }

        /// <inheritdoc/>
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
                if (this.m_configuration.PublishChanges)
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

        /// <inheritdoc/>
        public bool Stop()
        {
            this.Stopping?.Invoke(this, EventArgs.Empty);
            RedisConnectionManager.Current.Dispose();
            this.Stopped?.Invoke(this, EventArgs.Empty);
            return true;
        }

        /// <inheritdoc/>
        public void Clear()
        {
            try
            {
                RedisConnectionManager.Current.Connection.GetServer(this.m_configuration.Servers.First()).FlushAllDatabases();
            }
            catch (Exception e)
            {
                this.m_tracer.TraceWarning("Could not flush REDIS cache: {0}", e);
            }
        }

        /// <inheritdoc/>
        public long Size
        {
            get
            {
                return RedisConnectionManager.Current.Connection.GetServer(this.m_configuration.Servers.First()).DatabaseSize();
            }
        }
    }
}