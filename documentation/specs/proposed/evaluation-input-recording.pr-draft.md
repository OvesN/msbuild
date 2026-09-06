<!-- Draft of the pull request description for this branch, kept here for review. Delete this file when the PR opens. -->

Fixes #14688. Part of #14234; the validator covers the detection side of #14538. `ProjectInstance` materialization is #14684 and stays separate.

### Context

Every build re-evaluates every project. A cache that reuses evaluation results has to know exactly what an evaluation read, so it can tell when a result is stale and when a result must not be reused at all. This adds that observation layer. It is opt-in, off by default, and does not change evaluation results.

### Changes Made

- `MSBUILDRECORDEVALUATIONINPUTS=1` makes every evaluation record its inputs into an immutable `EvaluationInputs`, exposed as `ProjectInstance.EvaluationInputs` and, for design-time evaluations, `Project.EvaluationInputs`. It holds a key (project path, global properties, toolset, startup and working directories, culture, engine version, fingerprints of the imported environment and of the parser configuration), every file and directory read, probed, or enumerated with the kind found, timestamp, and length, environment variables read through property functions, and SDK resolutions. Registry reads, item timestamps by any route, volatile or unclassified property functions, in-memory projects, symbolic links, host file systems and directory caches, process-wide file caches, and a few switches mark the evaluation non-cacheable with the reason.
- Seams: `RecordingFileSystem` wraps the evaluation file system in a per-evaluation `EvaluationContext` copy and sees `Exists()`, upward searches, glob traversal, `@(Items->Exists())`, and file reads; the copy keeps the context's glob expansion cache, and `FileMatcher` reports the directories an expansion enumerates or probes, stored with the cached file list and replayed when the expansion is reused; `Evaluator` records project sources with the timestamp they were read at (now taken before the content is read), the `Directory.Parse.config` files the collection loaded, import probes, and SDK results; `Expander` and `LazyItemEvaluator` report property functions, metadata expressions, and metadata names. With recording off each seam is one null check and nothing is allocated.
- `PropertyFunctionEffects` classifies every allowed property function; anything unlisted fails closed. `ToolLocationHelper` is pure only when no argument names a root to search, reads of fields the manifest does not hold are unsupported, and parsing a time without a date is volatile.
- `EvaluationInputValidator.IsCurrent` re-checks a manifest with one stat per path in recording order, one lookup per environment read, and one re-resolution per SDK reference; it returns at the first difference and turns an exception into a miss.
- `EvaluationInputRecordingBenchmark` measures recording, validation with cached and with re-resolved SDK results, stale validation, and shared-context evaluation, on a synthetic project and on real projects named through an environment variable. An opt-in BuildXL Detours comparison checks recorded paths against every path the process touched, content reads reported separately.
- Design note: `documentation/specs/proposed/evaluation-input-recording.md`. Benchmark and comparison usage: `src/MSBuild.Benchmarks/readme.md`.

### Testing

- 62 tests in `EvaluationInputRecording_Tests`: each input kind is recorded; edits and created files invalidate; glob membership; `@(Items->Exists())`; timestamp metadata by every route; links; SDK results; parser configuration; conflicting observations; host file systems and caches; concurrent and shared-context evaluations; in-memory projects; and evaluation results with recording on equal those with it off, metadata, imports, and targets included. Two `FileMatcher` tests reuse a cached expansion over a file system that throws on every call and check the directories reported, a missing glob root included.
- Engine unit tests: no new failures on net11.0; net472 builds clean.
- Detours comparison on Windows x64 against the bootstrap .NET 11 SDK, one default outer and one inner-TFM evaluation per project. A touched path counts as explained when it was recorded, or when it was only probed or enumerated under a recorded directory; a content read under a recorded directory does not. Every path the sandbox saw that the recorder did not hold, reads included, was an SDK resolver input (validated by re-resolution), an installed SDK or reference assembly directory probed by `ToolLocationHelper`, or a runtime file:

| Corpus | Evaluations | Recorded paths | Touched paths | Content reads not recorded | Unexplained |
| --- | ---: | ---: | ---: | ---: | ---: |
| OrchardCore, 232 projects | 464, all cacheable | 76,706 | 182,762 | 26,569 | 0 |
| dotnet/msbuild, 39 projects, `OfficialBuildId` set | 78, all cacheable | 16,117 | 35,831 | 5,570 | 0 |
| Roslyn, Microsoft.CodeAnalysis.Workspaces, `OfficialBuildId` set | 2, all cacheable | 1,086 | 1,595 | 142 | 0 |

- Benchmarks, in-process default job, Windows 11, Ryzen 7 5700X3D, .NET 11 RC1, `DOTNET_GCgen0size=0x2000000` so the runtime's gen0 budget does not drift between benchmarks (with the default GC the recording ratio scatters by about 20% on this machine). Each evaluation uses a fresh `ProjectCollection`, so it reads every file. "Validate, unchanged" compares SDK results against the recorded ones, the cost every project after the first pays in a build submission; "resolving SDKs again" clears the submission cache first, the cost the first project pays:

| Project | Fresh evaluation | With recording | Validate, unchanged | Validate, resolving SDKs again | Stale project file | Stale import | Stale glob directory |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| OrchardCore.Cms.Web, 168 paths, 7 SDK results | 46.2 ms, 9.53 MB | 49.2 ms (1.07x), +27 KB | 1.17 ms (2.5%) | 8.83 ms | 22 µs | 103 µs | 1.18 ms |
| OrchardCore, 90 paths, 2 SDK results | 27.5 ms, 6.29 MB | 28.4 ms (1.03x), +11 KB | 0.81 ms (2.9%) | 6.81 ms | 21 µs | 89 µs | 83 µs |
| Roslyn Workspaces, 161 paths, 3 SDK results | 52.2 ms, 12.45 MB | 57.0 ms (1.09x), +22 KB | 2.86 ms (5.5%) | 9.12 ms | 18 µs | 72 µs | n.a. |
| Synthetic, 79 paths | 19.1 ms, 0.81 MB | 22.5 ms (1.18x), +29 KB | 4.19 ms | 4.13 ms | 94 µs | 164 µs | 2.57 ms |

  Over three pinned runs the recording ratio on the real projects was 1.02x to 1.09x, mean 1.05x: one stat per recorded path. Validation checks paths in recording order, so a changed project file or import is found within the first few stats. Resolving the SDKs again costs 7 to 9 ms per submission whatever the number of references, most of it in the .NET SDK resolver reading workload manifests.

  In a shared `EvaluationContext`, which `ProjectGraph` uses (build-time evaluation is isolated per project), the recording copy reuses the expansions the context has cached and replays the directories they depend on, so recording costs the same stats as in an isolated context: OrchardCore.Cms.Web 31.2 ms fresh, 33.5 ms recording (1.08x); OrchardCore 18.3 ms, 19.6 ms (1.07x); Roslyn Workspaces 38.2 ms, 41.9 ms (1.10x); the synthetic project, which is nothing but globs, 2.8 ms, 7.4 ms, the stat of its 79 paths.

  A warm no-op build of the OrchardCore solution measured +3.4% with recording on, with a run-to-run deviation of 13%, so the end-to-end number is not distinguishable from zero here.

### Notes

- Arcade repositories are non-cacheable in developer builds because Arcade derives `_BuildNumber` from `[System.DateTime]::UtcNow`; the dotnet/msbuild and Roslyn numbers above set `OfficialBuildId`.
- Timestamps and lengths detect changes, not content hashes; symbolic links, host file systems, and host directory caches make an evaluation non-cacheable; watcher and journal based invalidation are described in the design note and not implemented.
- Follow-ups: a PerfStar run with recording on for the end-to-end number; answering a probe from the recorder's own stat instead of a second system call; a report of every non-cacheable reason once a consumer exists; Linux and macOS coverage through CI.
