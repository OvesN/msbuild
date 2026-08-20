# Native evaluation observation inventory

Status: proposed implementation inventory

Related:

- [Evaluation observation layer design](evaluation-observation-layer-design.md)
- [Evaluation observation layer technical reference](evaluation-observation-layer-design-details.md)

## Scope

This document defines the closed set of external inputs, provider results, semantic
decisions, and evaluation-time side effects that the native MSBuild observation layer
must capture while one project evaluation runs.

It does not define:

- cache lookup or cache-hit materialization;
- dependency validation or invalidation;
- watchers, journals, or a reverse dependency index;
- persistence.

The output is an immutable manifest of what evaluation consumed. Evaluation behavior
must remain unchanged when observation is enabled.

## Observation completeness and future reuse

Observation completeness and future cache reuse are separate properties:

- **Observed** means the exact value, outcome, or semantic result consumed by evaluation
  was captured.
- **Incomplete** means the observer did not capture that consumed value or outcome
  exactly, or dropped part of it.
- **Unsupported** means the operation executed unchanged, but it is outside the supported
  observation boundary.
- **NoKnownReuseBlocker** means the manifest contains no known reason that would prevent a
  future validator from using it.
- **NonCacheable** means the consumed result may be observed exactly, but stable
  provenance, a dependency contract, or replayable side effects are unavailable.

The final future-reuse disposition is composed from those states:

| Observation outcome | Future-reuse disposition |
| --- | --- |
| `Observed` with stable provenance and no side effect | `NoKnownReuseBlocker` |
| `Observed` with missing provenance or an unreplayable side effect | `Blocked(NonCacheableReason)` |
| `Incomplete`, dropped, truncated, or conflicting observation | `Blocked(IncompleteObservationReason)` |
| `Unsupported` operation | `Blocked(UnsupportedReason)` |

A provider generation is therefore not required to say what evaluation consumed. It is
required only when that provider's result cannot otherwise be validated safely by a
future cache.

No unsupported or incomplete case should produce a user-facing warning. The report
records a typed internal reason and evaluation continues normally.

## Supported boundary

The intended supported set is:

- normal in-process project evaluation;
- disk-backed sources and MSBuild-owned in-memory sources with content identity;
- standard filesystem operations routed through observable providers;
- members in the closed property-function classification;
- SDK resolution treated as an opaque cache-owned request/result operation;
- standard shared caches after their consumed semantic result and required provenance can
  be replayed.

The following always block future reuse:

- `EnableAllPropertyFunctions` or its legacy environment escape hatch;
- an invoked property-function member that is not in the closed classification;
- an opaque external operation whose consumed result was not captured;
- an evaluation-time side effect that cannot be replayed safely.

MSBuild Server and worker-node evaluation reuse the SDK result returned by their existing
SDK caches. The observation layer does not validate resolver-internal dependencies.

A custom filesystem, directory cache, or source provider can be observed when its exact
returned value reaches the native boundary. It remains non-cacheable unless the provider
also supplies stable identity or version information sufficient for future validation.

## Record model

The completed manifest contains typed collections:

```text
EvaluationRequestObservation
ProjectSourceObservation
FileContentObservation
PathProbeObservation
FileMetadataObservation
DirectoryEnumerationObservation
GlobObservation
SearchObservation
ImportedEnvironmentObservation
LiveEnvironmentObservation
RegistryValueObservation
RegistryEnumerationObservation
AmbientObservation
SdkResolutionObservation
ToolsetObservation
TaskRegistrationObservation
PropertyFunctionUsageObservation
EvaluationSideEffectObservation
ObservationIssue
```

Provider identity belongs on the record that consumed the provider result; there is no
separate catch-all provider dependency.

### Single-owner rule

Each result-affecting fact has exactly one primary semantic owner. Lower-level records may
be retained as supporting evidence, but they are linked to the primary record and are not
independently interpreted as additional dependencies.

| Semantic fact | Primary owner | Supporting evidence |
| --- | --- | --- |
| Root or imported XML consumed | `ProjectSourceObservation` | File-content read, probe, PRE cache entry |
| Glob result | `GlobObservation` | Enumerations, probes, `FileMatcher` cache result |
| Upward/import/fallback selection | `SearchObservation` | Ordered path probes; selected `ProjectSourceObservation` |
| Selected toolset | `ToolsetObservation` | Registry/config reads and environment inputs |
| SDK resolution | `SdkResolutionObservation` | SDK request record and generated SDK-result source |
| Effective `UsingTask` registration | `TaskRegistrationObservation` | Project source, property/item expansion, assembly-path probes |
| VS project-cache plugin registration | `EvaluationSideEffectObservation` | Evaluated item, normalized path, descriptor metadata |
| Imported environment property read | `ImportedEnvironmentObservation` | Evaluation property lookup and imported-environment snapshot |
| Property-function external effect | The matching filesystem, environment, Registry, ambient, or side-effect record | `PropertyFunctionUsageObservation` identifies the dispatched member |

