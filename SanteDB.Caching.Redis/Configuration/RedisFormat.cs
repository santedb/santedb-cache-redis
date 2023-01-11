using System;
using System.Collections.Generic;
using System.Text;
using System.Xml.Serialization;

namespace SanteDB.Caching.Redis.Configuration
{
    /// <summary>
    /// Format of REDIS messages
    /// </summary>
    [XmlType(nameof(RedisFormat), Namespace = "http://santedb.org/configuration")]
    public enum RedisFormat
    {

        /// <summary>
        /// XML format
        /// </summary>
        [XmlEnum("xml")]
        Xml,
        /// <summary>
        /// JSON format
        /// </summary>
        [XmlEnum("json")]
        Json
    }
}
