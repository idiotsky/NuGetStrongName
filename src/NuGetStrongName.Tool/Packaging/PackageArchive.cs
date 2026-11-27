using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace JIT.NuGetStrongName.Packaging;

/// <summary>An extracted copy of a .nupkg inside a private workspace directory. The input file is never modified.</summary>
public sealed class PackageArchive
{
    public const string PackageSignatureEntry = ".signature.p7s";

    private PackageArchive(string root, IReadOnlyList<ArchiveEntry> entries)
    {
        Root = root;
        Entries = entries;
    }

    public string Root { get; }

    public IReadOnlyList<ArchiveEntry> Entries { get; }

    /// <summary>Upper bounds that protect the workspace from zip bombs.</summary>
    private const long MaxExtractedBytes = 4L * 1024 * 1024 * 1024;

    private const int MaxEntries = 100_000;

    public ArchiveEntry NuspecEntry
    {
        get
        {
            List<ArchiveEntry> manifests = Entries
                .Where(e => !e.Name.Contains('/') && e.Name.EndsWithIgnoreCase(".nuspec"))
                .ToList();
            return manifests.Count == 1
                ? manifests[0]
                : throw new InvalidDataException(
                    $"The package must contain exactly one root .nuspec manifest, found {manifests.Count}.");
        }
    }

