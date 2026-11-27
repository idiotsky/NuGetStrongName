using JIT.NuGetStrongName.Packaging;

namespace JIT.NuGetStrongName.Reporting;

// ReSharper disable CollectionNeverQueried.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global
// ReSharper disable NotAccessedPositionalProperty.Global
/// <summary>JSON document written to stdout for every invocation.</summary>
public sealed class Report
{
    public required string Command { get; init; }

    /// <summary>signed | already-strong-named | verified | failed | invalid-arguments</summary>
    public required string Status { get; set; }

    public string? PackageId { get; set; }

    public string? Version { get; set; }

    /// <summary>Local input path; null when the package was downloaded.</summary>
    public string? InputPath { get; set; }

    /// <summary>Name of the NuGet source a downloaded package came from.</summary>
    public string? InputSource { get; set; }

    public string? InputSha256 { get; set; }

    public string? OutputPath { get; set; }

    public string? OutputId { get; set; }

    public string? OutputVersion { get; set; }

    public string? OutputSha256 { get; set; }

    public string? PublicKeyToken { get; set; }

    public List<AssetGroupReport> AssetGroups { get; } = [];

    public List<DependencyChange> DependencyChanges { get; } = [];

    public List<string> Warnings { get; } = [];

    public List<string> Errors { get; } = [];

    public required string ToolVersion { get; init; }
}

public sealed class AssetGroupReport
{
    public required string Path { get; init; }

    public List<AssemblyReport> Assemblies { get; } = [];
}

public sealed class AssemblyReport
{
    public required string File { get; init; }

    public string? Name { get; set; }

    public string? AssemblyVersion { get; set; }

    public string? OriginalPublicKeyToken { get; set; }

    public string? PublicKeyToken { get; set; }

    /// <summary>signed | resigned | preserved | strong-named | not-strong-named | native | failed</summary>
    public required string Outcome { get; set; }

    public string? Signature { get; set; }

    public List<string> ReferencesRewritten { get; } = [];

    public List<string> Issues { get; } = [];
}

// ReSharper restore NotAccessedPositionalProperty.Global
// ReSharper restore UnusedAutoPropertyAccessor.Global
// ReSharper restore CollectionNeverQueried.Global
