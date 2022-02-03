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
using SanteDB.Core.Services;
using StackExchange.Redis;
using System;
using System.Diagnostics.CodeAnalysis;

namespace SanteDB.Caching.Redis
{
    /// <summary>
    /// REDIS Connection manager which keeps a multiplexor for common use in all the REDIS implementations of the various caching services.
    /// </summary>
    [ExcludeFromCodeCoverage] // Unit testing on REDIS is not possible in unit tests
    internal class RedisConnectionManager : IDisposable
    {
        /// <summary>
        ///  Current connection manager
        /// </summary>
        private static RedisConnectionManager m_current;

        // Lock
        private static object m_lock = new object();

        // Connection manager
        private ConnectionMultiplexer m_connection;

        // Subscriber
        private ISubscriber m_subscriber;

        // Configuration section
        private RedisConfigurationSection m_configuration = ApplicationServiceContext.Current.GetService<IConfigurationManager>().GetSection<RedisConfigurationSection>();

        private readonly Tracer m_tracer = Tracer.GetTracer(typeof(RedisConnectionManager));

        /// <summary>
        /// Constructor for the singleton instance of the multiplexor
        /// </summary>
        private RedisConnectionManager()
        {
            var configuration = new ConfigurationOptions()
            {
                Password = this.m_configuration.Password,
                ConnectTimeout = 1000,
                ConnectRetry = 2,
                SyncTimeout = 2000,
                Ssl = false
            };

            foreach (var itm in this.m_configuration.Servers)
            {
                var epData = itm.Split(':');
                this.m_tracer.TraceInfo("Adding {0}:{1}", epData[0], epData[1]);
                configuration.EndPoints.Add(epData[0], int.Parse(epData[1]));
            }

            this.m_connection = ConnectionMultiplexer.Connect(configuration);
            this.m_subscriber = this.m_connection.GetSubscriber();
        }

        /// <summary>
        /// Gets the connection multiplexor
        /// </summary>
        public ConnectionMultiplexer Connection => this.m_connection;

        /// <summary>
        /// Get the subscriber interface from the current connection
        /// </summary>
        public ISubscriber Subscriber => this.m_subscriber;

        /// <summary>
        /// Gets the current instance of the connection manager (creates a singleton)
        /// </summary>
        public static RedisConnectionManager Current
        {
            get
            {
                if (m_current == null)
                    lock (m_lock)
                        if (m_current == null)
                            m_current = new RedisConnectionManager();
                return m_current;
            }
        }

        /// <summary>
        /// Dispose the connection and this connection manager.
        /// </summary>
        public void Dispose()
        {
            lock (m_lock)
            {
                this.m_connection?.Dispose();
                this.m_connection = null;
                m_current = null;
            }
        }
    }
}