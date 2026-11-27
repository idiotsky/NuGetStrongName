using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using JIT.NuGetStrongName.Signing;

namespace JIT.NuGetStrongName.Inspection;

public sealed record AssemblyReferenceInfo(string Name, string? PublicKeyToken);

public sealed record AssemblyInfo(
    string Name,
    Version Version,
    string Culture,
    string? PublicKeyToken,
    SignatureState Signature,
    bool IsReferenceAssembly,
    IReadOnlyList<AssemblyReferenceInfo> References,
    bool IsILOnly = true,
    bool HasAuthenticode = false)
{
    public bool HasPublicKey => PublicKeyToken is not null;

    /// <summary>
    ///     Strong-named for its role: implementation assemblies need a verifiable signature; reference
    ///     assemblies (never loaded for execution) conventionally only carry the public key, often public-signed.
    /// </summary>
    public bool IsStrongNamedForRole(bool referenceRole)
    {
        return Signature switch
        {
            SignatureState.Valid or SignatureState.PlatformKey => true,
            SignatureState.Invalid => referenceRole || IsReferenceAssembly,
            _ => false,
        };
    }
}

public static class AssemblyInspector
{
    /// <summary>
    ///     Returns null when the file is not a managed assembly (not a PE, native image, netmodule).
    ///     Throws <see cref="InvalidDataException" /> for a PE with a CLI header whose metadata is corrupt.
    /// </summary>
    public static AssemblyInfo? Inspect(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Inspect(stream);
    }

    public static AssemblyInfo? Inspect(Stream stream)
    {
        using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen | PEStreamOptions.PrefetchEntireImage);
        CorHeader? corHeader;
        try
        {
            corHeader = pe.PEHeaders.CorHeader;
        }
        catch (BadImageFormatException)
        {
            return null; // not a PE image at all
        }

        if (corHeader is null || corHeader.MetadataDirectory.Size == 0)
        {
            return null; // native image
        }

        try
        {
            MetadataReader md = pe.GetMetadataReader();
            if (!md.IsAssembly)
            {
                return null;
            }

            AssemblyDefinition def = md.GetAssemblyDefinition();
            byte[] publicKey = md.GetBlobBytes(def.PublicKey);
            List<AssemblyReferenceInfo> references = md.AssemblyReferences
                .Select(md.GetAssemblyReference)
                .Select(r => new AssemblyReferenceInfo(
                            md.GetString(r.Name),
                            TokenOf(md.GetBlobBytes(r.PublicKeyOrToken), (r.Flags & AssemblyFlags.PublicKey) != 0)))
                .ToList();

            return new AssemblyInfo(
                md.GetString(def.Name),
                def.Version,
                md.GetString(def.Culture),
                publicKey.Length == 0 ? null : Convert.ToHexStringLower(StrongNameIdentity.ComputeToken(publicKey)),
                StrongNameIdentity.Verify(pe),
                HasReferenceAssemblyAttribute(md),
                references,
                (corHeader.Flags & CorFlags.ILOnly) != 0 && corHeader.ManagedNativeHeaderDirectory.Size == 0,
                pe.PEHeaders.PEHeader!.CertificateTableDirectory.Size > 0);
        }
        catch (BadImageFormatException ex)
        {
            throw new InvalidDataException($"corrupt managed assembly: {ex.Message}", ex);
        }
    }

    private static string? TokenOf(byte[] keyOrToken, bool isFullKey)
    {
        if (keyOrToken.Length == 0)
        {
            return null;
        }

        return Convert.ToHexStringLower(isFullKey ? StrongNameIdentity.ComputeToken(keyOrToken) : keyOrToken);
    }

    private static bool HasReferenceAssemblyAttribute(MetadataReader md)
    {
        foreach (CustomAttributeHandle handle in md.GetAssemblyDefinition().GetCustomAttributes())
        {
            CustomAttribute attribute = md.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference)
            {
                continue;
            }

            EntityHandle parent = md.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
            if (parent.Kind == HandleKind.TypeReference)
            {
                TypeReference type = md.GetTypeReference((TypeReferenceHandle)parent);
                if (md.StringComparer.Equals(type.Name, "ReferenceAssemblyAttribute")
                    && md.StringComparer.Equals(type.Namespace, "System.Runtime.CompilerServices"))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
