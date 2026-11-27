# NuGetStrongName

`nuget-strongname` is a cross-platform .NET tool that strong-name signs the managed assemblies of a NuGet package. It
rewrites references between them, then repacks the package and checks the result. It runs on Windows and Linux. It needs
no Windows SDK tools (`sn.exe`, `ildasm`, `ilasm`) and no PowerShell.

## Requirements

- .NET 11 runtime (for running the tool) and .NET 11 SDK (`dotnet tool install` needs the SDK). The tool is
  framework-dependent: cross-platform does not mean runtime-free.

## Install a pinned release

Each GitHub Release `v<version>` contains `NuGetStrongName.Tool.<version>.nupkg` and `SHA256SUMS`.

```sh
version=0.1.0
mkdir -p tool-feed
curl -fL -o tool-feed/NuGetStrongName.Tool.$version.nupkg \
  https://github.com/<owner>/NuGetStrongName/releases/download/v$version/NuGetStrongName.Tool.$version.nupkg
curl -fL -o tool-feed/SHA256SUMS https://github.com/<owner>/NuGetStrongName/releases/download/v$version/SHA256SUMS
(cd tool-feed && sha256sum -c SHA256SUMS)
dotnet tool install --tool-path .tools NuGetStrongName.Tool --version $version --add-source tool-feed
.tools/nuget-strongname --version
```

## Commands

```text
nuget-strongname sign-package (--input <nupkg> | --package-id <id> --version <version>)
                              [--key-file <snk>] [--output <dir>] [--output-version <version>]
                              [--output-id <id>] [--map-package <id>=<newId>@<version>]...
                              [--map-assembly <name>[=<publicKeyToken>]]...
                              [--repository <dir>] [--configfile <nuget.config>] [--timeout <seconds>]
nuget-strongname verify-package (--input <nupkg> | --package-id <id> --version <version>)
                                [--public-key-token <token>] [--repository <dir>] [--configfile <file>] [--timeout <seconds>]
nuget-strongname --help
nuget-strongname --version
```

- Inside a subcommand, `--version` selects the upstream package version. At top level, `--version` prints the tool
  version.
- `sign-package` inspects the package first. If every managed assembly is already strong-named and nothing needs
  rewriting, it exits 0 with `"status": "already-strong-named"` and `"outputPath": null`. In that case it reads no key
  and writes nothing. Otherwise `--key-file`, `--output` and `--output-version` are required. If one is missing, the
  tool fails without prompting (exit 2). The caller decides whether to ask a user.
- Packages are downloaded using the effective NuGet configuration of `--repository` (default: the current directory) or
  of `--configfile`. That includes package source mapping, credentials stored in `nuget.config`, and installed
  credential-provider plugins (never interactive).
- The output is written to `<output>/<output-id>.<output-version>.nupkg` only after verification passes. An existing
  file is never overwritten. The input is never modified.

### Example

```sh
nuget-strongname sign-package --package-id IppDotNetSdkForQuickBooksApiV3 --version 14.7.1.6 \
  --key-file ./keys/consumer.snk --output ./local-feed --output-version 14.7.1.6-signed
```

## What gets signed

- Every managed assembly under `lib/<tfm>/`, `ref/<tfm>/` and `runtimes/<rid>/lib/<tfm>/`, in every TFM and RID,
  including satellite resource assemblies. Native images are detected and left byte-identical.
- One key is used for the whole package, so same-named assemblies share one public key token across TFMs. Assembly
  versions do not change.
- Within each asset group, the assemblies are treated as a set. An exact identity map is built first, then every
  affected `AssemblyRef` is rewritten, including cycles. A `runtimes/` or `ref/` group resolves names against the `lib/`
  group of the same TFM. Type names inside custom attributes (`typeof(...)` arguments) follow the new references.
  `InternalsVisibleTo` friends that are signed in the same package get the full public key.
- Assemblies with a valid existing signature are preserved byte-for-byte. If one of them would need a rewrite, the run
  fails unless the supplied key is its own key. An implementation assembly whose signature does not verify is treated as
  not strong-named. A reference assembly (`ref/` or `[ReferenceAssembly]`) only needs a public key, because public
  signing is the convention there.
