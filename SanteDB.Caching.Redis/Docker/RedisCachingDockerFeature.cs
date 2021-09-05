﻿/*
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
using SanteDB.Caching.Redis;
using SanteDB.Caching.Redis.Configuration;
using SanteDB.Core.Configuration;
using SanteDB.Core.Exceptions;
using SanteDB.Docker.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;

namespace SanteDB.Caching.Redis.Docker
{
    /// <summary>
    /// Caching feature
    /// </summary>
    public class RedisCachingDockerFeature : IDockerFeature
    {


        public const string MaxAgeSetting = "TTL";
        public const string RedisAddressSetting = "SERVER";

        /// <summary>
        /// Get the id of this feature
        /// </summary>
        public string Id => "REDIS";

        /// <summary>
        /// Settings for the caching feature
        /// </summary>
        public IEnumerable<string> Settings => new String[] { MaxAgeSetting, RedisAddressSetting };

        /// <summary>
        /// Configure this service
        /// </summary>
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
                    Servers = new string[] {  "127.0.0.1:6379" }
                };
                configuration.AddSection(redisSetting);
            }
            redisSetting.TTLXml = XmlConvert.ToString(maxAgeTs);

            if (settings.TryGetValue(RedisAddressSetting, out string redisServer))
            {
                redisSetting.Servers = new string [] { redisServer };
            }

            // Add services
            var serviceConfiguration = configuration.GetSection<ApplicationServiceContextConfigurationSection>().ServiceProviders;
            serviceConfiguration.AddRange(serviceTypes.Where(t => !serviceConfiguration.Any(c => c.Type == t)).Select(t => new TypeReferenceConfiguration(t)));
        }
    }
}
