# NuGetStrongName — Implementation Plan

Status: done
Slice tag: `strongname`
Delivery: Each slice requires acceptance checks, independent review, and an explicitly authorized commit containing
implementation and both plan translations. Verified work awaiting commit stays `in progress`. This edit moves planning
only and does not authorize implementation, commits, or publication.

## Goal and How to Continue

Build a generic, independent cross-platform NuGet strong-name CLI and publish pinned GitHub Releases that other tools
can consume. Use the shared `continue-plan` skill, resuming one slice or its closeout at a time. Use `pending`,
`in progress`, and `done`; record actual commands, results, blockers, and next actions. The English plan is
authoritative; maintain its Chinese translation in the same change.

## Slices

| # | Outcome                                                            | Status      | Depends on |
|---|--------------------------------------------------------------------|-------------|------------|
| 1 | Generic cross-platform .NET CLI, signing, and package verification | done        | None       |

### Slice 1 — Implement a Generic Cross-Platform .NET CLI

Scope and distribution:

- Implement a testable cross-platform .NET console tool in this repository, without references to consumer projects or
  WPF. Own download, inspection, reference rewriting, signing, repackaging, and reporting directly.
- Use a license-reviewed, pinned metadata library (validate Mono.Cecil first), not a third-party signing CLI or
  handwritten PE/CLI parsing/cryptography. Verify modern IL, reference assemblies, and real signing/verification on both
  platforms first.
- Build a versioned .NET tool `.nupkg` with `PackAsTool`, command `nuget-strongname`, and package ID
  `NuGetStrongName.Tool`. Initially target `net11.0`; document the .NET 11 runtime and SDK needed for tool installation.
  Cross-platform does not mean runtime-free. Do not add multi-RID self-contained releases or a second distribution
  model.
- Provide `sign-package`, `verify-package`, `--help`, and `--version`. Signing inputs are mutually exclusive
  `--package-id` plus `--version`, or local `--input`, with explicit `--key-file`, `--output`, and dependency mappings.
  Subcommand version selects the upstream package; top-level version reports the tool version. Inspect for
  already-signed status before requiring key/output; missing signing parameters cause clear noninteractive failure,
  leaving user questions to the caller.
- Resolve effective NuGet configuration, source mapping, and authentication in the consumer repository context; accept
  explicit repository/configuration locations without requiring a particular layout. Key and output locations are caller
  inputs, not built-in consumer-specific defaults.
- Support Windows and Linux without WPF, WinForms, registry access, Windows-only sn.exe/ildasm.exe/ilasm.exe, or
  PowerShell. Use platform path APIs and cover spaces/case sensitivity; report missing prerequisites clearly.
- Maintain CLI source, NUnit tests, and release documentation in this repository. Consumers use pinned releases without
  cross-checkout source references.
- Strong-name sign the tool itself using its own `Key/NuGetStrongName.snk`, not a consumer's key. This 2048-bit key has
  been generated, import-validated, and committed. Add SignAssembly and an AssemblyOriginatorKeyFile resolved relative
  to the tool repository root when projects are created; use shared signing configuration for all owned assemblies
  including tests. Verify released assemblies/tokens and stable identity across rebuilds. This build key must never
  become the default input-package signing key or be included in a release.
- Maintain and release the tool on GitHub: pin versions, document reproducible build/pack commands, and prepare a GitHub
  Release containing the .NET tool `.nupkg`, SHA-256 manifest, and usage documentation. Use tag `v<version>` and a
  stable asset name `NuGetStrongName.Tool.<version>.nupkg` so callers can download a pinned version by URL and verify
  its SHA-256; never replace assets of a published release. Commit, push, tag, and publish only under user authorization
  after local and dual-platform acceptance. NuGet.org publication is outside current scope. Exclude private keys,
  credentials, and input packages from release artifacts.

Required behavior:

- Inspect relevant managed compile/runtime assets and reference identities. If already strong-named with no required
  rewrite, return exit 0 and JSON `status: already-strong-named`, original ID/version, and `outputPath: null`, without
  loading a private key, requiring signing tools, or creating output. A NuGet package signature or token alone is
  insufficient; account for reference-assembly conventions.
- Treat mutually referencing DLLs within each compatible TFM/RID asset group as a set: build an exact identity map
  before rewriting every affected AssemblyRef and signing with the matching key. Do not merely sign files independently,
  guess by name prefix, or mix same-named DLLs across TFMs. Support explicit cross-package mappings and
  InternalsVisibleTo where needed, without recursively re-signing all dependencies.
- Sign every managed assembly in the package: process each TFM/RID under `lib/`, `ref/`, and `runtimes/*/lib/`, not only
  the current platform or newest TFM. Use one key across all TFMs so same-named assemblies share the public key token,
  and keep assembly versions unchanged. Judge `already-strong-named` across all TFMs: if any TFM is unsigned, process
  the unsigned assets and preserve valid existing signatures. Report per-assembly results grouped by TFM/RID.
- Require a real QuickBooksApi (`IppDotNetSdkForQuickBooksApiV3`) regression: process its multiple unsigned, mutually
  referencing DLLs together, including transitive references and cycles if present. Signed DLLs retaining stale
  null-token references are a failure.
- Preserve valid vendor identities; fail explicitly if rewriting an already signed assembly requires an unavailable
  matching key. Preserve all TFM/RID, ref/lib, resources, native assets, build files, and licenses; do not sign native
  DLLs.
- Follow caller-specified output naming, for example the same ID with `X.Y.Z-signed`; do not impose one consumer
  convention universally. Update mapped package dependencies and assembly references; remove invalidated original NuGet
  signatures and do not add NuGet package signing.
- Use unique temporary workspaces, immutable inputs, no output overwrites, and final output only after verification. CLI
  stdout is JSON, stderr diagnostics; exits 0 success, 1 processing/verification failure, 2 invalid arguments. Propagate
  backend failures/timeouts, handle spaced paths, report versions/hashes/per-assembly outcomes, and never expose keys or
  credentials.

Acceptance and verification:

- Use TDD for new CLI behavior. Cover already-signed no-op, mixed/invalid signatures, mutual references,
  multi-TFM/ref/RID assets, missing dependencies, spaced paths, tool failure, timeout, collisions, and input
  preservation.
- Download a real QuickBooksApi upstream package, rewrite its assembly set, sign, and repack; inspect every changed
  reference token against the actual target DLL and verify required signatures. Restore from an isolated local feed,
  compile, and run representative consumer behavior without remote QuickBooks calls.
- Use a multi-TFM package (QuickBooksApi if it ships several TFMs) to verify that every DLL in every TFM/RID is signed
  and that same-named assemblies share one public key token.
- Run the same deterministic suite and real QuickBooksApi download/sign/pack/consumer verification on Windows and Linux.
  Compare package layout and dependency identities, not byte-identical ZIP hashes. Unrun or failing Linux acceptance
  remains an explicit blocker; Windows success is not a substitute. Preserve Windows-only package assets without
  claiming they can execute on Linux.
- Exercise asset types required by other custom packages and obtain independent review. Record the CLI library/license,
  pinned versions, exact commands/results, and verification limits. A CLI completing alone is not artifact acceptance;
  consumers own legacy-tool retirement.

Decisions:

- Libraries: Mono.Cecil 0.11.6 (MIT) reads, rewrites and signs. The feasibility check (2026-11-21) confirmed that
  `WriterParameters.StrongNameKeyBlob` signs on .NET 11 without `StrongNameKeyPair`. NuGet.Protocol and
  NuGet.Credentials 8.0.0 (Apache-2.0) handle configuration, source mapping, credentials and download.
  System.Security.Cryptography.Pkcs 10.0.14 is pinned transitively because NuGet.Packaging 8.0.0 brings a vulnerable
  8.0.1. Tests use NUnit 5.0.0, NUnit3TestAdapter 6.3.1 and Microsoft.NET.Test.Sdk 18.12.0. Every pin was published at
  least two weeks earlier. The SDK is pinned by `global.json` (11.0.100, `latestFeature`).
