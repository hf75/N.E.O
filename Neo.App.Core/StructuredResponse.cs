using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Neo.App
{
    public class StructuredResponse
    {
        [JsonProperty("Code")]
        public string Code { get; set; } = string.Empty;

        [JsonProperty("Patch")]
        public string Patch { get; set; } = string.Empty;

        [JsonProperty("NuGetPackages")]
        public List<string> NuGetPackages { get; set; } = new();
        [JsonProperty("Explanation")]
        public string Explanation { get; set; } = string.Empty;

        [JsonProperty("Chat")]
        public string Chat { get; set; } = string.Empty;

        [JsonProperty("PowerShellScript")]
        public string PowerShellScript { get; set; } = string.Empty;

        [JsonProperty("ConsoleAppCode")]
        public string ConsoleAppCode { get; set; } = string.Empty;
    }
}