Different outcomes for the same primary dependency identity during one evaluation produce
`ConflictingObservation`. Observing the same fact at a semantic and supporting layer does
not.

## Inventory

### 1. Evaluation request and process semantic snapshot

These values are already known when evaluation starts. Record one effective snapshot
before root source acquisition rather than intercepting repeated reads.

| Input | Kind | Observation seam | Record |
| --- | --- | --- | --- |
| MSBuild engine and host identity | Process ambient | engine entry point, assembly/build identity | Engine version, runtime flavor, host kind |
| Project identity | Request | `Project`, `ProjectInstance`, `BuildRequestConfiguration` entry points | Logical project path/source ID and source-provider identity |
| Complete global properties | Request | `ProjectOptions.GlobalProperties`, `BuildRequestConfiguration.GlobalProperties` | Name/value pairs with MSBuild name-comparison semantics |
| ToolsVersion and subtoolset | Request/effective | `ProjectOptions`, `ToolsetProvider`, `ProjectCollection` | Requested/effective values and explicitness |
| Toolset definition locations | Request | `ToolsetDefinitionLocations` | Exact flags controlling Registry/config discovery |
| Project load settings | Request | `ProjectOptions.LoadSettings`, `BuildParameters.ProjectLoadSettings` | Exact `ProjectLoadSettings` flags |
| Source load policy | Request/effective | PRE/source entry points | `autoReloadFromDisk`, preserve-formatting, read-only, and related load policy |
| Parser behavior | Process/request ambient | `ParserIgnoreConfiguration`, `MSBUILD_PARSE_CONFIG`, `Directory.Parse.config` handling | Effective parser-ignore configuration identity; supporting source record for any config file consumed |
| Evaluation stage | Request | `ProjectOptions.EvaluationStage` | Exact `ProjectEvaluationStage` |
| Interactive mode | Request/effective | evaluation entry point, `BuildParameters`, `NuGetInteractive` coupling | Effective interactive booleans |
| Visual Studio request mode | Request/effective | `BuildingInsideVisualStudio`, evaluator `_isRunningInVisualStudio` | Effective value passed to SDK resolution |
| Visual Studio process mode | Process ambient | `BuildEnvironmentHelper.Instance.RunningInVisualStudio` | Effective value gating VS-only evaluation behavior |
| Startup and initial working-directory context | Request/process ambient | `BuildParameters.StartupDirectory`, process/thread setup | Exact initial values and source |
| Node count | Request/effective | `BuildParameters.MaxNodeCount`, built-in property initialization | Effective count |
| Culture, UI culture, and time zone | Process ambient | node setup and classified culture-sensitive operations | Effective names/IDs |
| OS and runtime semantics | Process ambient | `NativeMethodsShared`, runtime/process architecture | OS, runtime, architecture, bitness |
| Path/name comparison semantics | Process ambient/provider | filesystem and MSBuild comparers | Comparison semantic ID |
| Evaluation feature regime | Request/process ambient | `Traits`, `FeatureSwitches`, `Features`, `ChangeWaves` | Effective result-affecting switches and wave state |
| Evaluation-context sharing | Request | `EvaluationContext.SharingPolicy` | `Isolated`, `Shared`, or `SharedSDKCache` |
| Properties from command line | Request | `ProjectCollection.PropertiesFromCommandLine` | Property-name set |
| Process-frozen build locations | Process ambient | `BuildEnvironmentHelper.Instance` | Effective tools, SDK, extensions, VS, and executable roots actually exposed to evaluation |

Some process values are initialized once, while others such as selected traits can be
refreshed between builds in a reused node. Record effective values per evaluation rather
than assuming process lifetime implies request lifetime.

Current directory is also a per-operation input. Every consuming record must include the
effective directory and its source, such as project directory,
`FileUtilities.CurrentThreadWorkingDirectory`, or process current directory. The initial
snapshot is not a substitute for that value.

`TreatAsLocalProperty` is evaluation-derived project data, not a request input. Its
effective set is implied by the observed root/import sources and initial global
properties; it may be retained as derived output but is not a second external dependency.

### 2. Root, import, and generated project sources

`ProjectSourceObservation` owns the exact XML or object-model source consumed by
evaluation.

| Input | Observation seam | Required record |
| --- | --- | --- |
| Root project file | source acquisition, `ProjectRootElement.LoadDocument`, `XmlReaderExtension` | Root role, logical path, provider, consumed-content identity, encoding/BOM where applicable |
| Imported project file | evaluator import loader, `ProjectRootElementCache` | Import role, importing location, logical path/provider, consumed-content identity |
| PRE cache hit | `ProjectRootElementCache.Get` / `TryGet` | PRE object identity/version plus the source stamp captured when it was loaded |
| In-memory `ProjectRootElement` | `Project.FromProjectRootElement`, PRE object model | Object/provider identity and version; use `ProjectRootElementLink.Version` for linked hosts |
| `XmlReader` / `TextReader` source | `Project.FromXmlReader` and source loaders | Host source identity/version and consumed-character fingerprint; otherwise non-cacheable |
| Unsaved IDE document | project-system source provider | Document identity and monotonic version |
| SDK-result synthetic project | `CreateProjectForSdkResult` | Generator identity plus the exact `PropertiesToAdd` / `ItemsToAdd` result that produced the XML |
| Solution metaproject or generated wrapper | generation owner | Generator identity, request inputs, generated-source fingerprint, and a separate observation report |