- Signature verification: Cecil cannot verify, and no cross-platform verifier is published on nuget.org.
  `StrongNameIdentity` computes the ECMA-335 II.6.2.1.3 signed-region hash over System.Reflection.Metadata PE headers
  and checks it with BCL RSA. `StrongNameKey` parses the CAPI `.snk` blob. This is a deliberate, narrow exception: no
  hand-written crypto primitive and no metadata parser. A Windows test cross-checks the verifier against `sn.exe -vf`.
- CLI contract: dependency mappings are repeatable `--map-package <id>=<newId>@<version>` and
  `--map-assembly <name>[=<token>]` options, not a mapping file. A mapping without a token means the `--key-file` token.
  `--output` is a directory and the file is `<output-id>.<output-version>.nupkg`. `--output-version` is required when
  signing, so the upstream identity is never shadowed. `--output-id` defaults to the input ID. Also available:
  `--repository`, `--configfile` and `--timeout` (default 600 s). `--help` and `--version` print plain text. Every
  command result is one JSON document. A downloaded input reports `inputPath: null` and `inputSource`.
- Strong-name rules: an implementation asset needs a valid signature, or the ECMA platform key. An asset in `ref/` or
  marked `[ReferenceAssembly]` only needs a public key (public-signing convention). Only null-token references are
  rewritten. Lookup order is the same group, then the `lib/` group of the same TFM, then package-wide only when every
  candidate ends with one token. Satellite assemblies are signed but kept out of the identity map. A valid signature is
  preserved byte-for-byte. A rewrite or re-sign is only allowed with the assembly's own key.
- Symbols: portable and embedded PDBs are rewritten. Native Windows PDBs (QuickBooksApi ships them) are kept unchanged
  and reported as a warning, because Cecil cannot write them cross-platform.

Verification (Windows 11 and Ubuntu 22.04, .NET SDK 11.0.100, 2026-11-21):

- Independent review (code-reviewer agent, static read): 0 critical, 2 high, 7 medium, 9 low findings. All fixed with
  regression tests (`ReviewRegressionTests`) except three:
    - Fixed H1: a catch-all keeps the JSON and exit-1 contract, a malformed nuget.config or manifest now fails cleanly,
      and `Single`/`First` crashes are gone.
    - Fixed H2: a mixed-mode or ReadyToRun image that needs signing fails instead of being rewritten.
    - Fixed M1: a reference from a preserved assembly to an unsigned assembly outside the package is now a warning in
      both sign and verify.
    - Fixed M2: unsafe entry names (`..`, `.`, empty segment, `:`, invalid characters) are rejected.
    - Fixed M3: IDs and versions, including `--map-package` ranges, are validated at parse time and give exit 2.
    - Fixed M4: a truncated PE is reported as `Invalid`, not a crash.
    - Fixed M5: a corrupt managed image fails instead of being treated as native.
    - Fixed M6: extraction is capped at 100,000 entries and 4 GiB, and XML is read without DTD processing.
    - Fixed M7 and L8: Authenticode and `.signature.p7s` removal are reported as warnings.
    - Fixed L3: logged source URLs are redacted.
    - Fixed L4: the workspace is created with mode 0700 on Unix.
    - Fixed L5: entries that differ only in case are rejected.
    - Fixed L6: only `_rels/.rels` and core-properties `.psmdcp` are rewritten, and a `.rels` file that cannot be
      renamed fails.
    - Fixed L9: extraction checks the timeout.
    - Deferred to Carry-over: L1 (precedence of the hash algorithm for enhanced strong-name keys), L2 (detect mixed
      vendor and unsigned TFMs before signing rather than at the final verification) and L7 (IVT in preserved
      assemblies). The M7 Authenticode warning has no fixture test.
    - Mutation checks: removing the ILOnly guard fails 1 test, and removing entry-name validation fails 4.
