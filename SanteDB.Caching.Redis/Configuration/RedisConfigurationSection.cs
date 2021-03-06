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
using SanteDB.Core.Configuration;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Xml;
using System.Xml.Serialization;

namespace SanteDB.Caching.Redis.Configuration
{
    /// <summary>
    /// Represents a simple redis configuration
    /// </summary>
    [XmlType(nameof(RedisConfigurationSection), Namespace = "http://santedb.org/configuration")]
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
        /// Gets the configuration connection string
        /// </summary>
        [XmlArray("servers"), XmlArrayItem("add"), DisplayName("REDIS Server(s)"), Description("Sets one or more REDIS servers to use for the caching of data in format HOST:PORT")]
        public String[] Servers { get; set; }

        /// <summary>
        /// Username
        /// </summary>
        [XmlAttribute("username"), DisplayName("REDIS User"), Description("If the REDIS infrastructure requires authentication, the user name to authenticate with")]
        public String UserName { get; set; }

        /// <summary>
        /// Password to the server
        /// </summary>
        [XmlAttribute("password")]
        [PasswordPropertyTextAttribute, DisplayName("Password"), Description("The password to use when connecting to REDIS")]
        public String Password { get; set; }

        /// <summary>
        /// Time to live for XML serialization
        /// </summary>
        [XmlAttribute("ttl"), Browsable(false)]
        public string TTLXml
        {
            get { return this.TTL.HasValue ? XmlConvert.ToString(this.TTL.Value) : null; }
            set { if (!string.IsNullOrEmpty(value)) this.TTL = XmlConvert.ToTimeSpan(value); }
        }

        /// <summary>
        /// Gets or sets the time to live
        /// </summary>
        [XmlIgnore, DisplayName("TTL"), Description("The maximum time that cache entries have to live in the REDIS cache")]
        [Editor("SanteDB.Configuration.Editors.TimespanPickerEditor, SanteDB.Configuration", "System.Drawing.Design.UITypeEditor, System.Drawing")]
        public TimeSpan? TTL { get; private set; }

        /// <summary>
        /// When true notify other systems of the changes
        /// </summary>
        [XmlAttribute("publish"), DisplayName("Broadcast Changes"), Description("When true, use the REDIS pub/sub infrastructure to broadcast changes ")]
        public bool PublishChanges { get; set; }

    }
}
