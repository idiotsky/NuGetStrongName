using JIT.NuGetStrongName.Inspection;

namespace JIT.NuGetStrongName.Processing;

public sealed class PackageAsset
{
    public required string EntryPath { get; init; }

    public required string FullPath { get; init; }

    public required AssetGroupKey Group { get; init; }

    /// <summary>Null for native (non-managed) images.</summary>
    public AssemblyInfo? Info { get; init; }

    public string FileName => EntryPath[(Group.Path.Length + 1)..];
}

public enum AssetAction
{
    Native,
    Preserve,
    Sign,
    Resign,
    Fail,
}

public sealed class AssetPlan
{
    public required PackageAsset Asset { get; init; }

    public AssetAction Action { get; set; }

    /// <summary>Assembly reference name → public key token to write.</summary>
    public Dictionary<string, string> ReferenceRewrites { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Issues { get; } = [];
}

public sealed class SigningPlan
{
    public required IReadOnlyList<AssetPlan> Assets { get; init; }

    /// <summary>
    ///     Names of every non-satellite assembly this package signs or re-signs (used to add the signing key's
    ///     public key to matching InternalsVisibleTo friends).
    /// </summary>
    public required IReadOnlySet<string> SignedNames { get; init; }

    public bool RequiresAssemblyWork =>
        Assets.Any(a => a.Action is AssetAction.Sign or AssetAction.Resign or AssetAction.Fail);

    public IEnumerable<string> Failures => Assets.Where(a => a.Action == AssetAction.Fail)
        .SelectMany(a => a.Issues.Select(i => $"{a.Asset.EntryPath}: {i}"));
}

/// <summary>
///     Decides, per asset group, which assemblies are signed, preserved or cannot be processed, and builds the
///     exact identity map used to rewrite assembly references. Same-named assemblies are never matched across
///     unrelated frameworks; a runtime/ref group falls back to the lib group of the same framework.
/// </summary>
public static class SigningPlanner
{
    /// <param name="assets"></param>
    /// <param name="signingKeyToken">Token of the signing key, or null when only checking whether work is required.</param>
    /// <param name="assemblyMappings">Explicit cross-package mappings: assembly name → token.</param>
    public static SigningPlan Plan(IReadOnlyList<PackageAsset> assets, string? signingKeyToken,
        IReadOnlyDictionary<string, string> assemblyMappings)
    {
        List<AssetPlan> plans = assets.Select(a => new AssetPlan { Asset = a }).ToList();
        List<AssetPlan> managed = plans.Where(p => p.Asset.Info is not null).ToList();

        // Pass 1: which assemblies need a (new) signature regardless of references.
        foreach (AssetPlan plan in plans)
        {
            AssemblyInfo? info = plan.Asset.Info;
            plan.Action = info is null
                ? AssetAction.Native
                : info.IsStrongNamedForRole(plan.Asset.Group.IsReference)
                    ? AssetAction.Preserve
                    : AssetAction.Sign;
        }

        const string pendingKey = "<signing-key>";
        string keyToken = signingKeyToken ?? pendingKey;

        string? FinalToken(AssetPlan p)
        {
            return p.Action is AssetAction.Sign or AssetAction.Resign ? keyToken : p.Asset.Info!.PublicKeyToken;
        }

        List<AssetPlan> nonSatellite = managed.Where(p => !IsSatellite(p)).ToList();
        ILookup<string, AssetPlan> byGroup = nonSatellite.ToLookup(p => p.Asset.Group.Path,
            StringComparer.OrdinalIgnoreCase);

        AssetPlan? Resolve(AssetPlan from, string name)
        {
            AssetPlan? Find(string groupPath)
            {
                return byGroup[groupPath].FirstOrDefault(p => p.Asset.Info!.Name.EqualsIgnoreCase(name));
            }

            AssetPlan? local = Find(from.Asset.Group.Path);
            if (local is not null)
            {
                return local;
            }

            if (from.Asset.Group.CompanionLibPath is { } companion && Find(companion) is { } fromLib)
            {
                return fromLib;
            }

            // Package-wide fallback only when every same-named candidate ends up with one identity.
            List<AssetPlan> candidates = nonSatellite.Where(p => p.Asset.Info!.Name.EqualsIgnoreCase(name)).ToList();
            return candidates.Count > 0 && candidates.Select(FinalToken).Distinct().Count() == 1 ? candidates[0] : null;
        }

        // Pass 2: references. Signing an assembly can change tokens others must point at; tokens only move
        // from null to the signing key, so one pass over the final identities is enough.
        foreach (AssetPlan plan in managed)
        {
            AssemblyInfo info = plan.Asset.Info!;
            foreach (AssemblyReferenceInfo reference in info.References.Where(r => r.PublicKeyToken is null))
            {
                string? target = null;
                if (Resolve(plan, reference.Name) is { } inPackage)
                {
                    target = FinalToken(inPackage);
                }
                else if (assemblyMappings.TryGetValue(reference.Name, out string? mapped))
                {
                    target = mapped.Length == 0 ? keyToken : mapped;
                }

                if (target is not null)
                {
                    plan.ReferenceRewrites[reference.Name] = target;
                }
                else
                {
                    plan.Issues.Add($"references unsigned assembly '{reference.Name}' that is neither in " +
                                    $"this asset group nor mapped (use --map-assembly)");
                }
            }

            if (plan.ReferenceRewrites.Count > 0 && plan.Action == AssetAction.Preserve)
            {
                plan.Action = AssetAction.Resign;
            }
        }

        // Pass 3: an existing identity can only be re-signed with its own key.
        foreach (AssetPlan plan in managed.Where(p => p.Action == AssetAction.Resign ||
                                                      (p.Action == AssetAction.Sign && p.Asset.Info!.HasPublicKey)))
        {
            string? existing = plan.Asset.Info!.PublicKeyToken;
            if (existing is not null && signingKeyToken is not null && !existing.EqualsIgnoreCase(signingKeyToken))
            {
                plan.Action = AssetAction.Fail;
                plan.Issues.Add($"already carries public key token {existing}; rewriting or re-signing it requires " +
                                $"that key, which was not supplied");
            }
            else
            {
                plan.Action = AssetAction.Resign;
            }
        }

        // Cecil drops native code (C++/CLI mixed mode) and ReadyToRun data when it rewrites an image.
        foreach (AssetPlan plan in managed.Where(p => p.Action is AssetAction.Sign or AssetAction.Resign &&
                                                      !p.Asset.Info!.IsILOnly))
        {
            plan.Action = AssetAction.Fail;
            plan.Issues.Add("is a mixed-mode or ReadyToRun image, which cannot be rewritten safely; supply a " +
                            "strong-named build of it");
        }

        // Unresolvable null-token references would leave stale references in a signed assembly.
        foreach (AssetPlan plan in managed.Where(p => p.Action is AssetAction.Sign or AssetAction.Resign &&
                                                      p.Issues.Count > 0))
        {
            plan.Action = AssetAction.Fail;
        }

        HashSet<string> signedNames = nonSatellite
            .Where(p => p.Action is AssetAction.Sign or AssetAction.Resign)
            .Select(p => p.Asset.Info!.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new SigningPlan { Assets = plans, SignedNames = signedNames };
    }

    private static bool IsSatellite(AssetPlan plan)
    {
        return !string.IsNullOrEmpty(plan.Asset.Info!.Culture) &&
               !plan.Asset.Info.Culture.EqualsIgnoreCase("neutral");
    }
}