For disk sources, compute identity from the bytes consumed by the parser. Do not reopen the
file after evaluation to reconstruct the dependency.

The PRE cache should eventually retain:

```text
ProjectRootElement
+ ProjectSourceStamp
```

On a cache hit, the exact PRE object/version is observable even if the old source stamp is
missing. The observation is then complete for the object consumed, but disk-backed future
reuse remains blocked until load-time source provenance is available.

Root observation requires the session to begin before project-source acquisition, not in
the `Evaluator` constructor after a PRE already exists.

### 3. File content reads

| Operation | Observation seam | Required record |
| --- | --- | --- |
| Full text read | `IFileSystem.ReadFileAllText` | Logical path, provider identity, returned-content hash, success/failure |
| Full byte read | `IFileSystem.ReadFileAllBytes` | Logical path, provider identity, returned-byte hash, success/failure |
| Text reader | `IFileSystem.ReadFile` | Path/provider and exact consumed characters or authoritative provider content token |
| Stream read | `IFileSystem.GetFileStream` | Path, mode/access, consumed-byte identity or authoritative provider token |
| Direct XML stream | `XmlReaderExtension` and PRE loaders | Supporting read owned by `ProjectSourceObservation` |
| Direct evaluator `System.IO` read | call site or classified property-function dispatcher | Route through the evaluation provider or record explicitly |

Unknown or partial stream consumption is incomplete unless the semantic owner records the
exact consumed content or the provider supplies an authoritative content version.

### 4. File and directory probes

| Operation | Observation seam | Required record |
| --- | --- | --- |
| File existence | `IFileSystem.FileExists` | Logical path, provider, present/missing/failure |
| Directory existence | `IFileSystem.DirectoryExists` | Logical path, provider, present/missing/failure |
| File-or-directory existence | `IFileSystem.FileOrDirectoryExists` | Requested API and exact returned outcome; actual kind only when authoritative |
| `Exists(...)` condition | condition evaluator / evaluation filesystem | Condition role plus underlying probe |
| Optional or missing import | import/search owner | Supporting negative probe linked to the import/search record |
| Empty-import decision | `ProjectRootElement.IsEmptyXmlFile` / `IgnoreEmptyImports` path | Exact content/probe outcome linked to import selection |
| Direct `FileSystems.Default` probe | evaluator call sites | Route through the per-evaluation provider |
| `FileUtilities.*NoThrow` probe | helper call sites | Adopt existing `IFileSystem` overloads and preserve current Boolean/failure semantics |
| Unix-path adjustment probe | `FileUtilities.MaybeAdjustFilePath` / `LooksLikeUnixFilePath` | Candidate path, effective base directory and its source, directory probe, and normalized result |

Current Boolean APIs can conflate missing with access or I/O failure. Record only the
outcome the API proves; add an issue when a negative result is ambiguous rather than
inventing a more precise failure.

### 5. File metadata and path identity

| Input | Observation seam | Required record |
| --- | --- | --- |
| Attributes | `IFileSystem.GetAttributes` or classified function | Exact returned attributes and outcome |
| Last-write time | `IFileSystem.GetLastWriteTimeUtc` or classified function | Exact returned value and outcome |
| Creation, last-access, and local/UTC write times | classified `File` / `Directory` / `FileSystemInfo` member | Member, logical path, exact returned value, and time-zone dependency for local-time variants |
| Length and other `FileInfo` / `DirectoryInfo` fields | classified member dispatcher | Member, logical path/provider, exact value |
| Built-in item metadata `%(ModifiedTime)`, `%(CreatedTime)`, `%(AccessedTime)` | `ItemSpecModifiers` / `FileUtilities.GetFileInfoNoThrow` | Metadata kind, unescaped item spec, effective base directory/source, exact returned string, and time-zone dependency |
| Built-in path metadata such as `%(FullPath)`, `%(RootDir)`, `%(RelativeDir)` | `ItemSpecModifiers` | Metadata kind, item spec, effective base directory/source, exact returned value |
| Symlink/reparse target | classified member/provider | Logical path, target, and provider semantics |
| Path normalization/full-path result | classified path member or evaluator helper | Input, base/current directory when consumed, and exact normalized result |
| Path comparison/case behavior | request semantic snapshot | Comparison semantic ID |

Metadata is recorded only when evaluation consumes it. A timestamp is not a substitute for
content identity.

`FileUtilities.GetFileInfoNoThrow` does not have the observable `IFileSystem` overload
available to the existence-probe helpers. Built-in item metadata therefore needs an
explicit native seam or provider-aware overload.

### 6. Raw directory enumeration

