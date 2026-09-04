# Evaluation input recording

Status: proposed, opt-in prototype. Part of the evaluation cache work in dotnet/msbuild#14234; implements the observation layer asked for in dotnet/msbuild#14688.

Set `MSBUILDRECORDEVALUATIONINPUTS=1` and every evaluation records the inputs it consumed into `ProjectInstance.EvaluationInputs`, or `Project.EvaluationInputs` for a design-time evaluation. When the variable is not set the engine pays one null check per seam and allocates nothing.

## What is recorded

`EvaluationInputs` is not modified after evaluation and holds:

| Member | Contents | How a cache validates it |
| --- | --- | --- |
| `Key` | Project path, sorted global properties, tools version and path, sub-toolset version, a fingerprint of the toolset's properties and import search paths, evaluation stage, load settings, interactive flag, node count, startup directory, working directory (the build thread's when the in-process node set one), culture and UI culture, engine version, disabled ChangeWave, fingerprint of the imported environment properties, fingerprint of the parser configuration loaded from `Directory.Parse.config` files | Exact comparison before lookup; a different key is a different entry |
| `Files` | Every file or directory evaluation read, probed, or enumerated, keyed by full path, with the kind found (`Missing`, `File`, `Directory`), last write time, and length. Keys are interned, so manifests of different projects share one string per SDK file | One stat per entry; any difference means the entry is stale |
| `EnvironmentReads` | Variables read through `[System.Environment]::GetEnvironmentVariable` or referenced by `ExpandEnvironmentVariables`, missing ones included | Compare with the next request's environment |
| `SdkResolutions` | Each SDK reference and the `SdkResult` it produced | Resolve again and compare |
| `NonCacheable` | Why the result must not be reused, with `NonCacheableDetail` naming the input | Never reuse |

Missing paths matter as much as existing ones: an `Exists()` that returned false, a missing import, or an upward-search candidate that was not there all become `Missing` entries, so creating the file invalidates the result.

Glob membership is validated through the directories the glob traversed. A file added to or removed from a traversed directory changes that directory's timestamp on NTFS, ext4, and APFS.

## Where inputs are observed

| Input | Seam |
| --- | --- |
| Root project and imports | `Evaluator.PerformDepthFirstPass` records each `ProjectRootElement` file with the timestamp it had when the element was read, taken before the content is read so a write during the read counts as a change, so content served from a stale `ProjectRootElementCache` entry or an element with unsaved edits is never reusable |
| `Directory.Parse.config` files the project collection loaded | Recorded as files when evaluation starts; what they allow the parser to skip is fingerprinted in the key |
| Missing imports, empty imports, fallback search roots, the SDK host probe | The existing probes in `Evaluator` report their result |
| `Exists()` conditions, `GetPathOfFileAbove`, `Directory.Build.props` discovery, glob traversal, `@(Items->Exists())`, file reads through `IFileSystem` | `RecordingFileSystem` wraps the evaluation file system; `EvaluationContext.ContextWithFileSystem` installs it in a per-evaluation context copy that keeps the context's glob expansion cache. The copy's `FileMatcher` reports every directory an expansion enumerates or probes for existence to the recorder and stores that list next to the cached file list, so a reused expansion replays the same directories without touching the file system, and an expansion cached before any recorder saw it is enumerated again. An import `Exists()` that a design-time evaluation answers from an already loaded project is recorded as a positive probe at the condition node, so a file deleted behind the cache conflicts with the answer |
| Property functions | `Expander.Function.Execute` reports receiver, member, arguments, and result after both the well-known fast path and reflection; `PropertyFunctionEffects` classifies them |
| `$(Registry:...)`, `%(ModifiedTime)` and the other item timestamps, whether referenced in an expression, as a transform, as the metadata name given to `Metadata`, `HasMetadata`, `WithMetadataValue`, `WithoutMetadataValue`, or `AnyHaveMetadataValue`, or in `MatchOnMetadata` | Mark the evaluation non-cacheable |
| SDK resolution | `Evaluator` records the reference and result after `ResolveSdk` |

The recorder travels with the `EvaluationContext` copy and, for expander seams, with `PropertiesUseTracker`, which already reaches every property expansion. There are no thread-static or process-global hooks.

## Property function classification

`PropertyFunctionEffects` maps each allowed receiver type and member to an effect: pure, a file or directory probe, a file or directory read, an environment read or expansion, a registry read, volatile, or unsupported. Anything not classified is unsupported and makes the evaluation non-cacheable rather than being silently ignored.

