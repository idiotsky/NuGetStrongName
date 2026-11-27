using System.Reflection.PortableExecutable;
using JIT.NuGetStrongName.Signing;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace JIT.NuGetStrongName.Tool.Tests.Fixtures;

/// <summary>Describes a small generated assembly. References are emitted with a null public key token unless given.</summary>
public sealed class FixtureAssembly
{
    public required string Name { get; init; }

    public Version Version { get; init; } = new(1, 2, 3, 4);

    public string? Culture { get; init; }

    /// <summary>Referenced assembly names; "Name=token" pins a token.</summary>
    public string[] References { get; init; } = [];

    /// <summary>Strong-name key (.snk blob) to sign with; null leaves the assembly unsigned.</summary>
    public byte[]? Key { get; init; }

    /// <summary>Only set the public key flag/key without a valid signature (public/delay signing).</summary>
    public bool PublicSignOnly { get; init; }

    public string[] InternalsVisibleTo { get; init; } = [];

    /// <summary>Name of a referenced assembly whose type is used as a typeof() custom attribute argument.</summary>
    public string? TypeArgumentFrom { get; init; }

    public bool ReferenceAssembly { get; init; }

    public bool PortablePdb { get; init; }

    /// <summary>Clears the ILOnly flag, as for a C++/CLI mixed-mode image.</summary>
    public bool NotILOnly { get; init; }

    public byte[] Build()
    {
        return Build(out _);
    }

    public byte[] Build(out byte[]? pdb)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"nsn-fixture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var name = new AssemblyNameDefinition(Name, Version) { Culture = Culture ?? "" };
            using var assembly = AssemblyDefinition.CreateAssembly(name, Name, ModuleKind.Dll);
            ModuleDefinition module = assembly.MainModule;
            var marker = new TypeDefinition(Name, "Marker", TypeAttributes.Public | TypeAttributes.Class,
                module.TypeSystem.Object);
            module.Types.Add(marker);

            var ctor = new MethodDefinition(".ctor",
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName |
                MethodAttributes.RTSpecialName,
                module.TypeSystem.Void);
            ILProcessor il = ctor.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call,
                module.ImportReference(new MethodReference(".ctor", module.TypeSystem.Void, module.TypeSystem.Object)
                                       { HasThis = true }));
            il.Emit(OpCodes.Ret);
            marker.Methods.Add(ctor);

            var references = new Dictionary<string, AssemblyNameReference>();
            foreach (string spec in References)
            {
                string[] parts = spec.Split('=');
                var reference = new AssemblyNameReference(parts[0], Version);
                if (parts.Length == 2)
                {
                    reference.PublicKeyToken = Convert.FromHexString(parts[1]);
                }

                module.AssemblyReferences.Add(reference);
                references[parts[0]] = reference;

                // Use the reference so it is meaningful metadata, like a compiler would.
                var foreign = new TypeReference(parts[0], "Marker", module, reference);
                marker.Fields.Add(new FieldDefinition($"f{parts[0].Replace(".", "")}", FieldAttributes.Public,
                    foreign));
            }

            var corlib = (AssemblyNameReference)module.TypeSystem.CoreLibrary;
            foreach (string friend in InternalsVisibleTo)
            {
                AddAttribute(module, corlib, "System.Runtime.CompilerServices", "InternalsVisibleToAttribute",
                    module.TypeSystem.String, friend);
            }

            if (TypeArgumentFrom is not null)
            {
                var typeType = new TypeReference("System", "Type", module, corlib);
                var target = new TypeReference(TypeArgumentFrom, "Marker", module, references[TypeArgumentFrom]);
                var attrType = new TypeReference("System.ComponentModel", "TypeConverterAttribute", module, corlib);
                var attrCtor = new MethodReference(".ctor", module.TypeSystem.Void, attrType) { HasThis = true };
                attrCtor.Parameters.Add(new ParameterDefinition(typeType));
                var attribute = new CustomAttribute(attrCtor);
                attribute.ConstructorArguments.Add(new CustomAttributeArgument(typeType, target));
                marker.CustomAttributes.Add(attribute);
            }

            if (ReferenceAssembly)
            {
                var attrType = new TypeReference("System.Runtime.CompilerServices", "ReferenceAssemblyAttribute",
                    module, corlib);
                assembly.CustomAttributes.Add(new CustomAttribute(
                    new MethodReference(".ctor", module.TypeSystem.Void, attrType) { HasThis = true }));
            }

            string path = Path.Combine(dir, $"{Name}.dll");
            var parameters = new WriterParameters();
            if (PortablePdb)
            {
                parameters.WriteSymbols = true;
                parameters.SymbolWriterProvider = new PortablePdbWriterProvider();
            }

            if (Key is not null)
            {
                byte[] publicKey = StrongNameKey.FromBlob(Key).PublicKey;
                assembly.Name.PublicKey = publicKey;
                assembly.Name.HasPublicKey = true;
                if (!PublicSignOnly)
                {
                    parameters.StrongNameKeyBlob = Key;
                }
            }

            assembly.Write(path, parameters);
            string pdbPath = Path.ChangeExtension(path, ".pdb");
            pdb = File.Exists(pdbPath) ? File.ReadAllBytes(pdbPath) : null;
            byte[] image = File.ReadAllBytes(path);
            if (NotILOnly)
            {
                // Cecil refuses to write mixed-mode images,
                // so clear COMIMAGE_FLAGS_ILONLY in the CLI header afterward.
                int corHeader;
                using (var pe = new PEReader(new MemoryStream(image)))
                {
                    corHeader = pe.PEHeaders.CorHeaderStartOffset;
                }

                image[corHeader + 16] &= 0xFE;
            }

            return image;
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static void AddAttribute(ModuleDefinition module, IMetadataScope scope, string ns, string type,
        TypeReference argType, object value)
    {
        var attrType = new TypeReference(ns, type, module, scope);
        var ctor = new MethodReference(".ctor", module.TypeSystem.Void, attrType) { HasThis = true };
        ctor.Parameters.Add(new ParameterDefinition(argType));
        var attribute = new CustomAttribute(ctor);
        attribute.ConstructorArguments.Add(new CustomAttributeArgument(argType, value));
        module.Assembly.CustomAttributes.Add(attribute);
    }

    /// <summary>A minimal valid PE image without CLI metadata, standing in for a native DLL.</summary>
    public static byte[] NativeImage()
    {
        // Build a managed image and strip the CLI header data directory, leaving a valid PE that has no metadata.
        byte[] bytes = new FixtureAssembly { Name = "NativeStub" }.Build();
        var peOffset = BitConverter.ToInt32(bytes, 0x3c);
        int optionalHeader = peOffset + 24;
        bool isPe32Plus = BitConverter.ToUInt16(bytes, optionalHeader) == 0x20b;
        int cliDirectory = optionalHeader + (isPe32Plus ? 112 : 96) + 14 * 8;
        Array.Clear(bytes, cliDirectory, 8);
        return bytes;
    }
}