    public static PackageArchive Extract(string packagePath, string root, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(root);
        var fullRoot = $"{Path.GetFullPath(root)}{Path.DirectorySeparatorChar}";
        var entries = new List<ArchiveEntry>();
        using ZipArchive zip = ZipFile.OpenRead(packagePath);
        if (zip.Entries.Count > MaxEntries)
        {
            throw new InvalidDataException($"The package has {zip.Entries.Count} entries; the limit is {MaxEntries}.");
        }

        if (zip.Entries.Sum(e => e.Length) > MaxExtractedBytes)
        {
            throw new InvalidDataException($"The package expands beyond {MaxExtractedBytes} bytes.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = entry.FullName.Replace('\\', '/');
            ValidateEntryName(name);
            if (!seen.Add(name.TrimEnd('/')))
            {
                throw new InvalidDataException($"Package entry '{entry.FullName}' duplicates another entry " +
                                               $"(names differ only in case).");
            }

            bool isDirectory = name.EndsWith('/');
            string target = Path.GetFullPath(Path.Combine(root, name.TrimEnd('/')));
            if (!target.StartsWith(fullRoot, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Package entry '{entry.FullName}' escapes the package root.");
            }

            if (isDirectory)
            {
                Directory.CreateDirectory(target);
            }
            else
            {
                if (File.Exists(target))
                {
                    throw new InvalidDataException($"Package entry '{entry.FullName}' collides with another entry on " +
                                                   $"this file system (case-insensitive path?).");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target);
            }

            entries.Add(new ArchiveEntry(name, target, isDirectory, entry.LastWriteTime));
        }

        return new PackageArchive(root, entries);
    }

    /// <summary>
    ///     Rejects names that would be normalized differently on disk than in the archive (and so be re-emitted
    ///     verbatim): rooted paths, empty/"."/".." segments, alternate data streams and invalid characters.
    /// </summary>
    private static void ValidateEntryName(string name)
    {
        string[] segments = name.TrimEnd('/').Split('/');
        HashSet<char> invalid = [.. Path.GetInvalidFileNameChars(), ':', '*', '?', '"', '<', '>', '|'];
        if (name.Length == 0 || name.StartsWith('/')
                             || segments.Any(s => s.Length == 0 || s == "." || s == ".." || s.Any(invalid.Contains)))
        {
            throw new InvalidDataException($"Package entry '{name}' has an unsafe or invalid path.");
        }
    }

    /// <summary>Writes a new package from the workspace, preserving entry order and timestamps, without the package signature.</summary>
    public void WritePackage(string destination, IReadOnlyDictionary<string, string> renamedEntries)
    {
        using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (ArchiveEntry entry in Entries)
        {
            if (entry.Name.EqualsIgnoreCase(PackageSignatureEntry))
            {
                continue;
            }

            string name = renamedEntries.TryGetValue(entry.Name, out string? renamed) ? renamed : entry.Name;
            ZipArchiveEntry zipEntry = zip.CreateEntry(name, CompressionLevel.Optimal);
            zipEntry.LastWriteTime = entry.LastWriteTime;
            if (!entry.IsDirectory)
            {
                using FileStream source = File.OpenRead(entry.FullPath);
                using Stream target = zipEntry.Open();
                source.CopyTo(target);
            }
        }
    }
}

public sealed record ArchiveEntry(string Name, string FullPath, bool IsDirectory, DateTimeOffset LastWriteTime);

public sealed record PackageDependencyMapping(string NewId, string NewVersion);

/// <summary>One nuspec dependency renamed by a package mapping; serialized into the report.</summary>
// ReSharper disable NotAccessedPositionalProperty.Global
public sealed record DependencyChange(
    string TargetFramework,
    string OriginalId,
    string OriginalVersion,
    string NewId,
    string NewVersion);
// ReSharper restore NotAccessedPositionalProperty.Global

/// <summary>Edits the manifest (.nuspec) and OPC core properties of an extracted package.</summary>
public static class ManifestEditor
{
    public static (string Id, string Version) ReadIdentity(string nuspecPath)
    {
        XDocument doc = LoadXml(nuspecPath, LoadOptions.None);
        XElement metadata = Metadata(doc);
        return (Value(metadata, "id"), Value(metadata, "version"));
    }

    /// <summary>Returns the dependency changes the mappings would make, without touching the file.</summary>
    public static IReadOnlyList<DependencyChange> PreviewDependencyChanges(string nuspecPath,
        IReadOnlyDictionary<string, PackageDependencyMapping> mappings)
    {
        XDocument doc = LoadXml(nuspecPath, LoadOptions.None);
        return MapDependencies(Metadata(doc), mappings);
    }

    /// <summary>
    ///     Applies the dependency mappings and the new package identity, saves the manifest, and returns the
    ///     dependency changes made.
    /// </summary>
    public static IReadOnlyList<DependencyChange> RewriteManifest(string nuspecPath,
        IReadOnlyDictionary<string, PackageDependencyMapping> mappings, string newId, string newVersion)
    {
        XDocument doc = LoadXml(nuspecPath, LoadOptions.PreserveWhitespace);
        XElement metadata = Metadata(doc);
        List<DependencyChange> changes = MapDependencies(metadata, mappings);
        Element(metadata, "id").Value = newId;
        Element(metadata, "version").Value = newVersion;
        SaveXml(doc, nuspecPath);
        return changes;
    }

    /// <summary>Rewrites the mapped dependencies in the in-memory metadata and returns what changed.</summary>
    private static List<DependencyChange> MapDependencies(XElement metadata,
        IReadOnlyDictionary<string, PackageDependencyMapping> mappings)
    {
        var changes = new List<DependencyChange>();
        foreach (XElement dependency in metadata.Descendants().Where(e => e.Name.LocalName == "dependency"))
        {
            var id = (string?)dependency.Attribute("id");
            if (id is null || !mappings.TryGetValue(id, out PackageDependencyMapping? mapping))
            {
                continue;
            }

            changes.Add(new DependencyChange(
                (string?)dependency.Parent?.Attribute("targetFramework") ?? "",
                id,
                (string?)dependency.Attribute("version") ?? "",
                mapping.NewId,
                mapping.NewVersion));
            dependency.SetAttributeValue("id", mapping.NewId);
            dependency.SetAttributeValue("version", mapping.NewVersion);
        }

        return changes;
    }

    public static void UpdateCoreProperties(string psmdcpPath, string newId, string newVersion)
    {
        XDocument doc = LoadXml(psmdcpPath, LoadOptions.PreserveWhitespace);
        foreach (XElement element in doc.Descendants())
        {
            if (element.Name.LocalName == "identifier")
            {
                element.Value = newId;
            }
            else if (element.Name.LocalName == "version")
            {
                element.Value = newVersion;
            }
        }

        SaveXml(doc, psmdcpPath);
    }

    /// <summary>
    ///     Saves without reformatting, keeping the file's original line endings. The XML reader normalizes CRLF to
    ///     LF and <see cref="XDocument.Save(string, SaveOptions)" /> would write the platform's newline, so the same
    ///     package would otherwise produce different manifests on Windows and Linux.
    /// </summary>
    private static void SaveXml(XDocument doc, string path)
    {
        string newLine = File.ReadAllText(path).Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var settings = new XmlWriterSettings
                       {
                           Indent = false,
                           NewLineChars = newLine,
                           NewLineHandling = NewLineHandling.Replace,
                       };
        if (doc.Declaration?.Encoding is { Length: > 0 } encoding)
        {
            settings.Encoding = Encoding.GetEncoding(encoding);
        }

        var buffer = new MemoryStream();
        using (var writer = XmlWriter.Create(buffer, settings))
        {
            doc.Save(writer);
        }

        File.WriteAllBytes(path, buffer.ToArray());
    }

    /// <summary>Loads package XML without DTD processing (no entity-expansion attacks).</summary>
    private static XDocument LoadXml(string path, LoadOptions options)
    {
        using var reader = XmlReader.Create(path,
            new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreWhitespace = !options.HasFlag(LoadOptions.PreserveWhitespace),
            });
        return XDocument.Load(reader, options);
    }

    private static XElement Element(XElement metadata, string name)
    {
        return metadata.Elements().FirstOrDefault(e => e.Name.LocalName == name)
               ?? throw new InvalidDataException($"The .nuspec manifest has no <{name}>.");
    }

    private static XElement Metadata(XDocument doc)
    {
        return doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata")
               ?? throw new InvalidDataException("The .nuspec manifest has no <metadata> element.");
    }

    private static string Value(XElement metadata, string name)
    {
        return metadata.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim()
               ?? throw new InvalidDataException($"The .nuspec manifest has no <{name}>.");
    }
}