| Input | Observation seam | Required record |
| --- | --- | --- |
| File enumeration | `IFileSystem.EnumerateFiles` | Root, pattern, recursion, provider, returned ordered members, completion |
| Directory enumeration | `IFileSystem.EnumerateDirectories` | Same |
| File-system-entry enumeration | `IFileSystem.EnumerateFileSystemEntries` | Same |
| Partial enumeration | recording iterator | Members consumed and partial state; mark incomplete unless the semantic owner proves the prefix is sufficient |
| Enumeration failure | recording iterator | Exact proven failure/outcome |
| Direct `Directory.*` enumeration | evaluator or classified property-function call site | Route through the evaluation provider |

Raw enumerations are supporting evidence for a glob when a `GlobObservation` owns the
semantic result.

### 7. Globs, lazy item specs, and wildcard imports

The primary observer belongs at the evaluator boundary that decides what item or import
membership is consumed, not solely inside `FileMatcher`.

| Input | Observation seam | Required record |
| --- | --- | --- |
| Item include glob | `EngineFileUtilities.GetFileList`, lazy item evaluator, `FileMatcher` callback | Expanded include, post-filter effective excludes, root, comparison semantics, final ordered membership; retain authored/dropped excludes as supporting evidence |
| Lazy wildcard preservation | `EngineFileUtilities.GetFileList` | Decision to preserve rather than enumerate, preserved expression, and reason |
| Drive-enumerating or invalid wildcard rejection | `EngineFileUtilities.GetFileList` | Exact decision/failure before `FileMatcher` is called |
| Import wildcard | import expansion owner plus `FileMatcher` | Import role, expanded specs, final ordered matches |
| `FileMatcher` result | explicit Framework-to-Build observation callback | Search action, exclude spec, returned membership, failure/completion state |
| Per-context/process glob cache hit | `EvaluationContext.FileEntryExpansionCache`, `FileMatcher.s_cachedGlobExpansions` | Request plus final semantic result; replay provenance when available |
| Non-context `FileMatcher.Default` use | call site | Supply the observed context/callback or mark incomplete |

Current `FileMatcher` cache entries retain membership but not every semantic output such as
search action or failure details. Capture the request before lookup and the final result at
the owning evaluator boundary; do not reconstruct missing semantics from a cache hit.

Access denied and an empty directory may be indistinguishable in existing APIs. Record the
proven outcome and an ambiguity issue.

### 8. Upward, import, and fallback searches

Search results depend on every candidate checked before the selected result.

| Search | Observation seam | Required record |
| --- | --- | --- |
| `GetPathOfFileAbove` | classified intrinsic / `FileUtilities` | Start directory, target name, ordered candidate probes, selected path/none |
| `GetDirectoryNameOfFileAbove` | same | Same |
| `Directory.Build.props` / `Directory.Build.targets` | common props/targets search owner | Ordered candidates and selected source |
| Import fallback paths | evaluator import search, `ProjectImportPathMatch` tables | Ordered roots/candidates and selected import |
| Toolset/SDK location ascent | owning provider helper | Ordered probes and selected result |
| Fallback-root directory memo | `Evaluator._fallbackSearchPathsCache` / `EngineFileUtilities.IOCache` | Supporting `PathProbeObservation` with expanded extension root, memoized Boolean, default-filesystem identity, and first-observation/stale-across-evaluations state |
| `Directory.Parse.config` discovery | `ParserIgnoreConfiguration`, project-directory ascent, `MSBUILD_PARSE_CONFIG` paths | Ordered explicit/upward candidates, default-filesystem probes, selected files/none |

The selected file is separately owned by `ProjectSourceObservation`. Negative nearer
candidates remain part of the `SearchObservation` because creating one can change the
selected source.

`_fallbackSearchPathsCache` is a process-static directory-existence memo, not a cached
search result. If its Boolean cannot be replayed with provenance, the supporting probe is
incomplete; the owning import-search record must not hide that gap.

### 9. Property-function dispatch and classification

Property-function support is a closed, per-member contract.

The authoritative classification is generated from the declarations in
`Constants.InitializeAvailableMethods` and the intrinsic aliases used by the dispatcher.
It is per member/overload, not merely per type, and a member may have multiple effects:

```text
Pure
FileContent
PathProbe
FileMetadata
DirectoryEnumeration
Environment
Registry
Ambient
Volatile
SideEffect
OpaqueUnsupported
```

At runtime, every invoked member is checked against that classification. An unclassified
member executes unchanged and adds `UnclassifiedPropertyFunction`; it must never silently
count as pure. This runtime guard is required because type-level allowlist entries can gain
new members when the runtime changes.

Whether `RestrictPropertyFunctionReceivers` is off, as in the default unrestricted path,
or on with a curated receiver set, an instance invocation is unsupported unless that exact
member is classified. This closes the otherwise unbounded instance-member path.

`EnableAllPropertyFunctions` immediately adds `AllPropertyFunctionsEnabled` and blocks
future reuse.

Important families include:

