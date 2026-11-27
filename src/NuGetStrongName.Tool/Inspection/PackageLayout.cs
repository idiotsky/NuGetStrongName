namespace JIT.NuGetStrongName.Inspection;

/// <summary>
///     A compile or runtime asset group of a package: <c>lib/&lt;tfm&gt;</c>, <c>ref/&lt;tfm&gt;</c> or
///     <c>runtimes/&lt;rid&gt;/lib/&lt;tfm&gt;</c>.
/// </summary>
// ReSharper disable once NotAccessedPositionalProperty.Global
public sealed record AssetGroupKey(string Path, string Kind, string? Framework, string? RuntimeIdentifier)
{
    public bool IsReference => Kind == "ref";

    /// <summary>The implementation group whose assemblies this group binds against at run time (same TFM under <c>lib/</c>).</summary>
    public string? CompanionLibPath => Kind == "lib" || Framework is null ? null : $"lib/{Framework}";
}

public static class PackageLayout
{
    private static readonly string[] m_assemblyExtensions = [".dll", ".exe", ".winmd"];

    private static string NormalizeEntryPath(string entryPath)
    {
        return entryPath.Replace('\\', '/');
    }

    public static bool HasAssemblyExtension(string entryPath)
    {
        return m_assemblyExtensions.Any(entryPath.EndsWithIgnoreCase);
    }

    /// <summary>Returns the asset group for a package entry, or null when the entry is outside lib/, ref/ and runtimes/*/lib/.</summary>
    public static AssetGroupKey? GetAssetGroup(string entryPath)
    {
        string[] segments = NormalizeEntryPath(entryPath).Split('/');
        if (segments.Length < 2)
        {
            return null;
        }

        string root = segments[0];
        if (root.EqualsIgnoreCase("lib") || root.EqualsIgnoreCase("ref"))
        {
            string kind = root.ToLowerInvariant();
            return segments.Length == 2
                ? new AssetGroupKey(kind, kind, null, null) // legacy framework-less layout: lib/Foo.dll
                : new AssetGroupKey($"{kind}/{segments[1]}", kind, segments[1], null);
        }

        if (root.EqualsIgnoreCase("runtimes") && segments.Length >= 5 && segments[2].EqualsIgnoreCase("lib"))
        {
            return new AssetGroupKey($"runtimes/{segments[1]}/lib/{segments[3]}", "runtime", segments[3], segments[1]);
        }

        return null;
    }
}