- Use `--map-assembly <name>[=<token>]` for a reference to another package's assembly that you sign separately. Without
  a token, the `--key-file` token is used. Use `--map-package Old=New@Version` to rewrite the matching `.nuspec`
  dependencies. Without a mapping, an unsigned reference that cannot be resolved fails the run, because it would
  otherwise leave a stale `PublicKeyToken=null` reference.
- The NuGet package signature (`.signature.p7s`) is removed because rewriting invalidates it. No new package signature
  is added. Every other file is copied unchanged: build files, licenses, content and documentation.
- Mixed-mode (C++/CLI) and ReadyToRun images are never rewritten. If one needs signing, the run fails; supply a
  strong-named build of it instead. Rewriting removes any Authenticode signature, and the report warns about it.
- Untrusted package input is checked. The tool rejects entry paths with `..`, `.`, empty segments, alternate streams or
  invalid characters, and entries that differ only in case. Extraction is capped at 100,000 entries and 4 GiB. The
  `.nuspec`, `.psmdcp` and `.rels` files are read without DTD processing. Source URLs are logged without user info or
  query strings.
- Portable and embedded PDBs are rewritten to match. Windows (native) PDBs are kept unchanged, and the report warns that
  they no longer match.

## Output

stdout receives exactly one JSON report. Diagnostics go to stderr. The exception is `--help` and `--version`, which
print plain text.

| Exit code | Meaning                                                          |
|-----------|------------------------------------------------------------------|
| 0         | Signed, already strong-named, or verified                        |
| 1         | Processing or verification failure, download failure, or timeout |
| 2         | Invalid or missing arguments, unreadable or invalid key file     |

The report contains `status` (`signed`, `already-strong-named`, `verified`, `failed` or `invalid-arguments`), the input
ID, version, path and SHA-256, and the output path and SHA-256. It also contains the signing public key token,
per-assembly outcomes grouped by TFM/RID, rewritten references, dependency changes, warnings and errors. Key material
and credentials are never written.

## Build, test and pack

```sh
dotnet test NuGetStrongName.slnx -c Release
dotnet pack src/NuGetStrongName.Tool/NuGetStrongName.Tool.csproj -c Release -o artifacts
```

The tool's own assemblies, tests included, are strong-named with the repository's build key `Key/NuGetStrongName.snk`.
That key is never used to sign input packages and is not shipped in the release package. Package versions are pinned in
`Directory.Packages.props`. The SDK is pinned in `global.json`.

| Dependency                         | Version | License    | Use                                                        |
|------------------------------------|---------|------------|------------------------------------------------------------|
| Mono.Cecil                         | 0.11.6  | MIT        | Reading, rewriting and strong-name signing assemblies      |
| NuGet.Protocol / NuGet.Credentials | 8.0.0   | Apache-2.0 | NuGet configuration, source mapping, credentials, download |
| System.Security.Cryptography.Pkcs  | 10.0.14 | MIT        | Transitive pin over a vulnerable 8.0.1                     |

Signature verification uses `System.Reflection.Metadata` and `System.Security.Cryptography` (ECMA-335 II.6.2.1.3
hashing). On Windows the tests check it against `sn.exe`.

## Releasing

1. Set `ToolVersion` in `Directory.Build.props`. Run the tests and the QuickBooksApi acceptance run on Windows and
   Linux.
2. Pack: `dotnet pack src/NuGetStrongName.Tool/NuGetStrongName.Tool.csproj -c Release -o artifacts`.
3. Write the manifest: `(cd artifacts && sha256sum NuGetStrongName.Tool.<version>.nupkg > SHA256SUMS)`.
4. Tag `v<version>` and publish a GitHub Release with `NuGetStrongName.Tool.<version>.nupkg` and `SHA256SUMS`. Never
   replace the assets of a published release; publish a new version instead. Do not attach keys, credentials or input
   packages.