| Member family | Required handling |
| --- | --- |
| `System.IO.File` / `Directory` reads, probes, metadata, and enumeration | Route to the matching filesystem record |
| `FileInfo` / `DirectoryInfo` instance members | Classify each external field/read; no type-wide assumption |
| `Path.GetFullPath` and equivalent normalization | Record current/base directory and exact result |
| `Path.GetTempPath` | Ambient |
| `Path.GetRandomFileName` | Volatile |
| `Path.GetTempFileName` | Volatile plus side effect |
| Culture-sensitive parse/format and `StringComparer.CurrentCulture*` | Record effective culture |
| `System.Environment` callable members | Route to live environment or ambient records |
| `Microsoft.Build.Utilities.ToolLocationHelper` | Multi-effect filesystem/Registry/SDK/process-cache dependency; non-cacheable until explicitly instrumented |
| `[MSBuild]::FileExists`, `DirectoryExists`, `DoesTaskHostExist` | Route probes through the evaluation provider |
| `[MSBuild]` tools/SDK/VS/program-files location intrinsics | Record the `BuildEnvironmentHelper` values they expose |
| `[MSBuild]::AreFeaturesEnabled` / `CheckFeatureAvailability` | Link to the request feature-regime snapshot |
| `[MSBuild]::RegisterBuildCheck` | Record analyzer assembly content plus registration side effect; non-cacheable until replay is designed |
| `DateTime.Now`, `DateTime.UtcNow`, `DateTimeOffset.Now`, `Guid.NewGuid`, tick count, working set, stack trace | Volatile/unsupported |

`PropertyFunctionUsageObservation` records the invoked type, member, overload, and assigned
effect classes. External dependencies themselves remain owned by the matching typed
record.

### 10. Evaluation-time registrations

Full evaluation produces registrations in addition to properties and items. External
decisions that change those registrations must be observed.

| Registration | Observation seam | Required record/policy |
| --- | --- | --- |
| Evaluated `UsingTask` | `TaskRegistry.InitializeTaskRegistryFromUsingTaskElements` | Source location, condition result, task name/factory, assembly file/name, runtime, architecture, override, and effective registration |
| Code/XAML task-factory assembly redirection | `TaskRegistry.RegisterTasksFromUsingTaskElement` | Original assembly path, ordered existence probes, selected replacement path |
| Project-cache plugin descriptor | `Evaluator.CollectProjectCachePlugins` | Effective plugin path, metadata/settings, Visual Studio mode, and process-global registration side effect |

`UsingTask` path probes are supporting `PathProbeObservation`s owned by the effective
`TaskRegistrationObservation`. Assembly loading during task execution is outside this
inventory.

`CollectProjectCachePlugins` mutates `BuildManager.ProjectCacheDescriptors`. Record the
descriptor and `EvaluationSideEffectObservation`; block future reuse until the
registration can be replayed safely.

### 11. Imported environment-derived property reads

Imported environment properties are a snapshot used to initialize evaluation properties;
they are not equivalent to live `System.Environment` calls.

| Input | Observation seam | Required record |
| --- | --- | --- |
| Imported environment snapshot | `ProjectCollection.EnvironmentProperties`, `BuildParameters.EnvironmentProperties`, `BuildParameters.BuildProcessEnvironment` | Snapshot/provider identity and MSBuild environment-name semantics |
| Present `$(NAME)` whose winning value came from the imported environment | an always-on observation wrapper around evaluator property lookup | Name, source, present state, exact effective value |
| Undefined `$(NAME)` | the same exact property lookup path plus imported-environment table membership | Name and absent state in the imported environment snapshot |
| Environment value overwritten before it is read | property predecessor/source tracking | Record the actual winning source; do not record an unused original environment value |
| SDK-injected environment/property value | SDK result application plus later property read | SDK result owns the injected value; environment record notes whether it was consumed |

Do not depend on `PropertyTrackingEvaluatorDataWrapper.TrackPropertyRead`, because event
emission is trait/logging gated. Reuse or extend the wrapper's unconditional `GetProperty`
lookup path when observation is enabled.

`PropertiesUseTracker` may remain a diagnostic aid, but it is not the primary missing
environment seam: it intentionally skips some undefined-property patterns and cannot
prove imported-environment membership.

Raw environment values remain internal and must be redacted from diagnostics.

### 12. Live environment and ambient operations

Only members admitted by the closed classification are supported. Capture their actual
return before MSBuild escaping or formatting changes it.

| Operation | Required record/policy |
| --- | --- |
| `GetEnvironmentVariable(name[, target])` | Name, target, present/missing, exact returned value |
| `GetEnvironmentVariables([target])` | Exact returned name/value snapshot and platform name semantics |
| `ExpandEnvironmentVariables(text)` | Input, output, and names read from an observable provider; otherwise record the full returned environment snapshot or mark non-cacheable |
| `GetFolderPath` | Requested folder/option and exact returned path |
| `GetLogicalDrives` | Exact returned drive set |
| Current-directory-consuming member | Exact current directory and operation result |
| Culture/time-zone-sensitive member | Effective culture/time-zone identity and exact result |
| OS/runtime/architecture/process property | Member name and exact returned typed value |
| Time-, random-, memory-, or process-state value | Volatile/unsupported unless a stable request snapshot explicitly owns it |

