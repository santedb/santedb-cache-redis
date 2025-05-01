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
using SanteDB.Core.Configuration;
using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Xml;
using System.Xml.Serialization;

namespace SanteDB.Caching.Redis.Configuration
{
    /// <summary>
    /// Configuration section that is used to control the REDIS caching
    /// </summary>
    [XmlType(nameof(RedisConfigurationSection), Namespace = "http://santedb.org/configuration")]
    [ExcludeFromCodeCoverage] // Unit testing on REDIS is not possible in unit tests
    public class RedisConfigurationSection : IConfigurationSection
    {
        /// <summary>
        /// REDIS Configuration
        /// </summary>
        public RedisConfigurationSection()
        {
            this.TTL = new TimeSpan(0, 15, 0);
        }

        /// <summary>
        /// Gets or sets whether the data being sent to REDIS should be in JSON
        /// </summary>
        [XmlAttribute("format"), DisplayName("Value Format"), Description("Specifies the type and format of the values in the REDIS cache")]
        public RedisFormat Format { get; set; }

        /// <summary>
        /// Gets or sets whether data being sent to the REDIS cache should be compressed
        /// </summary>
        /// <remarks>This is typically set for remote REDIS servers where bandwidth between the server is limited and the time of
        /// compressing the data and shipping it to the REDIS server is faster than the time of just sending the data</remarks>
        [XmlAttribute("compress"), DisplayName("Compress Data"), Description("When true, traffic between this server and the REDIS cache server will be compressed")]
        public bool Compress { get; set; }

        /// <summary>
        /// The REDIS server(s) which should be contacted for storing data
        /// </summary>
        [XmlArray("servers"), XmlArrayItem("add"), DisplayName("REDIS Server(s)"), Description("Sets one or more REDIS servers to use for the caching of data in format HOST:PORT")]
        public String[] Servers { get; set; }

        /// <summary>
        /// If the REDIS server requires authentication, the user name which SanteDB should use to connect to the server
        /// </summary>
        [XmlAttribute("username"), DisplayName("REDIS User"), Description("If the REDIS infrastructure requires authentication, the user name to authenticate with")]
        public String UserName { get; set; }

        /// <summary>
        /// If the REDIS server requires authentication, the password which SanteDB should use to connect to the server
        /// </summary>
        [XmlAttribute("password")]
        [PasswordPropertyTextAttribute, DisplayName("Password"), Description("The password to use when connecting to REDIS")]
        public String Password { get; set; }

        /// <summary>
        /// XML serialized value of the time-to-live parameter.
        /// </summary>
        /// <remarks>This is added because the attribute for <see cref="TimeSpan"/> is not supported by the .NET Serializer</remarks>
        [XmlAttribute("ttl"), Browsable(false)]
        public string TTLXml
        {
            get { return this.TTL.HasValue ? XmlConvert.ToString(this.TTL.Value) : null; }
            set
            {
                if (!string.IsNullOrEmpty(value))
                {
                    this.TTL = XmlConvert.ToTimeSpan(value);
                }
            }
        }

        /// <summary>
        /// Gets or sets the time to live for objects in the server
        /// </summary>
        [XmlIgnore, DisplayName("TTL"), Description("The maximum time that cache entries have to live in the REDIS cache")]
        [Editor("SanteDB.Configuration.Editors.TimespanPickerEditor, SanteDB.Configuration", "System.Drawing.Design.UITypeEditor, System.Drawing")]
        public TimeSpan? TTL { get; private set; }

        /// <summary>
        /// When true, instructs SanteDB to use the REDIS pub/sub mechanisms to notify other SanteDB servers that a cache object has changed
        /// </summary>
        [XmlAttribute("publish"), DisplayName("Broadcast Changes"), Description("When true, use the REDIS pub/sub infrastructure to broadcast changes ")]
        public bool PublishChanges { get; set; }
    }
}