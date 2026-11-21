# NuGetStrongName — Implementation Plan

Status: pending
Slice tag: `strongname`
Delivery: Each slice requires acceptance checks, independent review, and an explicitly authorized commit containing implementation and both plan translations. Verified work awaiting commit stays `in progress`. This edit moves planning only and does not authorize implementation, commits, or publication.

## Goal and How to Continue

Build a generic, independent cross-platform NuGet strong-name CLI and publish pinned GitHub Releases that other tools can consume. Use the shared `continue-plan` skill, resuming one slice or its closeout at a time. Use `pending`, `in progress`, and `done`; record actual commands, results, blockers, and next actions. The English plan is authoritative; maintain its Chinese translation in the same change.

## Slices

| # | Outcome | Status | Depends on |
|---|---------|--------|------------|
| 1 | Generic cross-platform .NET CLI, signing, and package verification | pending | None |

### Slice 1 — Implement a Generic Cross-Platform .NET CLI

Scope and distribution:
- Implement a testable cross-platform .NET console tool in this repository, without references to consumer projects or WPF. Own download, inspection, reference rewriting, signing, repackaging, and reporting directly.
- Use a license-reviewed, pinned metadata library (validate Mono.Cecil first), not a third-party signing CLI or handwritten PE/CLI parsing/cryptography. Verify modern IL, reference assemblies, and real signing/verification on both platforms first.
- Build a versioned .NET tool `.nupkg` with `PackAsTool`, command `nuget-strongname`, and package ID `NuGetStrongName.Tool`. Initially target `net11.0`; document the .NET 11 runtime and SDK needed for tool installation. Cross-platform does not mean runtime-free. Do not add multi-RID self-contained releases or a second distribution model.
- Provide `sign-package`, `verify-package`, `--help`, and `--version`. Signing inputs are mutually exclusive `--package-id` plus `--version`, or local `--input`, with explicit `--key-file`, `--output`, and dependency mappings. Subcommand version selects the upstream package; top-level version reports the tool version. Inspect for already-signed status before requiring key/output; missing signing parameters cause clear noninteractive failure, leaving user questions to the caller.
- Resolve effective NuGet configuration, source mapping, and authentication in the consumer repository context; accept explicit repository/configuration locations without requiring a particular layout. Key and output locations are caller inputs, not built-in consumer-specific defaults.
- Support Windows and Linux without WPF, WinForms, registry access, Windows-only sn.exe/ildasm.exe/ilasm.exe, or PowerShell. Use platform path APIs and cover spaces/case sensitivity; report missing prerequisites clearly.
- Maintain CLI source, NUnit tests, and release documentation in this repository. Consumers use pinned releases without cross-checkout source references.
- Strong-name sign the tool itself using its own `Key/NuGetStrongName.snk`, not a consumer's key. This 2048-bit key has been generated, import-validated, and committed. Add SignAssembly and an AssemblyOriginatorKeyFile resolved relative to the tool repository root when projects are created; use shared signing configuration for all owned assemblies including tests. Verify released assemblies/tokens and stable identity across rebuilds. This build key must never become the default input-package signing key or be included in a release.
- Maintain and release the tool on GitHub: pin versions, document reproducible build/pack commands, and prepare a GitHub Release containing the .NET tool `.nupkg`, SHA-256 manifest, and usage documentation. Use tag `v<version>` and a stable asset name `NuGetStrongName.Tool.<version>.nupkg` so callers can download a pinned version by URL and verify its SHA-256; never replace assets of a published release. Commit, push, tag, and publish only under user authorization after local and dual-platform acceptance. NuGet.org publication is outside current scope. Exclude private keys, credentials, and input packages from release artifacts.

Required behavior:
- Inspect relevant managed compile/runtime assets and reference identities. If already strong-named with no required rewrite, return exit 0 and JSON `status: already-strong-named`, original ID/version, and `outputPath: null`, without loading a private key, requiring signing tools, or creating output. A NuGet package signature or token alone is insufficient; account for reference-assembly conventions.
- Treat mutually referencing DLLs within each compatible TFM/RID asset group as a set: build an exact identity map before rewriting every affected AssemblyRef and signing with the matching key. Do not merely sign files independently, guess by name prefix, or mix same-named DLLs across TFMs. Support explicit cross-package mappings and InternalsVisibleTo where needed, without recursively re-signing all dependencies.
- Sign every managed assembly in the package: process each TFM/RID under `lib/`, `ref/`, and `runtimes/*/lib/`, not only the current platform or newest TFM. Use one key across all TFMs so same-named assemblies share the public key token, and keep assembly versions unchanged. Judge `already-strong-named` across all TFMs: if any TFM is unsigned, process the unsigned assets and preserve valid existing signatures. Report per-assembly results grouped by TFM/RID.
- Require a real QuickBooksApi (`IppDotNetSdkForQuickBooksApiV3`) regression: process its multiple unsigned, mutually referencing DLLs together, including transitive references and cycles if present. Signed DLLs retaining stale null-token references are a failure.
- Preserve valid vendor identities; fail explicitly if rewriting an already signed assembly requires an unavailable matching key. Preserve all TFM/RID, ref/lib, resources, native assets, build files, and licenses; do not sign native DLLs.
- Follow caller-specified output naming, for example the same ID with `X.Y.Z-signed`; do not impose one consumer convention universally. Update mapped package dependencies and assembly references; remove invalidated original NuGet signatures and do not add NuGet package signing.
- Use unique temporary workspaces, immutable inputs, no output overwrites, and final output only after verification. CLI stdout is JSON, stderr diagnostics; exits 0 success, 1 processing/verification failure, 2 invalid arguments. Propagate backend failures/timeouts, handle spaced paths, report versions/hashes/per-assembly outcomes, and never expose keys or credentials.

Acceptance and verification:
- Use TDD for new CLI behavior. Cover already-signed no-op, mixed/invalid signatures, mutual references, multi-TFM/ref/RID assets, missing dependencies, spaced paths, tool failure, timeout, collisions, and input preservation.
- Download a real QuickBooksApi upstream package, rewrite its assembly set, sign, and repack; inspect every changed reference token against the actual target DLL and verify required signatures. Restore from an isolated local feed, compile, and run representative consumer behavior without remote QuickBooks calls.
- Use a multi-TFM package (QuickBooksApi if it ships several TFMs) to verify that every DLL in every TFM/RID is signed and that same-named assemblies share one public key token.
- Run the same deterministic suite and real QuickBooksApi download/sign/pack/consumer verification on Windows and Linux. Compare package layout and dependency identities, not byte-identical ZIP hashes. Unrun or failing Linux acceptance remains an explicit blocker; Windows success is not a substitute. Preserve Windows-only package assets without claiming they can execute on Linux.
- Exercise asset types required by other custom packages and obtain independent review. Record the CLI library/license, pinned versions, exact commands/results, and verification limits. A CLI completing alone is not artifact acceptance; consumers own legacy-tool retirement.

Resume: Validate metadata-library feasibility and add a failing multi-DLL reference-rewrite test for the CLI; no tool implementation, signing performed.

## Verification and Handoff Record

Migrated CLI development requirements from a consumer repository's plan; consumer legacy-tool retirement remains there. `Key/NuGetStrongName.snk` has been generated, import-validated, and committed with this plan. No project exists; no builds, tests, package signing, or releases have run. Next action: begin Slice 1 when implementation is requested. Preserve the existing key rather than regenerating it.