Engine-owned environment inputs used by toolset selection, SDK discovery, traits, and
`BuildEnvironmentHelper` are recorded by their owning request, toolset, or SDK record
rather than duplicated here.

Raw environment values remain internal and must not be emitted in diagnostics.

### 13. Registry expressions and functions

| Input | Observation seam | Required record |
| --- | --- | --- |
| `$(Registry:...)` | `PropertyExpander.ExpandRegistryValue` | Original request, hive/view/key/value, exact returned string, proven failure |
| `[MSBuild]::GetRegistryValue` | property-function dispatcher / `IntrinsicFunctions` | Key/value/default and exact typed result |
| `[MSBuild]::GetRegistryValueFromView` | same | Ordered views and exact typed result |
| Registry reads inside an explicitly supported helper | typed Registry provider/dependency contract | Exact request and typed returned value |
| Registry subkey/value enumeration | typed Registry provider | Ordered request and returned names/values |

Current APIs may not distinguish a missing key from a missing value, or a stored value
equal to the supplied default. Record only what the API proves until a typed provider can
distinguish more.

The provider must return and record the value atomically; do not re-read Registry state to
build the observation.

### 14. Toolset Registry and configuration inputs

`ToolsetObservation` owns the selected effective toolset.

| Input | Observation seam | Required record |
| --- | --- | --- |
| ToolsVersions subkey enumeration | `ToolsetRegistryReader` | Hive/view/key and exact returned subkeys |
| Toolset Registry properties | `ToolsetRegistryReader` / `RegistryKeyWrapper` | Key/value request and exact typed result |
| Toolset configuration files | `ToolsetReader` / configuration reader | Source identity and selected properties |
| Selected toolset | `ProjectCollection`, `ToolsetProvider` | Requested/effective toolset, paths, source, and selection reason |
| Import-path tables/extensions paths | toolset/import configuration | Ordered effective roots and source |
| Toolset collection changes | `ProjectCollection.ToolsetsVersion` | Exact generation/version |
| Toolset environment inputs | toolset reader / `BuildEnvironmentHelper` | Named value and exact consumed result |

Toolset discovery can happen before the evaluation session. Capture load-time provenance
when it occurs and attach the effective toolset snapshot at evaluation start.

Registry-backed toolset discovery is platform/runtime specific; do not require Registry
records on paths where MSBuild did not use Registry.

### 15. SDK request and result

`SdkResolutionObservation` treats SDK resolution as an opaque request-keyed cache.
Resolver discovery, manifests, assemblies, file probes, and internal dependencies are not
observed.

| Input | Observation seam | Required record |
| --- | --- | --- |
| SDK request | evaluator / `ISdkResolverService.ResolveSdk` | Name, requested/minimum version, project/solution paths, interactive value, effective `IsRunningInVisualStudio`, and failure mode |
| SDK cache lookup | `CachingSdkResolverService`, `OutOfProcNodeSdkResolverService` | Request record and cache hit/miss |
| Selected SDK result | `SdkResult` | Success/failure, path/version, additional paths, properties/items/environment additions, warnings, and errors |
| SDK-result synthetic XML | `CreateProjectForSdkResult` | Generated source linked to the selected SDK result |
| SDK-result host-path compatibility probe | evaluator `dotnet.exe` probe before `DOTNET_HOST_PATH` injection | Candidate path, positive/negative default-filesystem result, and injected value if any |
| SDK-injected properties/items/environment values | evaluator data | Exact values and whether evaluation later consumed them |

The SDK result cache owns reuse validity. MSBuild returns the stored `SdkResult` for the
same cache key until that cache is cleared; the evaluation observer does not independently
detect SDK installation, workload, NuGet, resolver, or manifest changes.

The current caches key primarily by SDK name. A complete request key and explicit cache
lifetime/epoch are required before extending this reuse beyond the existing cache scope.
The observation records omitted request fields but must not imply that the current cache
used those fields when selecting the result.

### 16. Shared caches and provider provenance

An outer observer may see the exact value returned by an inner cache even though it does
not see the original operation. That is complete observation of the consumed value, but it
can still be a future reuse blocker.

| Cache/provider | What must be captured | State without provenance |
| --- | --- | --- |
| `CachingFileSystemWrapper` | Exact returned probe/metadata value plus cache/provider identity | Observed but non-cacheable if no stable provider contract |
| `FileUtilities.FileExistenceCache` | Route through observed call sites or replay exact probe result | Incomplete if it bypasses the observer |
| Per-context `FileEntryExpansionCache` | Final semantic glob result plus cache identity | Observed but non-cacheable until provenance is replayable |
| `FileMatcher.s_cachedGlobExpansions` | Same | Same |
| Host `IDirectoryCache` | Exact returned members, provider/cache identity, optional generation | Observed but non-cacheable without stable identity |
| `ProjectRootElementCache` | PRE object/version and load-time `ProjectSourceStamp` | Object observed; disk reuse blocked without source stamp |
| `Evaluator._fallbackSearchPathsCache` | Expanded extension root and memoized default-filesystem directory-existence Boolean | Incomplete when the process-static probe bypasses observation |
| `CachingSdkResolverService` | Full SDK request, exact cached `SdkResult`, and hit/miss | Existing cache lifetime owns validity |
| `OutOfProcNodeSdkResolverService` response cache | Full request/result and hit/miss | Existing cross-node cache lifetime owns validity |
| Toolset/configuration caches | Effective toolset plus source records/generation | Non-cacheable without source provenance |
| `ToolLocationHelper` static caches | Exact helper result plus underlying dependency manifest | Non-cacheable until instrumented |
| Imported-environment snapshot/cache | Snapshot identity and exact property reads | Non-cacheable if the actual snapshot cannot be identified |