- `dotnet test NuGetStrongName.slnx` after the fixes: 53 passed, 0 failed. Before the fixes: 34 passed, 0 failed. The
  suite covers already-signed no-op, mixed, invalid and foreign-key signatures, mutual references with a cycle, IVT and
  `typeof` rewrites, multi-TFM/ref/RID/satellite/native assets, missing and mapped dependencies, spaced paths, corrupt
  input, an unresponsive source timeout, missing packages, source mapping, output collisions, input immutability,
  output-ID renames and portable PDBs. The `sn.exe` oracle test ran (was not skipped). The tests were written alongside
  the implementation, not strictly red-first. To compensate, mutation checks were run: removing reference rewriting
  fails 5 tests, and removing IVT rewriting fails 1.
- `dotnet pack src/NuGetStrongName.Tool/NuGetStrongName.Tool.csproj -c Release -o artifacts` produced
  `NuGetStrongName.Tool.0.1.0.nupkg` (contains no `.snk`). It was installed with
  `dotnet tool install --tool-path <tmp> NuGetStrongName.Tool --version 0.1.0 --add-source artifacts`. `--version`
  prints `0.1.0`.
- Tool identity: the tool and test assemblies pass `sn -vf` with token `43203feb910a45dc` (`Key/NuGetStrongName.snk`). A
  `--no-incremental` Release rebuild was byte-identical.
- QuickBooksApi run:
  `nuget-strongname sign-package --package-id IppDotNetSdkForQuickBooksApiV3 --version 14.7.1.6 --key-file "<tmp>/keys/consumer test.snk" --output "<tmp>/local feed" --output-version 14.7.1.6-signed`
  exited 0 in about 4.5 s. It signed 48 assemblies (16 × net461/net472/netstandard2.0) with a throw-away `sn -k 2048`
  test key (token `887f7ba4a4ba02b9`) and rewrote every `Intuit.*` reference. It warned about the kept native PDBs.
  `verify-package --public-key-token 887f7ba4a4ba02b9` exited 0. `sn -vf` passed for 48/48 DLLs. The entry list matches
  upstream apart from the removed `.signature.p7s`. The upstream release ships no cycle, `ref/` or `runtimes/` assets;
  fixtures cover those.
- Consumer: a strong-named `net48;net11.0` project (`TreatWarningsAsErrors`, so CS8002 fails the build) restored
  `14.7.1.6-signed` from an isolated local feed through source mapping and an isolated `globalPackagesFolder`. It built
  and ran `ServiceContext`, `DataService`, `QueryService<Customer>` and `Customer` without remote calls. On both TFMs,
  all loaded `Intuit.Ipp.*` assemblies carried `887f7ba4a4ba02b9` and no reference had a null token. Two environment
  issues came up and are not tool defects. The upstream SDK's NU1902/NU1904 audit warnings were exempted in the consumer
  only. The run was moved to a shorter temp path to stay under Windows MAX_PATH.
- Re-run after the review fixes, using the Release build from `<tmp>`: the result was the same (exit 0, 48 signed).
  `verify-package` reported `verified`, `sn -vf` passed 48/48, and the consumer printed `CONSUMER OK` on net11.0 and
  net48. The `.signature.p7s` removal warning is now reported. Running the tool inside the consumer repository correctly
  failed with "not found": its source mapping limits the package to the local feed.
