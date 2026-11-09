using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Apps2Samsung.Models;

namespace Apps2Samsung.Catalog.Repositories
{
    /// <summary>
    /// An independent repository's manifest, format v1 (#1018): the payload of its
    /// <c>repository.json.signed</c>. The spec is <c>docs/repositories.md</c> in
    /// tizen-community-packages, with the schema in <c>spec/repository.schema.json</c> there. The
    /// provider and release shapes are the ones <see cref="ResolvedCatalog"/> already uses; what is
    /// new is the <see cref="Repository"/> block. Read only after <see cref="RepositoryVerifier"/>
    /// accepted the file.
    /// </summary>
    public sealed class RepositoryManifest
    {
        /// <summary>The only <c>schemaVersion</c> this build knows; any other is rejected.</summary>
        public const int CurrentSchemaVersion = 1;

        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("repository")]
        public RepositoryInfo Repository { get; set; } = new();

        [JsonPropertyName("generatedAt")]
        public DateTimeOffset GeneratedAt { get; set; }

        [JsonPropertyName("providers")]
        public List<RepositoryProvider> Providers { get; set; } = new();
    }

    public sealed class RepositoryInfo
    {
        /// <summary>Stable reverse-DNS id (<c>io.github.owner.tizen</c>). Clients pin it: it never changes.</summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("maintainer")]
        public string Maintainer { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("homepage")]
        public string? Homepage { get; set; }
    }

    /// <summary>One app of a repository: a <c>third-party-apps.json</c> provider with its releases resolved.</summary>
    public sealed class RepositoryProvider
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("category")]
        public string Category { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("prefix")]
        public string? Prefix { get; set; }

        [JsonPropertyName("take")]
        public int Take { get; set; } = 1;

        [JsonPropertyName("buildInfo")]
        public ProviderBuildInfo? BuildInfo { get; set; }

        [JsonPropertyName("source_url")]
        public string? SourceUrl { get; set; }

        [JsonPropertyName("adult")]
        public bool Adult { get; set; }

        /// <summary><c>root</c>, <c>closed-source</c>, <c>self-updating</c>: as in the official catalog.</summary>
        [JsonPropertyName("flags")]
        public List<string> Flags { get; set; } = new();

        [JsonPropertyName("cert_level")]
        public string? CertLevel { get; set; }

        [JsonPropertyName("releases")]
        public List<GitHubRelease> Releases { get; set; } = new();
    }
}