Shared `EvaluationContext` use, including `ProjectGraph`, must not suppress observations.
Cache hits either replay the original semantic record or let the owning boundary record
the final result and attach an appropriate reuse blocker. SDK result caches are the
exception: their existing cache lifetime owns validity.

### 17. Custom hosts and providers

| Provider | Required record | Policy |
| --- | --- | --- |
| Custom `MSBuildFileSystemBase` | Per-operation provider identity and exact returned result | Observation can be complete; future reuse requires stable identity/version |
| Custom `IDirectoryCacheFactory` | Provider/cache identity, exact members/results, optional generation | Same |
| Linked/in-memory PRE | Document/provider identity and `ProjectRootElementLink.Version` or equivalent | Supported when version is authoritative |
| Unsaved IDE source | Document identity and monotonic content version | Supported only with host contract |
| `XmlReader` supplied by host | Source identity/version and consumed-content fingerprint | Non-cacheable without stable source identity |
| Pre-evaluated `ProjectInstance` | Host/source identity and state version | Outside normal project-evaluation observation unless the host supplies provenance |

`MSBuildFileSystemBase` members can fall back to `FileSystems.Default`. Record provider
identity per operation so a partially overridden custom filesystem does not make host and
physical-disk results indistinguishable.

### 18. Volatile values and evaluation-time side effects

Volatile values and side effects are not ordinary dependencies. They execute unchanged,
are recorded, and block future reuse unless a separate replay contract is designed.

| Case | Record/policy |
| --- | --- |
| Wall-clock time, random values, GUID creation, tick count | `UnsupportedVolatile` with exact invoked member |
| Working set, stack trace, rapidly changing process state | `UnsupportedVolatile` |
| `Path.GetTempFileName` or another file-creating function | Returned value plus `EvaluationSideEffectObservation`; non-cacheable |
| `[MSBuild]::RegisterBuildCheck` | Analyzer assembly dependency plus registration side effect; non-cacheable until replayable |
| VS project-cache plugin collection | Effective descriptor plus `BuildManager.ProjectCacheDescriptors` registration side effect; non-cacheable until replayable |
| Any write/delete/mutation reached through an unclassified reflected member | Execute unchanged, record unsupported side effect when detectable, non-cacheable |

The observation prototype does not replay side effects.

### 19. Completion and coverage metadata

The report records both observer trustworthiness and future-reuse blockers.

| Input | Record |
| --- | --- |
| Evaluation success/failure | Boolean and failure category |
| Observation schema/classification version | Version IDs |
| Category implementation coverage | `NotImplemented`, `Partial`, `Complete` |
| Per-evaluation category state | `NotExercised`, `Observed`, `Incomplete`, `Unsupported` |
| Future reuse disposition | `NoKnownReuseBlocker` or `Blocked(reason)` composed from the table above |
| Observer exception | `ObservationIncomplete` |
| Dropped/truncated record | Counter plus `ObservationIncomplete` |
| Conflicting primary observation | Dependency identity and `ConflictingObservation` |
| Partial enumeration/stream | Completion state and typed issue |
| Unsupported provider/function | Typed reason |
| Known bypass exercised without observation | Typed bypass ID |

Coverage counters and differential-test failures are internal diagnostics, not
user-visible evaluation warnings.

## Known native bypass checklist

This is an implementation checklist, not a second set of dependency categories.

