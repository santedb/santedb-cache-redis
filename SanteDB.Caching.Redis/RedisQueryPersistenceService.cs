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
 * Date: 2023-5-19
 */
using SanteDB.Caching.Redis.Configuration;
using SanteDB.Core;
using SanteDB.Core.Diagnostics;
using SanteDB.Core.Services;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace SanteDB.Caching.Redis
{
    /// <summary>
    /// An implementation of the <see cref="IQueryPersistenceService"/> which usees REDIS for its stateful result set
    /// </summary>
    /// <remarks>
    /// <para>This persistence service uses REDIS list values to store the UUIDs representing the query executed on the SanteDB server. The data
    /// is stored in database 2 of the REDIS server.</para>
    /// </remarks>
    [ServiceProvider("REDIS Query Persistence Service")]
    [ExcludeFromCodeCoverage] // Unit testing on REDIS is not possible in unit tests
    public class RedisQueryPersistenceService : IQueryPersistenceService, IDaemonService
    {
        /// <inheritdoc/>
        public string ServiceName => "REDIS Query Persistence Service";

        /// <inheritdoc/>
        public bool IsRunning => this.m_configuration != null;

        // Redis trace source
        private readonly Tracer m_tracer = new Tracer(RedisCacheConstants.TraceSourceName);

        /// <summary>
        /// Query tag in a hash set
        /// </summary>
        private const int FIELD_QUERY_TAG_IDX = 0;

        /// <summary>
        /// Query total results
        /// </summary>
        private const int FIELD_QUERY_TOTAL_RESULTS = 1;

        /// <summary>
        /// Query result index
        /// </summary>
        private const int FIELD_QUERY_RESULT_IDX = 2;

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
        public void AddResults(Guid queryId, IEnumerable<Guid> results, int totalResults)
        {
            try
            {
                var redisConn = RedisConnectionManager.Current.Connection.GetDatabase(RedisCacheConstants.QueryDatabaseId);
                if (redisConn.KeyExists($"{queryId}.{FIELD_QUERY_RESULT_IDX}"))
                {
                    var batch = redisConn.CreateBatch();
                    batch.ListRightPushAsync($"{queryId}.{FIELD_QUERY_RESULT_IDX}", results.Select(o => (RedisValue)o.ToByteArray()).ToArray(), flags: CommandFlags.FireAndForget);
                    batch.StringSetAsync($"{queryId}.{FIELD_QUERY_TOTAL_RESULTS}", BitConverter.GetBytes(totalResults), expiry: this.m_configuration.TTL);
                    batch.KeyExpireAsync($"{queryId}.{FIELD_QUERY_RESULT_IDX}", this.m_configuration.TTL);
                    batch.Execute();
                }
            }
            catch (Exception e)
            {
                this.m_tracer.TraceError("Error fetching results from REDIS: {0}", e);
                throw new Exception("Error fetching results from REDIS", e);
            }
        }

        /// <inheritdoc/>
        public Guid FindQueryId(object queryTag)
        {
            throw new NotSupportedException();
        }

        /// <inheritdoc/>
        public IEnumerable<Guid> GetQueryResults(Guid queryId, int offset, int count)
        {
            try
            {
                var redisConn = RedisConnectionManager.Current.Connection.GetDatabase(RedisCacheConstants.QueryDatabaseId);
                var batch = redisConn.CreateBatch();
                batch.KeyExpireAsync($"{queryId}.{FIELD_QUERY_RESULT_IDX}", this.m_configuration.TTL);
                batch.KeyExpireAsync($"{queryId}.{FIELD_QUERY_TOTAL_RESULTS}", this.m_configuration.TTL);
                batch.Execute();
                if (redisConn.KeyExists($"{queryId}.{FIELD_QUERY_RESULT_IDX}"))
                {
                    return redisConn.ListRange($"{queryId}.{FIELD_QUERY_RESULT_IDX}", offset, offset + count).Select(o => new Guid((byte[])o)).ToArray();
                }
                else
                {
                    return new Guid[0];
                }
            }
            catch (Exception e)
            {
                this.m_tracer.TraceError("Error fetching results from REDIS: {0}", e);
                throw new Exception("Error fetching results from REDIS", e);
            }
        }

        /// <inheritdoc/>
        public object GetQueryTag(Guid queryId)
        {
            try
            {
                var redisConn = RedisConnectionManager.Current.Connection.GetDatabase(RedisCacheConstants.QueryDatabaseId);
                return redisConn.StringGet($"{queryId}.{FIELD_QUERY_TAG_IDX}").ToString();
            }
            catch (Exception e)
            {
                this.m_tracer.TraceError("Error fetching: {0}", e);
                throw new Exception("Error fetching tag from REDIS", e);
            }
        }

        /// <inheritdoc/>
        public bool IsRegistered(Guid queryId)
        {
            try
            {
                var redisConn = RedisConnectionManager.Current.Connection.GetDatabase(RedisCacheConstants.QueryDatabaseId);
                return redisConn.KeyExists($"{queryId}.{FIELD_QUERY_RESULT_IDX}");
            }
            catch (Exception e)
            {
                this.m_tracer.TraceError("Error fetching: {0}", e);
                throw new Exception("Error fetching from REDIS", e);
            }
        }

        /// <inheritdoc/>
        public long QueryResultTotalQuantity(Guid queryId)
        {
            try
            {
                var redisConn = RedisConnectionManager.Current.Connection.GetDatabase(RedisCacheConstants.QueryDatabaseId);
                var strTotalCount = redisConn.StringGet($"{queryId}.{FIELD_QUERY_TOTAL_RESULTS}");
                if (strTotalCount.HasValue)
                {
                    return BitConverter.ToInt32(strTotalCount, 0);
                }

                return 0;
            }
            catch (Exception e)
            {
                this.m_tracer.TraceError("Error getting query result quantity: {0}", e);
                throw new Exception("Error getting query result from REDIS", e);
            }
        }

        /// <inheritdoc/>
        public bool RegisterQuerySet(Guid queryId, IEnumerable<Guid> results, object tag, int totalResults)
        {
            try
            {
                var redisConn = RedisConnectionManager.Current.Connection.GetDatabase(RedisCacheConstants.QueryDatabaseId);
                var batch = redisConn.CreateBatch();
                batch.KeyDeleteAsync($"{queryId}.{FIELD_QUERY_RESULT_IDX}");
                batch.ListRightPushAsync($"{queryId}.{FIELD_QUERY_RESULT_IDX}", results.Select(o => (RedisValue)o.ToByteArray()).ToArray());

                if (tag != null)
                {
                    batch.StringSetAsync($"{queryId}.{FIELD_QUERY_TAG_IDX}", tag.ToString(), expiry: this.m_configuration.TTL);
                }

                batch.StringSetAsync($"{queryId}.{FIELD_QUERY_TOTAL_RESULTS}", BitConverter.GetBytes(totalResults), expiry: this.m_configuration.TTL);
                batch.KeyExpireAsync($"{queryId}.{FIELD_QUERY_RESULT_IDX}", this.m_configuration.TTL);
                batch.Execute();
                return true;
            }
            catch (Exception e)
            {
                this.m_tracer.TraceError("Error registering query in REDIS: {0}", e);
                throw new Exception("Error getting query result from REDIS", e);
            }
        }

        /// <inheritdoc/>
        public void SetQueryTag(Guid queryId, object value)
        {
            try
            {
                var redisConn = RedisConnectionManager.Current.Connection.GetDatabase(RedisCacheConstants.QueryDatabaseId);
                if (redisConn.KeyExists($"{queryId}.{FIELD_QUERY_RESULT_IDX}"))
                {
                    redisConn.StringSet($"{queryId}.{FIELD_QUERY_TAG_IDX}", value?.ToString(), flags: CommandFlags.FireAndForget, expiry: this.m_configuration.TTL);
                }
            }
            catch (Exception e)
            {
                this.m_tracer.TraceError("Error setting tags in REDIS: {0}", e);
                throw new Exception("Error setting query tag in REDIS", e);
            }
        }

        /// <inheritdoc/>
        public bool Start()
        {
            try
            {
                this.Starting?.Invoke(this, EventArgs.Empty);

                this.m_tracer.TraceInfo("Starting REDIS query service to hosts {0}...", String.Join(";", this.m_configuration.Servers));
                this.m_tracer.TraceInfo("Using shared REDIS cache {0}", RedisConnectionManager.Current.Connection);

                this.Started?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch (Exception e)
            {
                this.m_tracer.TraceError("Error starting REDIS query persistence, will switch to query persister : {0}", e);
                ApplicationServiceContext.Current.GetService<IServiceManager>().RemoveServiceProvider(typeof(RedisQueryPersistenceService));
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

        /// <summary>
        /// Abort the query set <paramref name="queryId"/>
        /// </summary>
        public void AbortQuerySet(Guid queryId)
        {
            try
            {
                var redisConn = RedisConnectionManager.Current.Connection.GetDatabase(RedisCacheConstants.QueryDatabaseId);
                redisConn.KeyDelete($"{queryId}.{FIELD_QUERY_TAG_IDX}", flags: CommandFlags.FireAndForget);
                redisConn.KeyDelete($"{queryId}.{FIELD_QUERY_RESULT_IDX}", flags: CommandFlags.FireAndForget);
                redisConn.KeyDelete($"{queryId}.{FIELD_QUERY_TOTAL_RESULTS}", flags: CommandFlags.FireAndForget);
            }
            catch (Exception e)
            {
                this.m_tracer.TraceError("Error setting tags in REDIS: {0}", e);
                throw new Exception("Error setting query tag in REDIS", e);
            }
        }
    }
}