using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using JIT.NuGetStrongName.Inspection;
using JIT.NuGetStrongName.Packaging;

namespace JIT.NuGetStrongName.Processing;

/// <summary>A verification result; warnings are reported but do not fail the run.</summary>
public sealed record VerificationFinding(string EntryPath, string Message, bool IsWarning = false);

/// <summary>
///     Checks an extracted package: every managed asset in lib/, ref/ and runtimes/*/lib/ is strong-named for its
///     role, every reference to an assembly of the package carries that assembly's actual token, no
///     assembly-qualified type name in a custom attribute still says PublicKeyToken=null for a package assembly,
///     and same-named assemblies share one token across all frameworks and runtimes.
/// </summary>
public static class PackageVerifier
{
    private const string NullPublicKeyToken = "PublicKeyToken=null";

    public static List<PackageAsset> CollectAssets(PackageArchive archive)
    {
        var assets = new List<PackageAsset>();
        foreach (ArchiveEntry entry in archive.Entries
                     .Where(e => !e.IsDirectory && PackageLayout.HasAssemblyExtension(e.Name)))
        {
            AssetGroupKey? group = PackageLayout.GetAssetGroup(entry.Name);
            if (group is null)
            {
                continue;
            }

            assets.Add(new PackageAsset
                       {
                           EntryPath = entry.Name,
                           FullPath = entry.FullPath,
                           Group = group,
                           Info = AssemblyInspector.Inspect(entry.FullPath),
                       });
        }

        return assets;
    }

    public static List<VerificationFinding> Verify(IReadOnlyList<PackageAsset> assets, string? requiredToken)
    {
        var findings = new List<VerificationFinding>();
        List<PackageAsset> managed = assets.Where(a => a.Info is not null).ToList();
        List<PackageAsset> primary = managed.Where(a => !IsSatellite(a)).ToList();

        foreach (PackageAsset asset in managed)
        {
            AssemblyInfo info = asset.Info!;
            if (!info.IsStrongNamedForRole(asset.Group.IsReference))
            {
                findings.Add(new VerificationFinding(asset.EntryPath,
                    $"not strong-named (signature: {info.Signature})"));
            }

            if (requiredToken is not null && !info.PublicKeyToken.EqualsIgnoreCase(requiredToken))
            {
                findings.Add(new VerificationFinding(asset.EntryPath,
                    $"public key token {info.PublicKeyToken ?? "null"} is not the required {requiredToken}"));
            }

            foreach (AssemblyReferenceInfo reference in info.References)
            {
                PackageAsset? target = Resolve(primary, asset, reference.Name);
                if (target is null)
                {
                    if (reference.PublicKeyToken is null && info.HasPublicKey)
                    {
                        // Outside this package: the caller may sign it separately
                        // (planner reports the same as a warning).
                        findings.Add(new VerificationFinding(asset.EntryPath,
                            $"strong-named assembly references unsigned assembly '{reference.Name}' " +
                            $"from outside the package",
                            true));
                    }

                    continue;
                }

                if (!reference.PublicKeyToken.EqualsIgnoreCase(target.Info!.PublicKeyToken))
                {
                    findings.Add(new VerificationFinding(asset.EntryPath,
                        $"reference '{reference.Name}' has token {reference.PublicKeyToken ?? "null"} " +
                        $"but {target.EntryPath} has {target.Info.PublicKeyToken ?? "null"}"));
                }
            }
        }

        HashSet<string> packageNames = primary.Select(a => a.Info!.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (PackageAsset asset in managed.Where(a => a.Info!.HasPublicKey))
        {
            foreach (string stale in FindNullTokenTypeNames(asset.FullPath, packageNames))
            {
                findings.Add(new VerificationFinding(asset.EntryPath,
                    $"custom attribute still names '{stale}' with PublicKeyToken=null"));
            }
        }

        foreach (IGrouping<string, PackageAsset> sameName in primary.GroupBy(a => a.Info!.Name,
                     StringComparer.OrdinalIgnoreCase))
        {
            List<string> tokens = sameName.Select(a => a.Info!.PublicKeyToken ?? "null")
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (tokens.Count > 1)
            {
                findings.Add(new VerificationFinding(sameName.First().EntryPath,
                    $"assembly '{sameName.Key}' has different public key tokens across asset groups: " +
                    $"{string.Join(", ", tokens)}"));
            }
        }

        return findings;
    }

    private static PackageAsset? Resolve(List<PackageAsset> primary, PackageAsset from, string name)
    {
        bool Matches(PackageAsset a)
        {
            return a.Info!.Name.EqualsIgnoreCase(name);
        }

        return primary.FirstOrDefault(a => Matches(a) && a.Group.Path.EqualsIgnoreCase(from.Group.Path))
               ?? (from.Group.CompanionLibPath is { } lib
                   ? primary.FirstOrDefault(a => Matches(a) && a.Group.Path.EqualsIgnoreCase(lib))
                   : null)
               ?? primary.FirstOrDefault(Matches);
    }

    private static IEnumerable<string> FindNullTokenTypeNames(string path, IReadOnlySet<string> packageNames)
    {
        using var pe = new PEReader(File.OpenRead(path));
        MetadataReader md = pe.GetMetadataReader();
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CustomAttributeHandle handle in md.CustomAttributes)
        {
            string text = Encoding.UTF8.GetString(md.GetBlobBytes(md.GetCustomAttribute(handle).Value));
            int index = text.IndexOfIgnoreCase(NullPublicKeyToken);
            while (index >= 0)
            {
                // "<Type>, <Assembly>, Version=..., Culture=..., PublicKeyToken=null"
                string head = text[..index];
                int versionAt = head.LastIndexOfIgnoreCase(", Version=");
                if (versionAt > 0)
                {
                    int nameStart = head.LastIndexOf(',', versionAt - 1) + 1;
                    string assemblyName = head[nameStart..versionAt].Trim();
                    if (packageNames.Contains(assemblyName))
                    {
                        found.Add(assemblyName);
                    }
                }

                index = text.IndexOfIgnoreCase(NullPublicKeyToken, index + 1);
            }
        }

        return found;
    }

    private static bool IsSatellite(PackageAsset asset)
    {
        return !string.IsNullOrEmpty(asset.Info!.Culture) && !asset.Info.Culture.EqualsIgnoreCase("neutral");
    }
}