| Bypass | Owning category |
| --- | --- |
| `XmlReaderExtension` / PRE raw source streams | Project source |
| `ProjectRootElement.IsEmptyXmlFile` and source reload helpers | Project source / import search |
| Evaluator import `FileSystems.Default.FileExists` call sites | Path probe / import search |
| `IntrinsicFunctions.FileExists`, `DirectoryExists`, `DoesTaskHostExist` | Path probe |
| `EngineFileUtilities.GetFileList` decisions before `FileMatcher` | Glob |
| `FileMatcher.Default` and process/per-context glob caches | Glob |
| `FileUtilities.MaybeAdjustFilePath` / `LooksLikeUnixFilePath` | Path probe/path normalization |
| `ItemSpecModifiers` / `FileUtilities.GetFileInfoNoThrow` | File metadata / path identity |
| `Expander.ItemExpander.Transforms` direct `FileOrDirectoryExists` | Path probe |
| `Evaluator._fallbackSearchPathsCache` process-static directory memo | Path probe supporting import search |
| `ParserIgnoreConfiguration` default-filesystem probes and raw config reads | Parser-config search / project source |
| SDK request/result cache boundaries | SDK resolution |
| Evaluator `dotnet.exe` probe used to inject `DOTNET_HOST_PATH` | SDK resolution / path probe |
| `TaskRegistry` task-factory assembly redirection probes | Task registration / path probe |
| `Evaluator.CollectProjectCachePlugins` process-global registration | Evaluation side effect |
| `ToolLocationHelper` direct filesystem/Registry reads and static caches | Classified property function |
| `BuildEnvironmentHelper` and parser/toolset initialization before evaluation | Request/toolset/source snapshot |
| Direct `FileInfo`, `DirectoryInfo`, `File`, or `Directory` property-function paths | File content/probe/metadata/enumeration |

## Current prototype coverage

The native prototype implements the closed observation model, while provider provenance
remains intentionally fail-closed.

Report collection order, including nested named-value and SDK-item collections, is
unspecified and is not part of dependency identity unless a field is explicitly documented
as ordered. Semantic payloads such as glob members, enumeration members, and search
candidates retain the order consumed by evaluation.

| Category | Current coverage |
| --- | --- |
| Session lifetime | Per-evaluation session plus load-time source stamps; source loading occurs before the evaluator session but hashes bytes as they are consumed |
| Request snapshot | Typed immutable snapshot includes global properties, load/evaluation policy, host/runtime semantics, feature waves, and result-affecting escape hatches |
| Root/import sources | Raw-byte hash and encoding for disk sources; object/link identity and version for in-memory/linked sources; opaque reader providers block reuse |
| Full text/byte reads | Typed hash domains distinguish raw bytes, decoded text, decoded line sequences, and parsed XML |
| Streams/readers | Marked incomplete unless a semantic owner or provider supplies full-content identity |
| Probes | Provider-tagged file/directory/file-or-directory outcomes; swallowed I/O failures are recorded as ambiguous |
| Metadata | Provider-tagged attributes, timestamps, lengths, path facts, and property-function member identity |
| Built-in item metadata | `ItemSpecModifiers` records value plus effective base directory without treating normal relative specs as incomplete |
| Raw enumeration | Standalone enumerations record ordered fingerprint, count, completion, and provider; optional diagnostics retain members |
| Globs and searches | Semantic glob ownership suppresses duplicate parallel walks; upward, parser, and import-fallback searches retain ordered candidates |
| `UsingTask` / project-cache registrations | Effective task registrations and VS project-cache registration side effects are recorded |
| Environment and Registry | Imported/live/missing environment reads and both Registry expression/function paths are recorded; raw values remain internal |
| Toolset | Effective toolset and properties are recorded; pre-session Registry/config provenance remains an explicit reuse blocker |
| SDK | Request, final result payload, and cache hit/miss are recorded; resolver internals are opaque |
| Shared caches/providers | Final consumed values are recorded; caches/providers without replayable provenance or stable identity block reuse |
| Property-function classification | Per-member fail-closed classifier records external effects, volatile values, side effects, and unknown members; pure calls are omitted |
| Unsupported/coverage reporting | Schema/classification versions, per-category coverage/state, typed reasons, conflicts, partial operations, and atomic completion are present |

## Not evaluation dependencies

The native observer does not record:

- target/task execution inputs;
- compiler inputs;
- build outputs;
- logger-only state that cannot affect evaluation;
- repeated reads of already evaluated properties as new external dependencies.

Those belong to execution/build caching or diagnostics, not project evaluation
observation.

## Recommended implementation order

This order closes the largest native gaps while keeping each step measurable:

1. Start the session at the evaluation entry point and capture the request/process
   semantic snapshot.
2. Add root/import source identities, parser/load-policy inputs, and PRE source-stamp
   replay.
3. Route direct evaluator filesystem bypasses, including built-in item metadata, through
   observable providers.
4. Add semantic glob, lazy-item, wildcard-import, and search observations.
5. Add effective `UsingTask` registration observation and VS project-cache side-effect
   reporting.
6. Add always-on imported-environment property lookup observation.
7. Generate the closed per-member property-function classification and instrument
   filesystem, live-environment, ambient, volatile, and side-effect members.
8. Add Registry expressions/functions and toolset Registry/configuration provenance.
9. Add SDK request/result/cache-hit observation and synthetic SDK sources.
10. Define the complete SDK request key and cache lifetime/epoch.
11. Add shared-cache provenance for PRE, filesystem, glob, search, directory,
    toolset, environment, and `ToolLocationHelper` caches.
12. Add custom-provider contracts where custom-host support is required.
13. Enforce the closed taxonomy with static checks, mutation tests, internal coverage
    counters, and native/Detours differential tests.

At every step, rerun the benchmark and report marginal and cumulative overhead. No cache,
validation, invalidation, watcher, journal, or persistence implementation is required for
this inventory.