- Pure: everything on `System.String`, `System.Math`, `System.Version`, the numeric types, `Convert` except `ToDateTime`, and `Path`. The working directory is part of the key, so `Path.GetFullPath` and `Directory.GetCurrentDirectory` are pure.
- `[MSBuild]::*` intrinsics: every one evaluation can reach is classified, `FileExists` and `DirectoryExists` as probes and the registry functions as registry reads; an intrinsic not in the table, `RegisterBuildCheck` today or one added later, is unsupported until it is classified.
- `File` and `Directory`: `Exists` is a probe, content reads and enumerations record the path, `GetLastWriteTime` records the path. Attributes and creation or access times are unsupported, because the manifest does not hold those fields and a change to them alone would validate as unchanged.
- `Environment`: `GetEnvironmentVariable` records the variable it reads; `ExpandEnvironmentVariables` records every `%NAME%` reference in its argument, present or missing, which is what Roslyn's `eng/targets/Settings.props` needs to stay cacheable. Process constants such as `MachineName` and `ProcessorCount` are pure; everything else is unsupported.
- `ToolLocationHelper` reads installed SDKs and frameworks, which do not change while the process lives, so its members are pure unless an argument names a location, a rooted path or a relative one with a directory separator, which makes the call unsupported; `FindRootFolderWhereAllFilesExist`, `GetAssemblyFoldersFromConfigInfo`, and `ClearSDKStaticCache` are unsupported outright.
- Volatile: `DateTime.Now`, `UtcNow`, `Today`, `Guid.NewGuid`, and the `DateTime`, `DateTimeOffset`, and `Convert` parsers, which fill in today's date for a time-only string.
- Instance calls run on values evaluation already produced; only `FileSystemInfo` and `DriveInfo` members that go beyond the path reach the disk, and every object that does originates from a classified static call.

## Validation

`EvaluationInputValidator.IsCurrent` re-checks a manifest: one stat per recorded path, one environment lookup per recorded read, and one resolution per recorded SDK reference through a caller-supplied delegate. It returns at the first difference and names it, and an exception during a check is a miss, never a failed build. Paths are checked in the order they were recorded, so the project file and its imports come first and a stale check usually returns after a few stats.

SDK results are validated by resolving the reference again. Through `CachingSdkResolverService` that costs one resolution per SDK per build submission, which is what every build pays today before its first evaluation; within a submission the cached result is returned, and the service caches deliberately so a result cannot change mid-submission. A cache that validates many projects per submission therefore pays the resolver once, not per project.

## Cost

Measured with `EvaluationInputRecordingBenchmark` on 2026-09-04 (Windows 11, Ryzen 7 5700X3D, .NET 11 RC1, in-process job, gen0 budget pinned) on OrchardCore.Cms.Web, OrchardCore, and Roslyn Workspaces; the tables are in the pull request.

- Recording adds one stat per recorded path: 1.02x to 1.09x of a fresh evaluation over three pinned runs, mean 1.05x, and 11 to 27 KB of allocation on 6 to 12 MB.
- Validating an unchanged manifest with the SDK results the submission already holds takes 0.8 to 2.9 ms, 2.5% to 5.5% of a fresh evaluation; a stale project file or import is found in 18 to 105 µs.
- Resolving every SDK reference again through a cold submission cache adds 7 to 9 ms, most of it in the .NET SDK resolver reading workload manifests, paid once per submission and independent of how many references a project has.
- In a shared context (`SharingPolicy.Shared`, which `ProjectGraph` uses; build-time `ProjectInstance` evaluation is isolated per project) the recording copy reuses the expansions the context has cached and replays the directories they depend on, so recording costs the same stats there: 1.07x to 1.10x of a shared-context evaluation, 1.3 to 3.7 ms on 18 to 38 ms. A project that is nothing but a recursive glob pays the stat of each recorded directory: 2.8 ms to 7.4 ms for 79 paths.

## Change detection

The manifest supports three ways to learn that an input changed; this change ships the first.

| Approach | How it uses the manifest | Cost and limits |
| --- | --- | --- |
| Manifest validation, shipped here | One stat per recorded path before a result is reused | Portable, no background state, cost proportional to manifest size; blind to changes within timestamp granularity and to timestamp-preserving edits |
| `FileSystemWatcher` | Watch the directories of recorded paths; an event marks the entries under it stale, so a hit needs no stats | Cheap hits; events can overflow or coalesce, so it accelerates validation rather than replacing it; needs a long-lived process |
| Windows USN journal | Read the records since the last cursor per volume and map file ids to entries | Survives process restarts, so it also serves a persistent cache; NTFS and ReFS only, needs a volume handle and cursor management per volume |

Phase 1 of dotnet/msbuild#14234 is an in-memory cache in the MSBuild server process, where validation on every hit is affordable. Watchers and the journal lower the cost of a hit without changing what is recorded: both need exactly the path set the manifest holds.

## Accepted limitations

