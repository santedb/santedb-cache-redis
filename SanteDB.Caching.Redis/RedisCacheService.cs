/*
 * Copyright (C) 2021 - 2025, SanteSuite Inc. and the SanteSuite Contributors (See NOTICE.md for full copyright notices)
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
 * Date: 2023-6-21
 */
using Newtonsoft.Json;
using SanteDB.Caching.Redis.Configuration;
using SanteDB.Core;
using SanteDB.Core.Diagnostics;
using SanteDB.Core.Model;
using SanteDB.Core.Model.Attributes;
using SanteDB.Core.Model.Collection;
using SanteDB.Core.Model.Constants;
using SanteDB.Core.Model.Interfaces;
using SanteDB.Core.Model.Serialization;
using SanteDB.Core.Services;
using StackExchange.Redis;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Xml;
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
    [ExcludeFromCodeCoverage] // Unit testing on REDIS is not possible in unit tests
    public class RedisCacheService : IDataCachingService, IDaemonService
    {

        private readonly JsonSerializer m_serializer = new JsonSerializer()
        {
            //SerializationBinder = new ModelSerializationBinder(),
            DateFormatHandling = DateFormatHandling.IsoDateFormat,
            Formatting = Newtonsoft.Json.Formatting.None,
            NullValueHandling = NullValueHandling.Include,
            TypeNameHandling = TypeNameHandling.All,
            TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Full
        };

        /// <summary>
        /// Consistent indicator
        /// </summary>
        private struct CacheConsistentIndicator { }

        // PUB/SUB Topic
        private const string PUBSUB_TOPIC = "sdb.events";

        // The field in the REDIS cache for value
        private const string FIELD_VALUE = "value";

        // The field in the REDIS cache for type
        private const string FIELD_LOAD = "load";

        /// <inheritdoc/>
        public string ServiceName => "REDIS Data Caching Service";

        // Redis trace source
        private readonly Tracer m_tracer = new Tracer(RedisCacheConstants.TraceSourceName);

        // Configuration
        private readonly RedisConfigurationSection m_configuration;

        // Binder
        private readonly ModelSerializationBinder m_binder = new ModelSerializationBinder();

        // Non cached types - used to ignore cache requests
        private HashSet<Type> m_nonCached = new HashSet<Type>();

        /// <summary>
        /// DI constructor
        /// </summary>
        public RedisCacheService(IConfigurationManager configurationManager)
        {
            this.m_configuration = configurationManager.GetSection<RedisConfigurationSection>();

        }

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
        /// Serialize to stream
        /// </summary>
        private void SerializeXmlToStream(IdentifiedData data, Stream targetStream)
        {
            XmlSerializer xsz = XmlModelSerializerFactory.Current.CreateSerializer(data.GetType());
            var objectTypeData = System.Text.Encoding.UTF8.GetBytes(data.GetType().AssemblyQualifiedName);
            var objectTypeLength = BitConverter.GetBytes(objectTypeData.Length);

            targetStream.Write(objectTypeLength, 0, objectTypeLength.Length);
            targetStream.Write(objectTypeData, 0, objectTypeData.Length);
            using (var xs = XmlWriter.Create(targetStream, new XmlWriterSettings() { Indent = false, Encoding = System.Text.Encoding.UTF8 }))
            {
                xsz.Serialize(targetStream, data);
            }
        }

        /// <summary>
        /// Serialize objects
        /// </summary>
        private RedisValue SerializeToRedisValue(IdentifiedData data)
        {
            data.BatchOperation = Core.Model.DataTypes.BatchOperationType.Auto;

            switch (this.m_configuration.Format)
            {
                case RedisFormat.Xml:

                    using (var ms = new MemoryStream())
                    {
                        if (this.m_configuration.Compress)
                        {
                            using (var gzs = new GZipStream(ms, CompressionLevel.Fastest))
                            {
                                this.SerializeXmlToStream(data, gzs);
                            }
                        }
                        else
                        {
                            this.SerializeXmlToStream(data, ms);
                        }

                        return ms.ToArray();
                    }
                case RedisFormat.Json:
                    using (var sw = new StringWriter())
                    {
                        using (var jw = new JsonTextWriter(sw))
                        {
                            this.m_serializer.Serialize(jw, data);
                        }
                        return sw.ToString();
                    }
                default:
                    throw new NotSupportedException(this.m_configuration.Format.ToString());
            }

        }

        /// <summary>
        /// De-serialize object from stream
        /// </summary>
        /// <param name="sourceStream">The stream to read from</param>
        /// <returns>The parsed object</returns>
        private IdentifiedData DeserializeObjectFromStream(Stream sourceStream)
        {
            byte[] headerLengthBytes = new byte[4], headerType = null;
            sourceStream.Read(headerLengthBytes, 0, 4);
            // Now read the type registration
            var headerLength = BitConverter.ToInt32(headerLengthBytes, 0);
            headerType = new byte[headerLength];
            sourceStream.Read(headerType, 0, headerLength);

            // Now get the type and serializer
            var typeString = System.Text.Encoding.UTF8.GetString(headerType);
            var type = Type.GetType(typeString);
            XmlSerializer xsz = XmlModelSerializerFactory.Current.CreateSerializer(type);
            return xsz.Deserialize(sourceStream) as IdentifiedData;
        }

        /// <summary>
        /// Parse the REDIS hash values and de-serialize the data to an <see cref="IdentifiedData"/> instance
        /// </summary>
        private IdentifiedData DeserializeObjectFromRedis(RedisValue rvValue)
        {
            if (rvValue.IsNullOrEmpty)
            {
                return null;
            }

            switch (this.m_configuration.Format)
            {
                case RedisFormat.Xml:
                    // Find serializer
                    using (var sr = new MemoryStream((byte[])rvValue))
                    {
                        if (this.m_configuration.Compress)
                        {
                            using (var gzs = new GZipStream(sr, CompressionMode.Decompress))
                            {
                                return this.DeserializeObjectFromStream(gzs);
                            }
                        }
                        else
                        {
                            return this.DeserializeObjectFromStream(sr);
                        }
                    }
                case RedisFormat.Json:
                    using (var sr = new StringReader(rvValue))
                    {
                        using (var jr = new JsonTextReader(sr))
                        {
                            return this.m_serializer.Deserialize(jr) as IdentifiedData;
                        }
                    }
                default:
                    throw new NotSupportedException(this.m_configuration.Format.ToString());
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
                this.EnsureCacheConsistency(data);

                // We want to add only those when the connection is present
                if (RedisConnectionManager.Current.Connection == null || data == null || !data.Key.HasValue ||
                    (data as BaseEntityData)?.ObsoletionTime.HasValue == true ||
                    this.m_nonCached.Contains(data.GetType()))
                {
                    return;
                }
                else if (data.GetType().GetCustomAttribute<NonCachedAttribute>() != null)
                {
                    this.m_nonCached.Add(data.GetType());
                    return;
                }

                // HACK: Remove all non-transient tags since the persistence layer doesn't persist them
                if (data is ITaggable taggable)
                {
                    // TODO: Put this as a constant
                    // Don't cache generated data
                    if (taggable.GetTag(SystemTagNames.GeneratedDataTag) == "true")
                    {
                        return;
                    }

                    taggable.RemoveAllTags(o => o.TagKey.StartsWith("$") || o.TagKey != SystemTagNames.DcdrRefetchTag);
                }

                var redisDb = RedisConnectionManager.Current.Connection.GetDatabase(RedisCacheConstants.CacheDatabaseId);
                var cacheKey = data.Key.Value.ToString();

                var cacheData = new HashEntry[] {
                    new HashEntry(FIELD_VALUE, this.SerializeToRedisValue(data)),
                    new HashEntry(FIELD_LOAD, (int?)data.GetAnnotations<LoadMode>().FirstOrDefault())
                };
                redisDb.HashSet(cacheKey, cacheData, flags: CommandFlags.FireAndForget);
                redisDb.KeyExpire(cacheKey, expiry: this.m_configuration.TTL, flags: CommandFlags.FireAndForget);

                //this.EnsureCacheConsistency(new DataCacheEventArgs(data));
                if (this.m_configuration.PublishChanges)
                {
                    var existing = redisDb.KeyExists(data.Key.Value.ToString());
#if DEBUG
                    this.m_tracer.TraceVerbose("HashSet {0} (EXIST: {1}; @: {2})", data, existing, new System.Diagnostics.StackTrace(true).GetFrame(1));
#endif
                    if (existing)
                    {
                        RedisConnectionManager.Current.Connection.GetSubscriber().Publish(PUBSUB_TOPIC, $"PUT cache://{Environment.MachineName}/{cacheKey}", CommandFlags.FireAndForget);
                    }
                    else
                    {
                        RedisConnectionManager.Current.Connection.GetSubscriber().Publish(PUBSUB_TOPIC, $"POST cache://{Environment.MachineName}/{cacheKey}", CommandFlags.FireAndForget);
                    }
                }
            }
            catch (ObjectDisposedException) { }
            catch (Exception e)
            {
                this.m_tracer.TraceWarning("REDIS CACHE ERROR (CACHING SKIPPED): {0}", e.Message);
            }
        }

        /// <summary>
        /// Get cache item
        /// </summary>
        /// <typeparam name="TData"></typeparam>
        /// <param name="key"></param>
        /// <returns></returns>
        public TData GetCacheItem<TData>(Guid key) where TData : IdentifiedData
        {
            var retVal = this.GetCacheItem(key);
            if (retVal is TData td)
            {
                return td;
            }
            else
            {
                this.Remove(key);
                return default(TData);
            }
        }

        /// <summary>
        /// Get a cache item
        /// </summary>
        public IdentifiedData GetCacheItem(Guid key)
        {
            try
            {
                // We want to add
                if (RedisConnectionManager.Current.Connection == null)
                {
                    return null;
                }

                // Add
                var cacheKey = key.ToString();

                var redisDb = RedisConnectionManager.Current.Connection.GetDatabase(RedisCacheConstants.CacheDatabaseId);
                var hdata = redisDb.HashGet(cacheKey, FIELD_VALUE);
                if (!hdata.IsNullOrEmpty)
                {
                    var retVal = this.DeserializeObjectFromRedis(hdata);
                    var loadState = redisDb.HashGet(cacheKey, FIELD_LOAD);
                    if (loadState.HasValue)
                    {
                        retVal.AddAnnotation((LoadMode)(int)loadState);
                    }
                    return retVal;
                }
                else
                {
                    return null;
                }
            }
            catch (ObjectDisposedException) { return null; }
            catch (Exception e)
            {
                this.m_tracer.TraceWarning("REDIS CACHE ERROR (FETCHING SKIPPED): {0}", e.Message);

                return null;
            }
        }

        /// <inheritdoc/>
        public void Remove(Guid key)
        {
            // We want to add
            if (RedisConnectionManager.Current.Connection == null)
            {
                return;
            }
            // Add
            var existing = this.GetCacheItem(key);
            this.Remove(existing as IdentifiedData);
        }

        /// <inheritdoc/>
        public void Remove(IdentifiedData entry)
        {
            if (entry == null)
            {
                return;
            }

            try
            {
                var redisDb = RedisConnectionManager.Current.Connection.GetDatabase(RedisCacheConstants.CacheDatabaseId);
                redisDb.KeyDelete(entry.Key.ToString(), CommandFlags.FireAndForget);
                entry.BatchOperation = Core.Model.DataTypes.BatchOperationType.Delete;
                this.EnsureCacheConsistency(entry);
                RedisConnectionManager.Current.Connection.GetSubscriber().Publish("oiz.events", $"DELETE http://{Environment.MachineName}/cache/{entry.Key}");
            }
            catch (ObjectDisposedException) { }
            catch (Exception e)
            {
                this.m_tracer.TraceWarning("Error removing from REDIS - skipping - {0}", e.Message);
            }
        }


        /// <summary>
        /// Ensure cache consistency
        /// </summary>
        /// <remarks>This method ensures that referenced objects (objects which are stored or updated which
        /// are associative in nature) have their source and target objects evicted from cache.</remarks>
        private void EnsureCacheConsistency(IdentifiedData data)
        {
            // No data - no consistency needed
            if (data == null || data.GetAnnotations<CacheConsistentIndicator>().Any()) { return; }
            data.AddAnnotation(new CacheConsistentIndicator());

            switch (data)
            {
                case Bundle bundle: // If it is a bundle we want to process the bundle
                    foreach (var itm in bundle.Item)
                    {
                        if (itm.BatchOperation == Core.Model.DataTypes.BatchOperationType.Delete)
                        {
                            this.Remove(itm);
                        }
                        else
                        {
                            this.Add(itm);
                        }
                    }
                    break;
                case ISimpleAssociation sa:
                    if (data.BatchOperation != Core.Model.DataTypes.BatchOperationType.Auto)
                    {
                        var host = this.GetCacheItem(sa.SourceEntityKey.GetValueOrDefault()); // Cache remove the source item
                        if (host != null) // hosting type is cached
                        {
                            foreach (var prop in host.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(o => o.PropertyType.StripGeneric().IsAssignableFrom(data.GetType())))
                            {
                                var value = host.LoadProperty(prop.Name) as IList;
                                if (value is IList list)
                                {
                                    var exist = list.OfType<IdentifiedData>().FirstOrDefault(o => o.SemanticEquals(data));
                                    if (exist != null)
                                    {
                                        list.Remove(exist);
                                    }

                                    // Re-add
                                    if (data.BatchOperation != Core.Model.DataTypes.BatchOperationType.Delete)
                                    {
                                        list.Add(data);
                                    }
                                }
                            }

                            if (host is ITaggable ite)
                            {
                                ite.AddTag(SystemTagNames.DcdrRefetchTag, "true");
                            }
                            this.Add(host); // refresh 

                        }
                    }
                    break;
            }
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
                {
                    this.m_nonCached.Add(itm);
                }

                // Subscribe to SanteDB events
                RedisConnectionManager.Current.Subscriber.Subscribe(PUBSUB_TOPIC, (o, e) =>
                {
                    try
                    {
                        if (e.HasValue)
                        {
                            var notificationData = e.ToString().Split(' ');
                            var notificationUrl = new Uri(notificationData.Last());
                            if ("cache".Equals(notificationUrl.Scheme) && Guid.TryParse(notificationUrl.AbsolutePath, out var cacheObject))
                            {
                                this.m_tracer.TraceVerbose("Receive Pub/Sub Notification {0}", e);
                                switch (notificationData[0].ToUpperInvariant())
                                {
                                    case "POST":
                                        this.Added?.Invoke(this, new DataCacheEventArgs(this.GetCacheItem(cacheObject)));
                                        break;
                                    case "PUT":
                                        this.Updated?.Invoke(this, new DataCacheEventArgs(this.GetCacheItem(cacheObject)));
                                        break;
                                    case "DELETE":
                                        this.Removed?.Invoke(this, new DataCacheEventArgs(this.GetCacheItem(cacheObject)));
                                        break;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        this.m_tracer.TraceWarning("Error on event subscription - {0}", ex.Message);
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

        /// <summary>
        /// Returns tru if the cache contains the specified id
        /// </summary>
        public bool Exists<T>(Guid id) where T : IdentifiedData
        {
            try
            {
                var db = RedisConnectionManager.Current.Connection?.GetDatabase(RedisCacheConstants.AdhocCacheDatabaseId);
                return db.KeyExists(id.ToString());
            }
            catch (Exception e)
            {
                this.m_tracer.TraceError("Error removing entity from ad-hoc cache : {0}", e);
                return false;
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