using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Neo.App
{
    public class ResxData
    {
        public int Version { get; set; }
        /// <summary>
        /// Ein zusätzlicher String, der mit serialisiert werden soll.
        /// </summary>
        public string? Code { get; set; }

        /// <summary>
        /// Ein Dictionary, das ebenfalls serialisiert werden soll.
        /// </summary>
        public Dictionary<string, string>? Nuget { get; set; }

        public string? History { get; set; }
    }
}
