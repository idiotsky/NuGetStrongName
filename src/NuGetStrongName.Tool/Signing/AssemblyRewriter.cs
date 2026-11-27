using System.Reflection.PortableExecutable;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace JIT.NuGetStrongName.Signing;

public sealed record RewriteResult(IReadOnlyList<string> ReferencesRewritten, IReadOnlyList<string> Warnings);

/// <summary>
///     Rewrites assembly references, InternalsVisibleTo friends and type-valued custom attribute arguments,
///     then strong-name signs the assembly with Mono.Cecil. Assembly versions are never changed.
/// </summary>
public static class AssemblyRewriter
{
    public static RewriteResult Rewrite(
        string path,
        StrongNameKey key,
        IReadOnlyDictionary<string, string> referenceRewrites,
        IReadOnlySet<string> signedFriendNames,
        IEnumerable<string> searchDirectories)
    {
        var warnings = new List<string>();
        var rewritten = new List<string>();
        SymbolKind symbols = DetectSymbols(path);
        if (symbols == SymbolKind.Native)
        {
            warnings.Add("native (Windows) PDB kept unchanged; it no longer matches the rewritten assembly");
        }

        using var resolver = new DefaultAssemblyResolver();
        foreach (string dir in searchDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            resolver.AddSearchDirectory(dir);
        }

        var readerParameters = new ReaderParameters
                               {
                                   InMemory = true,
                                   ReadingMode = ReadingMode.Immediate,
                                   AssemblyResolver = resolver,
                                   ReadSymbols = symbols is SymbolKind.Portable or SymbolKind.Embedded,
                                   SymbolReaderProvider = symbols switch
                                   {
                                       SymbolKind.Portable => new PortablePdbReaderProvider(),
                                       SymbolKind.Embedded => new EmbeddedPortablePdbReaderProvider(),
                                       _ => null,
                                   },
                               };

        using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(path, readerParameters);
        ModuleDefinition module = assembly.MainModule;

        // Type arguments in custom attributes are stored as assembly-qualified strings. Cecil maps them onto
        // the module's assembly references, so decoding every attribute up front makes them re-serialize with
        // the rewritten tokens; anything undecodable is reported (and caught later by package verification).
        DecodeCustomAttributes(assembly, warnings);

        Dictionary<string, byte[]> tokens = referenceRewrites.ToDictionary(kv => kv.Key,
            kv => Convert.FromHexString(kv.Value), StringComparer.OrdinalIgnoreCase);
        foreach (AssemblyNameReference reference in module.AssemblyReferences)
        {
            if (!reference.HasPublicKey && (reference.PublicKeyToken is null || reference.PublicKeyToken.Length == 0)
                                        && tokens.TryGetValue(reference.Name, out byte[]? token))
            {
                reference.PublicKeyToken = token;
                rewritten.Add($"{reference.Name}, Version={reference.Version} -> {Convert.ToHexStringLower(token)}");
            }
        }

        RewriteInternalsVisibleTo(assembly, key, signedFriendNames);

        assembly.Name.PublicKey = key.PublicKey;
        assembly.Name.HasPublicKey = true;

        var writerParameters = new WriterParameters
                               {
                                   StrongNameKeyBlob = key.KeyBlob,
                                   DeterministicMvid = false,
                                   WriteSymbols = symbols is SymbolKind.Portable or SymbolKind.Embedded,
                                   SymbolWriterProvider = symbols switch
                                   {
                                       SymbolKind.Portable => new PortablePdbWriterProvider(),
                                       SymbolKind.Embedded => new EmbeddedPortablePdbWriterProvider(),
                                       _ => null,
                                   },
                               };

        assembly.Write(path, writerParameters);
        return new RewriteResult(rewritten, warnings);
    }

    private static void RewriteInternalsVisibleTo(AssemblyDefinition assembly, StrongNameKey key,
        IReadOnlySet<string> signedFriendNames)
    {
        string publicKeyHex = Convert.ToHexStringLower(key.PublicKey);
        const string internalsVisibleToAttribute = "System.Runtime.CompilerServices.InternalsVisibleToAttribute";
        foreach (CustomAttribute attribute in assembly.CustomAttributes.Where
                 (a => a.AttributeType.FullName == internalsVisibleToAttribute &&
                       a.ConstructorArguments.Count == 1))
        {
            if (attribute.ConstructorArguments[0].Value is not string friend || friend.ContainsIgnoreCase("PublicKey"))
            {
                continue;
            }

            string friendName = friend.Split(',')[0].Trim();
            if (signedFriendNames.Contains(friendName))
            {
                attribute.ConstructorArguments[0] = new CustomAttributeArgument(
                    attribute.ConstructorArguments[0].Type, $"{friendName}, PublicKey={publicKeyHex}");
            }
        }
    }

    private static void DecodeCustomAttributes(AssemblyDefinition assembly, List<string> warnings)
    {
        var unreadable = 0;

        void Visit(ICustomAttributeProvider provider)
        {
            if (!provider.HasCustomAttributes)
            {
                return;
            }

            foreach (CustomAttribute attribute in provider.CustomAttributes)
            {
                try
                {
                    _ = attribute.ConstructorArguments.Count + attribute.Fields.Count + attribute.Properties.Count;
                }
                catch (Exception ex) when (ex is AssemblyResolutionException or InvalidOperationException
                                               or NotSupportedException or ArgumentException)
                {
                    unreadable++;
                }
            }
        }

        Visit(assembly);
        foreach (ModuleDefinition module in assembly.Modules)
        {
            Visit(module);
            foreach (TypeDefinition type in module.GetTypes())
            {
                Visit(type);
                foreach (GenericParameter gp in type.GenericParameters)
                {
                    Visit(gp);
                }

                foreach (FieldDefinition field in type.Fields)
                {
                    Visit(field);
                }

                foreach (PropertyDefinition property in type.Properties)
                {
                    Visit(property);
                }

                foreach (EventDefinition @event in type.Events)
                {
                    Visit(@event);
                }

                foreach (MethodDefinition method in type.Methods)
                {
                    Visit(method);
                    Visit(method.MethodReturnType);
                    foreach (ParameterDefinition parameter in method.Parameters)
                    {
                        Visit(parameter);
                    }

                    foreach (GenericParameter gp in method.GenericParameters)
                    {
                        Visit(gp);
                    }
                }
            }
        }

        if (unreadable > 0)
        {
            warnings.Add($"{unreadable} custom attribute(s) could not be decoded and were kept byte-for-byte");
        }
    }

    private enum SymbolKind
    {
        None,
        Portable,
        Embedded,
        Native,
    }

    private static SymbolKind DetectSymbols(string path)
    {
        using (var pe = new PEReader(File.OpenRead(path)))
        {
            if (pe.ReadDebugDirectory().Any(e => e.Type == DebugDirectoryEntryType.EmbeddedPortablePdb))
            {
                return SymbolKind.Embedded;
            }
        }

        string pdb = Path.ChangeExtension(path, ".pdb");
        if (!File.Exists(pdb))
        {
            return SymbolKind.None;
        }

        Span<byte> header = stackalloc byte[4];
        using FileStream stream = File.OpenRead(pdb);
        return stream.Read(header) == 4 && header.SequenceEqual("BSJB"u8) ? SymbolKind.Portable : SymbolKind.Native;
    }
}
