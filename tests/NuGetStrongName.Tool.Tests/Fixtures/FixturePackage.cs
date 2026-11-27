using System.IO.Compression;
using System.Text;

namespace JIT.NuGetStrongName.Tool.Tests.Fixtures;

/// <summary>Writes a .nupkg with the OPC parts NuGet itself produces.</summary>
public sealed class FixturePackage
{
    public required string Id { get; init; }

    public string Version { get; init; } = "1.0.0";

    public Dictionary<string, byte[]> Files { get; } = new();

    /// <summary>(targetFramework, id, version) dependency entries.</summary>
    public List<(string Framework, string Id, string Version)> Dependencies { get; } = [];

    public bool WithPackageSignature { get; init; }

    /// <summary>Line ending for the generated XML parts; null keeps whatever this source file was checked out with.</summary>
    public string? NewLine { get; init; }

    public FixturePackage Add(string entryPath, byte[] content)
    {
        Files[entryPath] = content;
        return this;
    }

    public FixturePackage Add(string groupPath, FixtureAssembly assembly)
    {
        byte[] dll = assembly.Build(out byte[]? pdb);
        Files[$"{groupPath}/{assembly.Name}.dll"] = dll;
        if (pdb is not null)
        {
            Files[$"{groupPath}/{assembly.Name}.pdb"] = pdb;
        }

        return this;
    }

    public string Write(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{Id}.{Version}.nupkg");
        using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Put(zip, "_rels/.rels",
            $"""
             <?xml version="1.0" encoding="utf-8"?>
             <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Type="http://schemas.microsoft.com/packaging/2010/07/manifest" Target="/{Id}.nuspec" Id="R1" /><Relationship Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="/package/services/metadata/core-properties/abc.psmdcp" Id="R2" /></Relationships>
             """);
        Put(zip, $"{Id}.nuspec", Nuspec());
        foreach ((string name, byte[] content) in Files)
        {
            using Stream stream = zip.CreateEntry(name).Open();
            stream.Write(content);
        }

        Put(zip, "[Content_Types].xml",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml" /><Default Extension="nuspec" ContentType="application/octet" /><Default Extension="dll" ContentType="application/octet" /><Default Extension="psmdcp" ContentType="application/vnd.openxmlformats-package.core-properties+xml" /></Types>
            """);
        Put(zip, "package/services/metadata/core-properties/abc.psmdcp",
            $"""
             <?xml version="1.0" encoding="utf-8"?>
             <coreProperties xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns="http://schemas.openxmlformats.org/package/2006/metadata/core-properties"><dc:creator>test</dc:creator><dc:identifier>{Id}</dc:identifier><version>{Version}</version></coreProperties>
             """);
        if (WithPackageSignature)
        {
            Put(zip, ".signature.p7s", "not a real signature");
        }

        return path;
    }

    private string Nuspec()
    {
        IEnumerable<string> groups = Dependencies.GroupBy(d => d.Framework).Select(g =>
                $"<group targetFramework=\"{g.Key}\">{string.Concat(g.Select(d => $"<dependency id=\"{d.Id}\" version=\"{d.Version}\" />"))}</group>");
        return $"""
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                  <metadata>
                    <id>{Id}</id>
                    <version>{Version}</version>
                    <authors>test</authors>
                    <description>fixture</description>
                    <dependencies>{string.Concat(groups)}</dependencies>
                  </metadata>
                </package>
                """;
    }

    private void Put(ZipArchive zip, string name, string content)
    {
        if (NewLine is not null)
        {
            content = content.ReplaceLineEndings(NewLine);
        }

        using Stream stream = zip.CreateEntry(name).Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }
}