- Linux acceptance: this workstation cannot run Docker, so the run used a remote Ubuntu 22.04 x64 host over SSH
  (user-provided), with SDK 11.0.100 installed privately via `dotnet-install.sh`. A bundle of the working tree and an
  acceptance script (kept in the git-ignored `artifacts/linux-acceptance/`) ran the steps below in a spaced work path
  and compared the result with the Windows output:
    - `dotnet test NuGetStrongName.slnx -c Release`.
    - Pack, then install the tool from a fresh package folder.
    - Download QuickBooksApi 14.7.1.6 from nuget.org and sign it with the same test key.
    - `verify-package --public-key-token 887f7ba4a4ba02b9`.
    - Run the net11.0 consumer (net48 cannot run on Linux).
    - Compare with the Windows output: status, input SHA-256, output ID/version, token, per-assembly asset groups,
      dependency changes, entry list and per-entry SHA-256.
- First Linux run: everything passed, but the `.nuspec` and `.psmdcp` differed from Windows only in line endings. The
  upstream files use CRLF; `XDocument.Save` wrote the platform newline, so Linux emitted LF. Fixed: `ManifestEditor` now
  saves through an `XmlWriter` with the file's original newline. The regression test
  `Edited_manifests_keep_their_line_endings_on_every_platform` (CRLF and LF cases) fails against the old save call. This
  fix came after the independent review and has not been reviewed independently.
- After the fix: Windows `dotnet test` gives 55 passed, 0 failed. A Windows re-run of the QuickBooksApi sign and verify
  (exit 0) produced entries byte-identical to the earlier Windows output, so the earlier `sn -vf` and consumer results
  still hold. The Linux re-run passed:
    - Tests: 54 passed, 0 failed, 1 skipped (the Windows-only `sn.exe` oracle).
    - The package was `signed` with 48 assemblies and the native-PDB and `.signature.p7s` warnings, and `verify-package`
      reported `verified`.
    - The consumer printed `CONSUMER OK` with all 8 loaded `Intuit.Ipp.*` assemblies on `887f7ba4a4ba02b9`.
    - The comparison matched every field and all 149 entries byte-for-byte (0 differ), including the dependency
      identities.
- Unverified: Asset types of other custom packages (none named yet). Credential-provider plugins against an
  authenticated feed. Release publication.

Resume: None; Slice 1 is done (commit `1483426`). Remaining work is in Carry-over.

Carry-over:

- GitHub Release `v0.1.0` (`NuGetStrongName.Tool.0.1.0.nupkg` + `SHA256SUMS`) only after dual-platform acceptance and
  explicit authorization. The README install URL keeps an `<owner>` placeholder until the repository remote exists.
- Consumers should name any other custom packages whose asset types need coverage.
- Low-severity review items deferred:
    - L1: verify enhanced (SHA-256 signature-key) strong names against the CLR's hash-algorithm precedence.
    - L2: let the planner detect, before signing, a package whose same-named assemblies are vendor-signed in some TFMs
      and unsigned in others. Today this fails at the final verification.
    - L7: decide whether IVT friends in preserved assemblies need handling.
    - Add an Authenticode fixture for the removal warning.
- Get an independent review of the line-ending fix in `ManifestEditor.SaveXml`, and re-run the Linux acceptance on the
  committed code (see the 2026-11-26 record).

## Verification and Handoff Record

Migrated CLI development requirements from a consumer repository's plan; consumer legacy-tool retirement remains there.
`Key/NuGetStrongName.snk` has been generated, import-validated, and committed with this plan. Preserve the existing key
rather than regenerating it.

2026-11-21: Slice 1 implemented and accepted on Windows and Linux (see Slice 1 Verification).

2026-11-26: Slice 1 committed as `1483426` with explicit user authorization. Between the Linux acceptance run and the
commit, the code got behavior-preserving cleanups: `m_` private-field naming, a `JIT.` root namespace, IDE
reformatting, string interpolation, a closure-free test listener, a new `StringExtensions.cs` helper, and removal of the
unused `AssemblyReferenceInfo.Version`/`Culture`. Windows `dotnet test` passed 55/55 on the committed code; Linux was
not re-run. Slice 1 is done; next work is in Carry-over.