- Timestamps and lengths, not content hashes, detect file changes. This matches `ProjectRootElementCache` and incremental build; a persistent cache can add hashing later without changing the manifest.
- Glob membership relies on the directory timestamp changing when a child is created, deleted, or renamed. NTFS, ext4, and APFS do this; some network and overlay file systems do not, and a cache on those must re-enumerate instead.
- The timestamp of a file read through a property function is taken when the read is recorded, so a change between the read and the stat is not detected until the next evaluation. Project files use the timestamp captured by `ProjectRootElement` when it was read.
- Reads through `Microsoft.Build.Utilities.ToolLocationHelper`, installed SDK and framework directories, and the imported environment are treated as constant for the process lifetime.
- Symbolic links and junctions are not followed. A recorded path whose final component is a link makes the evaluation non-cacheable, because the link's own timestamp does not change with its target; on Windows the reparse tag decides, on both target frameworks, so cloud placeholders and deduplicated files stay ordinary entries. A link in an intermediate component is transparent to the file system, so the target's timestamp is what gets recorded; re-pointing such a link is detected only when the new target's timestamps differ.
- A host file system (`EvaluationContext.Create` with an `MSBuildFileSystemBase`) or a host directory cache (`ProjectOptions.DirectoryCacheFactory`, which Visual Studio supplies) can answer from state the recorder does not see, so those evaluations are non-cacheable until such providers expose a snapshot identity. The same holds for the process-wide existence and enumeration caches behind `MsBuildCacheFileExistence` and `MsBuildCacheFileEnumerations`.
- The recorded `SdkResult` instances are the ones the resolver's own cache holds; nothing copies them, and nothing in the engine modifies a result after resolution.
- Process-wide switches read from the environment at startup (`Traits`, `FeatureSwitches`) are not in the key. A cache that outlives the process must add them to its header.
- In-memory projects, partial evaluations, lazy wildcard evaluation, and `MSBUILDENABLEALLPROPERTYFUNCTIONS` are never cacheable.

## Coverage evidence

`MSBuild.Benchmarks` has an opt-in comparison that evaluates a project under BuildXL Detours and compares every path the process touched with the recorded inputs: build with `-p:EnableEvaluationInputDetours=true` on Windows x64 and run `--evaluation-input-detours --project <path> [--global-property Name=Value]`. A touched path is explained when it was recorded, or when it was only probed or enumerated and its parent directory was recorded, since a directory's timestamp covers its membership but not the content of its files. The rest is printed for attribution, content reads separately from probes.

Runs on 2026-09-04 against the bootstrap .NET 11 SDK, one default outer and one inner-TFM evaluation per project. A content read is an access with read or write intent; probes and enumerations are the rest.

| Corpus | Evaluations | Recorded paths | Touched paths | Explained by the recording | Content reads not recorded | Detours-only paths, unique |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| OrchardCore `e3f8acb`, 232 projects | 464, all cacheable | 76,706 | 182,762 | 79,519 (76,125 exact, 3,394 probes under a recorded directory) | 26,569 | 849 |
| dotnet/msbuild, 39 Arcade projects, `OfficialBuildId` set | 78, all cacheable | 16,117 | 35,831 | 16,783 (15,884 exact, 899 probes under a recorded directory) | 5,570 | 416 |
| Roslyn `0f82fdec`, Microsoft.CodeAnalysis.Workspaces, `OfficialBuildId` set | 2, all cacheable | 1,086 | 1,595 | 1,102 (1,084 exact, 18 probes under a recorded directory) | 142 | 258 |

Every Detours-only path, reads included, fell into one of three groups, none of which can change an evaluation result without passing through a recorded seam:

- SDK resolver inputs: `global.json`, the `.editorconfig` and `.globalconfig` walk the .NET SDK resolver performs, NuGet settings, workload manifests and packs, resolver assemblies, and SDK version files. Validated by resolving again.
- Installed SDK, reference assembly, and Windows Kits directories probed by `ToolLocationHelper`. Process constants by design.
- Process runtime files: shared framework assemblies, time zone data, the harness itself, and the case-sensitivity probe file every process creates.

Recorded paths Detours did not see were directories whose existence evaluation checked through a cached answer; recording them is harmless.

The Arcade runs needed `OfficialBuildId` because `Version.BeforeCommonTargets.targets` derives `_BuildNumber` from `[System.DateTime]::UtcNow` in developer builds, which makes every Arcade project non-cacheable as intended. Recording stops at the first non-cacheable input, so the manifest of such an evaluation is partial by design.

## Out of scope

Cache storage, lookup, `ProjectInstance` materialization (dotnet/msbuild#14684), persistence, watcher and journal based invalidation (see Change detection), and cross-machine reuse.
