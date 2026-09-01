# Adversarial BuildXL validation of native evaluation observation

## Verdict

The current observation layer is **not complete as an inventory of files and
paths used by evaluation**, and some records are **incorrectly classified as
filesystem dependencies**.

The adversarial campaign at observer commit `3a45f8d883` ran 20 scenario
families and produced 36 BuildXL/native traces. It confirmed:

- successful evaluations with file reads visible to BuildXL but absent from
  native file/path records;
- path-only calculations recorded as file metadata despite no filesystem
  access;
- relative file operations recorded with relative identities and no
  per-record base directory;
- two native identities for the same physical file when one path used the
  Windows `\\?\` prefix;
- failed reads whose exact path was absent from typed file records;
- malformed imported XML that was opened but whose bytes and parse failure
  were not recorded;
- malformed root XML that produced no observation report at all.

Most cache and provider cases fail closed through report reasons. Therefore
this campaign proves that the observation inventory is incomplete and
contains false dependencies; it does **not** prove an unsafe cache hit if
every incomplete reason is enforced correctly.

No tested realistic report was generally reusable as-is:
`UnversionedToolsetInputs` alone remained present in every normal evaluation,
and a real SDK project added further blockers.

## Method

| Property | Value |
| --- | --- |
| Observer commit | `3a45f8d883` |
| Platform | Windows |
| Sandbox broker | .NET Framework 4.7.2, x64, BuildXL Detours |
| BuildXL package | `Microsoft.BuildXL.Processes` `0.1.0-20260612.4` |
| Normal child | .NET Framework 4.7.2, x64 |
| Real SDK child | .NET 11, launched under the same Detours broker |
| Runs | Cold, warm, shared-cache, mutation, and expected-failure variants |
| Trace scope | Scenario roots plus explicitly selected SDK, workload-manifest, and targeting-pack roots |

The temporary harness retained:

- BuildXL operation, requested access, status, error, path, and enumeration
  pattern;
- every typed native observation;
- report reasons and category states;
- the evaluated `RequestedProperty` and `Compile` item count for cache
  mutation tests.

The original scenario-only comparison remains valid for its one project, but
it is not a completeness result. The broader matrix intentionally crossed
the boundaries omitted by that run.

## Scenario matrix

| Scenario | Main result | Classification |
| --- | --- | --- |
| Path-only property functions and item modifiers | Nine native metadata records without corresponding filesystem I/O | Incorrect false dependencies |
| Relative property-function reads, probes, metadata, and enumeration | Native records retained relative paths; BuildXL reported absolute paths | Explicitly incomplete through `UnrootedPath` |
| Windows drive-relative and root-relative paths | Resolved to the same absolute native and BuildXL path | Negative control |
| `\\?\` extended path plus normal path | One physical file became two native `FileRead` identities with the same hash | Incorrect path identity |
| Junction and hardlink | Logical paths were retained; internal junction traversal was BuildXL-only under literal comparison | Alias-policy and comparer limitation |
| Alternate data stream | Path was rejected before the read; failure was global rather than a typed file record | Explicit failure limitation |
| Empty globs, empty enumeration, positive/negative probes | Semantic records were present; internal traversed directories were BuildXL-only under literal comparison | Semantic-comparer requirement |
| Supported file and directory property functions | Reads, metadata, `GetFiles`, and `GetDirectories` were recorded; all relative identities remained incomplete | Partially correct |
| Wildcard imports and upward searches | Selected files and ordered candidates were recorded | Negative control |
| Malformed parser configuration | Raw-byte content hash and malformed parse outcome were recorded | Negative control |
| Custom SDK resolver | Resolver sidecar file was read but absent from native file records | Intentional SDK opacity |
| Custom filesystem provider | Provider sidecar file was read but absent from native file records | Explicitly incomplete custom-provider boundary |
| Shared evaluation context | Cached probe/glob values were replayed, with `UnversionedSharedCache` | Fail closed |
| Process file-existence cache | Measured evaluation had zero native probes, with `UnversionedFileExistenceCache` | Fail closed |
| Process glob cache | Semantic glob result was replayed, with `UnversionedGlobCache` | Fail closed |
| PRE/import mutation | Disk changed, evaluation returned stale data, and native report retained the old consumed hash | Fail closed through shared-cache reason |
| Missing `ReadAllText` input | BuildXL reported the missing path; native typed file records did not | Failure-path incompleteness |
| Malformed import | BuildXL opened the import; native recorded only a positive probe | Incorrect failure observation |
| Malformed root | BuildXL opened the project; no native report existed | Pre-session coverage gap |
| Real `Microsoft.NET.Sdk` project | 26 warm and 34 cold file reads were absent from native path records | Confirmed built-in SDK/toolset gap |

## Confirmed incorrect records

### 1. Path-only calculations are reported as file metadata

The `path-only` scenario performed no filesystem access for:

- `Directory.GetParent`;
- `DirectoryInfo.FullName`;
- `DirectoryInfo.Name`;
- `DirectoryInfo.Parent`;
- item modifiers `FullPath`, `RootDir`, `RelativeDir`, and `Directory`.

The native report nevertheless emitted **nine** `FileMetadata` observations.
It had no matching incompleteness reason beyond the unrelated toolset reason.

This follows directly from the classifier:

- `s_directoryMetadataMembers` includes `GetParent`;
- `s_fileSystemInfoMetadataMembers` includes path-only members such as
  `Directory`, `DirectoryName`, `Extension`, `FullName`, `Name`, `Parent`,
  and `Root`;
- `RecordItemMetadata` maps path-only item modifiers into the file-metadata
  category.

These values affect evaluation, but they depend on path syntax and a base
directory, not on file mutation. Treating them as disk metadata creates
false invalidations and mixes two different dependency domains.

### 2. Windows extended paths produce duplicate identities

The same file was read through:

```text
C:\...\input.txt
\\?\C:\...\input.txt
```

BuildXL canonicalized both accesses to one path. The native observer emitted
two `FileRead` records with identical content hashes and different keys.
There was no alias or incompleteness reason.

The current `NormalizePath` calls `GetFullPathNoThrow`, but it does not define
a canonical identity across Windows namespace aliases. The same issue
applies conceptually to substituted drives, junction targets, and other
equivalent names.

### 3. Malformed imports were incomplete at the baseline

At the pinned validation baseline, for a malformed imported `.props` file:

- BuildXL recorded a successful `CreateFile` read;
- native observation recorded a positive file probe;
- native observation did **not** record the imported bytes, a source record,
  or parse-failure provenance;
- `EvaluationSucceeded` was false;
- the only reason was `UnversionedToolsetInputs`;
- `ProjectSource`, `FileContent`, and `Completion` remained reported as
  observed rather than incomplete.

The path set happened to contain the import because of the probe. A
path-only differential would therefore miss the missing content
observation.

The current observer resolves this model gap. It completes the raw-byte hash
through the original still-open stream, records an import source with
`ParseFailure`, emits a typed `ProjectSource.Parse` failure, and marks the
project-source category incomplete. The same record is retained when
`IgnoreInvalidImports` allows evaluation to continue. Missing imports remain
negative probes and are not reclassified as failed sources. Post-fix BuildXL
differential results are reported only after the complete six-gap validation
pass.

## Confirmed incomplete path observations

### 4. Relative property-function paths lack stable identities

With the process current directory set to the project directory, these
operations succeeded:

- `File.ReadAllText("relative.txt")`;
- `File.Exists("relative.txt")`;
- `File.GetLastWriteTimeUtc("relative.txt")`;
- `Directory.GetFiles("enum", ..., AllDirectories)`.

BuildXL reported absolute paths. Native records retained:

```text
relative.txt
enum
```

The report added `UnrootedPath`, which correctly blocks reuse, but the typed
file read, probe, metadata, and enumeration records do not carry the
effective base directory. `Path.GetFullPath` and `MakeRelative` separately
record their resolved paths; direct file and directory property functions do
not.

This is safe only if `UnrootedPath` always rejects the complete evaluation.
It is not a complete path inventory and makes common relative property
functions ineligible for caching.

### 5. Hidden reads inside custom extension points are absent

#### Custom SDK resolver

A registered resolver read `resolver.config`, then returned an SDK directory.
BuildXL reported the config read. Native observation recorded:

- SDK request;
- SDK result and cache hit/miss;
- SDK props and targets;
- no file read or probe for `resolver.config`.

The report had no resolver-file reason because SDK resolver internals are
opaque. The recorded SDK cache identity describes only the request/result
boundary and entry lifetime; it does not validate resolver dependencies. This
scenario therefore remains outside cache-correctness claims until resolvers
provide dependency manifests or authoritative validity tokens. It is evidence
for the resolver-contract exclusion, not evidence that the supported filesystem
observation boundary is incomplete.

#### Custom filesystem provider

A custom `MSBuildFileSystemBase` read `hidden-provider.config` before
delegating an `Exists("marker.txt")` request. BuildXL reported both paths.
Native observation recorded only `marker.txt`.

This case did fail closed through:

- `UnversionedCustomProvider`;
- `UnversionedSharedCache`.

Again, the cache gate is conservative, but the file inventory is incomplete.

### 6. A real SDK evaluation has many missing file reads

The broad real-SDK run included:

- the project root;
- the installed SDK directory;
- `sdk-manifests`;
- targeting packs.

#### Warm run

| Metric | Count |
| --- | ---: |
| BuildXL raw events | 750 |
| BuildXL unique paths | 349 |
| Unique BuildXL paths opened for read | 143 |
| Read paths with an exact native path | 117 |
| Read paths absent from native path records | **26** |

The 26 missing reads were:

- 18 `WorkloadManifest.json` files;
- 6 localized `WorkloadManifest.en.json` files;
- `KnownWorkloadManifests.txt`;
- `NETCoreSdkRuntimeIdentifierChain.txt`.

BuildXL also reported native-path omissions for:

- a negative `global.json` probe;
- two workload-locator SDK directory probes.

One raw mismatch for the project `obj` directory was only a trailing-slash
normalization difference; the native observer had recorded the probe.

#### Cold run

| Metric | Count |
| --- | ---: |
| BuildXL raw events | 808 |
| BuildXL unique paths | 374 |
| Unique BuildXL paths opened for read | 151 |
| Read paths with an exact native path | 117 |
| Read paths absent from native path records | **34** |

The additional cold-only reads were resolver and dependency assemblies or
metadata loaded from the SDK directory.

The native report was already non-reusable:

- `ConflictingObservation`;
- `UnclassifiedPropertyFunction`;
- `UnversionedToolsetInputs`;
- `UnversionedToolLocationHelperCache`.

The unclassified member was `System.String[]::GetValue`, a pure array access
used by SDK targets. This means a normal SDK project currently cannot produce
a clean observer report even before filesystem invalidation is implemented.

The existing SDK-cache policy can intentionally hide resolver internals
within one live cache entry. It does not make the native file/path inventory
complete, and it does not cover the separate tool-location read.

### 7. Failed reads lost the typed path at the baseline

At the pinned validation baseline, for `File.ReadAllText("missing.txt")`:

- BuildXL reported the failed access to the absolute missing path;
- the native report contained the root project only in `FileReads`;
- the failed property-function record retained `missing.txt` as an argument;
- `ExternalOperationFailure` made `Completion` incomplete;
- `FileContent` itself remained `Observed`.

The current observer resolves this model gap by emitting a typed failure with the
affected category, operation, canonical path and provider when applicable,
exception type, HRESULT, and diagnostic message. The affected category and
`Completion` become incomplete, and `ExternalOperationFailure` remains the global
reuse blocker. Parser-configuration load failures and registry-expression failures
also retain typed attribution. Post-fix BuildXL differential results are reported
only after the complete six-gap validation pass.

### 8. Early root failures had no report at the baseline

At the pinned validation baseline, a malformed root project generated two BuildXL events for the root path and
zero native reports. The observation session is created in `Evaluator`,
after the root `ProjectRootElement` has already been loaded.

This is outside successful evaluation-cache reuse, but it disproves any
claim that failed evaluation always has typed source and failure
observations.

The current observer starts a source-load capture before root acquisition
for file-based `Project` and `ProjectInstance` entry points. If parsing
fails, it emits one minimal failed report with the root role, exact
raw-byte hash, parse outcome, timestamp/provider data, and typed
`ProjectSource.Parse` failure. The request category is incomplete because
normal evaluator initialization was never reached.

## Cache experiments

### PRE/import mutation

The test:

1. evaluated a project importing `shared.props`;
2. replaced `shared.props` with new content;
3. evaluated a second project in the same `ProjectCollection` and shared
   `EvaluationContext`.

Disk contained:

```xml
<ImportedProperty>ImportedValue</ImportedProperty>
```

The second evaluation returned:

```text
RequestedProperty=Initial
```

Native observation recorded the **old consumed hash**, not the new disk hash,
and added `UnversionedSharedCache`.

This is correct observation of a stale engine cache result. It also proves
that a validator must reject shared-cache reports until that cache has
versioned provenance; simply hashing the value consumed during evaluation is
not enough to establish disk freshness.

### Process caches

| Cache | Measured behavior | Reason |
| --- | --- | --- |
| File-existence cache | Second evaluation performed zero native path probes | `UnversionedFileExistenceCache` |
| Glob cache | Semantic glob result was replayed with almost no measured traversal | `UnversionedGlobCache` |
| Shared evaluation context | Probe and glob observations were replayed | `UnversionedSharedCache` |

These cases are incomplete but correctly fail closed.

## Alias and oracle findings

- Junction and hardlink reads were recorded under the logical path used by
  the project. BuildXL reported the same logical paths with both reparse
  settings tested.
- BuildXL reported the internal junction glob directory while native
  observation recorded the owning glob. This is a semantic mapping issue,
  not a missing dependency.
- The alternate-data-stream path was rejected before file I/O. Native
  observation recorded a failed property function and a global failure
  reason, but no typed file path.
- BuildXL did not always emit isolated successful directory probes in the
  earlier validation. It cannot be the sole oracle for probe completeness.

## Negative controls

The following cases did not expose a new observer defect:

- malformed `Directory.Parse.config` bytes and parse outcome were recorded;
- upward searches retained ordered candidates and selected files;
- Windows drive-relative and root-relative paths resolved to the same
  absolute native and BuildXL path in the tested process directory;
- supported `ReadAllBytes`, `GetFiles`, and `GetDirectories` property
  functions recorded their results;
- a missing `UsingTask` assembly was not touched during evaluation, so the
  native registration path was semantic output rather than a missed read;
- internal directories and nonmatching entries reported by enumeration were
  owned by native glob or enumeration requests.

`ReadAllLines`, `GetFileSystemEntries`, and `EnumerateFiles` were not available
as property functions in the tested net472 allowlist, so they could not be
used as supported-path counterexamples.

## What has been proved

The broad claim:

> Every file or path used by evaluation is represented accurately by the
> native observation layer.

is false.

The narrower claim:

> For ordinary supported disk evaluation, every unresolved limitation makes
> the report non-reusable.

was not falsified by this campaign. The observer frequently failed closed,
and the real SDK project was already blocked by several reasons. That is a
correctness safeguard, but it also means the current prototype is not ready
to provide useful cache coverage for normal SDK projects.

## Required fixes

1. Split path computation from disk metadata:
   - path-only item modifiers and `FileSystemInfo` members must not become
     file-mutation dependencies;
   - retain their base-directory/ambient inputs in a path-resolution domain.
2. Give every file, probe, metadata, and enumeration record a stable absolute
   identity or an explicit effective base directory.
3. Define Windows path canonicalization for `\\?\`, drive aliases, junctions,
   hardlinks, casing, and trailing separators; otherwise mark aliased paths
   incomplete.
4. Record failed operations with path, operation kind, provider, and exact
   outcome instead of only a global failure bit.
5. Hash and stamp successful sources during acquisition, finish hashing malformed
   imports through the original stream, and retain hash/outcome/failure metadata for
   root loads that fail before `Evaluator` exists.
6. Correct category states when evaluation fails before a source/content
   dependency was captured.
7. Either:
   - instrument built-in SDK/workload/tool-location file dependencies, or
   - require resolvers to provide a complete dependency manifest or authoritative
     validity token and keep SDK/tool-location paths without such provenance blocked.

   SDK cache identity is cache-entry lifetime evidence only.
8. Resolve `ConflictingObservation` and classify pure SDK target operations
   such as `String[]::GetValue`.
9. Land a repeatable semantic BuildXL comparer. Raw path intersection is
   insufficient, but hand-mapped one-off reports are not a regression test.
