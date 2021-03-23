using SanteDB.Caching.Redis;
using SanteDB.Caching.Redis.Configuration;
using SanteDB.Core.Configuration;
using SanteDB.Core.Exceptions;
using SanteDB.Docker.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SanteDB.Caching.Redis.Docker
{
    /// <summary>
    /// Caching feature
    /// </summary>
    public class RedisCachingFeature : IDockerFeature
    {


        public const string MaxAgeSetting = "EXPIRE";
        public const string RedisAddressSetting = "REDIS_SERVER";

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
                maxAge = "PT1H";
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
                redisSetting = DockerFeatureUtils.LoadConfigurationResource<RedisConfigurationSection>("SanteDB.Caching.Redis.Docker.RedisCacheFeature.xml");
                configuration.AddSection(redisSetting);
            }
            redisSetting.TTLXml = maxAgeTs.ToString();

            if (settings.TryGetValue(RedisAddressSetting, out string redisServer))
            {
                redisSetting.Servers = new List<string>() { redisServer };
            }

            // Add services
            var serviceConfiguration = configuration.GetSection<ApplicationServiceContextConfigurationSection>().ServiceProviders;
            serviceConfiguration.AddRange(serviceTypes.Where(t => !serviceConfiguration.Any(c => c.Type == t)).Select(t => new TypeReferenceConfiguration(t)));
        }
    }
}
