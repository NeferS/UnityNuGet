﻿using System.Collections.Generic;
using Newtonsoft.Json;
using NuGet.Versioning;

namespace UnityNuGet
{
    /// <summary>
    /// An entry in the <see cref="Registry"/>
    /// </summary>
    public class RegistryEntry
    {
        [JsonProperty("ignore")]
        public bool Ignored { get; set; }

        [JsonProperty("listed")]
        public bool Listed { get; set; }

        [JsonProperty("scope")]
        public string? UnityScope { get; set; }

        [JsonProperty("version")]
        public VersionRange? Version { get; set; }

        [JsonProperty("defineConstraints")]
        public List<string> DefineConstraints { get; set; } = new();

        [JsonProperty("analyzer")]
        public bool Analyzer { get; set; }
    }
}
