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
using SanteDB.Caching.Redis.Configuration;
using SanteDB.Core.Configuration;
using SanteDB.Core.Exceptions;
using SanteDB.Docker.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Xml;

namespace SanteDB.Caching.Redis.Docker
{
    /// <summary>
    /// Docker feature for the SanteDB REDIS caching provider
    /// </summary>
    [ExcludeFromCodeCoverage]
    public class RedisCachingDockerFeature : IDockerFeature
    {
        /// <summary>
        /// The maximum age of objects to place in the cache
        /// </summary>
        public const string MaxAgeSetting = "TTL";

        /// <summary>
        /// The server connection setting
        /// </summary>
        public const string RedisAddressSetting = "SERVER";

        /// <summary>
        /// Get the id of this feature
        /// </summary>
        public string Id => "REDIS";

        /// <summary>
        /// Settings for the caching feature
        /// </summary>
        /// <remarks>
        /// <list type="table">
        ///     <item><term>SDB_REDIS_TTL</term><description>The time to live in XML format (PT5M) or .NET timestamp string format (0.00:05:00) - (default: 1 hour)</description></item>
        ///     <item><term>SDB_REDIS_SERVER</term><description>The REDIS server which should be used for caching - (default: 127.0.0.1:6379)</description></item>
        /// </list>
        /// </remarks>
        public IEnumerable<string> Settings => new String[] { MaxAgeSetting, RedisAddressSetting };

        /// <inheritdoc/>
        public void Configure(SanteDBConfiguration configuration, IDictionary<string, string> settings)
        {
            // The type of service to add
            Type[] serviceTypes = new Type[] {
                            typeof(RedisCacheService),
                            typeof(RedisAdhocCache),
                            typeof(RedisQueryPersistenceService)
                        };

            if (!settings.TryGetValue(MaxAgeSetting, out string maxAge))
            {
                maxAge = "0.1:0:0";
            }

            // Parse
            if (!TimeSpan.TryParse(maxAge, out TimeSpan maxAgeTs))
            {
                throw new ConfigurationException($"{maxAge} is not understood as a timespan", configuration);
            }

            // REDIS Config
            var redisSetting = configuration.GetSection<RedisConfigurationSection>();
            if (redisSetting == null)
            {
                redisSetting = new RedisConfigurationSection()
                {
                    PublishChanges = false,
                    Servers = new string[] { "127.0.0.1:6379" }
                };
                configuration.AddSection(redisSetting);
            }
            redisSetting.TTLXml = XmlConvert.ToString(maxAgeTs);

            if (settings.TryGetValue(RedisAddressSetting, out string redisServer))
            {
                redisSetting.Servers = new string[] { redisServer };
            }

            // Add services
            var serviceConfiguration = configuration.GetSection<ApplicationServiceContextConfigurationSection>().ServiceProviders;
            serviceConfiguration.AddRange(serviceTypes.Where(t => !serviceConfiguration.Any(c => c.Type == t)).Select(t => new TypeReferenceConfiguration(t)));
        }
    }
}