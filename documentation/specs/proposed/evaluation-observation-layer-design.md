# Evaluation observation layer design

Status: proposed

Prototype: [dotnet/msbuild#14689](https://github.com/dotnet/msbuild/pull/14689)

Detailed reference:
[Evaluation observation layer technical reference](evaluation-observation-layer-design-details.md)

## Purpose

An evaluation cache can reuse an evaluated project only when MSBuild knows every input
that affected the result.

The observation layer records those inputs while evaluation consumes them. It is not
itself a cache.

Every input must be:

1. part of the candidate key;
2. recorded as an observed dependency;
3. covered by an authoritative provider generation; or
4. classified non-cacheable.

Unknown or incomplete observation fails closed.

## Proposal at a glance

- Capture file-based root sources while they are acquired, then create one isolated
  `EvaluationObservationSession` in `Evaluator`.
- Pass or scope the session only across the active evaluation.
- Reuse existing evaluator-native interception points.
- Keep per-evaluation state off shared `EvaluationContext` and out of process-global
  ownership; scoped current-observer access is transport only.
- Use default-deny category coverage.
- Treat opaque third-party code and unclassified property functions as non-cacheable.
- Validate dependencies on lookup in the first in-memory cache.
- Add watchers and journals later only to avoid repeated validation work.
- Use Detours only to verify coverage on Windows.

## First milestone

The current prototype milestone remains observation-only:

- load-time root source hashing/stamping and failure capture followed by one isolated
  session per evaluation in `Evaluator`;
- typed request, source, filesystem, environment, Registry, property-function, SDK
  boundary, toolset, task-registration, side-effect, and failure records;
- explicit incomplete, unsupported, and non-cacheable reasons;
- semantic and concurrency tests plus BuildXL-differential and overhead benchmark
  harnesses;
- no report-level dependency validator, cache admission, or cache hits.

The value of this milestone is proving the observation boundary: it shows that MSBuild
can collect isolated, typed dependencies without changing evaluation. Cache behavior is
deliberately deferred until the team trusts the coverage and cost model.

It remains off by default behind
`MSBUILDPROTOTYPEEVALUATIONOBSERVATION=1`.

The current report does not promise that every recorded category is ready for cache
admission. In particular, SDK request/result observation stops at the resolver boundary:
resolver discovery and resolver-internal file, environment, Registry, network, and host
dependencies remain outside the prototype contract.

## Existing MSBuild code to reuse

| Existing mechanism | Reuse |
| --- | --- |
| `EvaluationContext` and `ContextWithFileSystem` | Reuse the current filesystem and caches while installing a per-evaluation outer recorder. |
| Internal `IFileSystem` | File reads, probes, raw enumeration, and metadata. |
| `DirectoryCacheFileSystemWrapper` | Preserve host cache behavior; record the value returned by the wrapper. |
| `FileMatcher` | Record semantic glob requests and returned membership, including expansion-cache hits. |
| `ProjectRootElementCache` and `ProjectRootElement.Version` | Root/import and unsaved source identity and versions. |
| `PropertyTrackingEvaluatorDataWrapper` | Present environment-derived property reads, independently from logging. |
| `PropertiesUseTracker` | Undefined property reads that may become environment-derived properties later. |
| `Expander` and `Expander.Function` | Property-function classification and observation. |
| `PropertyExpander.ExpandRegistryValue` | Classic `$(Registry:...)` access. |
| `IntrinsicFunctions.GetRegistryValue*` | `[MSBuild]::GetRegistryValue*` access. |
| `ISdkResolverService`, `SdkReference`, `SdkResult` | SDK request, final result, and cache hit/miss. |
| `BuildParameters` and `ProjectCollection.EnvironmentProperties` | Effective environment sources already consumed by evaluation. |
| Existing Detours reporting | Windows-only coverage comparison, not production semantics. |

No second public filesystem abstraction is proposed.

## Session ownership and transport

File-based `Project` and `ProjectInstance` root acquisition computes a raw-content hash
and publishes source metadata on the resulting `ProjectRootElement`. A temporary capture
retains hash, encoding, timestamp, parse outcome, and failure details when root loading
fails.

For successful evaluation, `Evaluator` creates the session after the
`ProjectRootElement` exists. It passes the session to stateful evaluator wrappers.
`EvaluationObservationSession.Enter` also exposes narrowly scoped thread-static and
Framework observer access for existing static and extension seams only while evaluation
is active.

The session is not stored on:

- shared or user-supplied `EvaluationContext`;
- `ProjectCollection`;
- a process-global singleton;
- production `AsyncLocal` state.

Observation completion is atomic. Observation failures never change evaluation behavior,
but they set `ObservationIncomplete`, which prevents reuse.

Repeated observations of the same identity must agree. Different values, outcomes, or
provider generations set `ConflictingObservation` and make the evaluation non-cacheable.

## Coverage model

Use a closed `EvaluationInputCategory` enum.

Static implementation coverage:

```text
NotImplemented   // default
Partial
Complete
```

Per-evaluation state:

```text
NotExercised
Observed
Incomplete
Unsupported
```

The full category enum is required after explicit platform applicability is applied.
Adding a category fails a coverage test until it is classified.

Cache eligibility requires:

- successful evaluation;
- every applicable implementation category `Complete`;
- no per-report `Incomplete` or `Unsupported` state;
- no typed non-cacheable reason;
- an accepted dependency manifest or authoritative validity token for every SDK
  resolution;
- no dropped or conflicting observation.

The current prototype records no resolver dependency contract, keeps every
non-completion implementation category `Partial`, and implements no eligibility or
admission path. `SdkResolutionWithoutCacheLifetime` only reports that the existing SDK
cache was disabled; its absence does not make an SDK-bearing report eligible.

## Input ownership

| Class | Category | Primary observer |
| --- | --- | --- |
| Key | Project/provider identity, global properties | Evaluation entry point |
| Key | ToolsVersion, load settings, evaluation stage, interactive/VS mode | Evaluation entry point |
| Key | Culture, startup directory, node count, semantic feature identity | Evaluation entry point |
| Observed | Root and imported XML | Source/PRE provider |
| Observed | Non-PRE file reads, probes, metadata, raw enumeration | Per-evaluation filesystem |
| Observed | Upward/fallback searches | Search helper |
| Observed | Globs | `FileMatcher` semantic boundary |
| Observed | Imported environment properties | Property tracking |
| Observed | Live `System.Environment` calls | Property-function boundary |
| Observed | Registry | Classic Registry expansion, Registry intrinsics, typed built-in provider |
| Observed | SDK/toolset | SDK resolver services and toolset provider |
| Observed | Stable machine/process values | Property-function/host observer |
| Observed | Unsaved IDE/object-model state | Host source provider |
| Non-cacheable | Opaque extensions, unclassified functions, unstable ambient input | Owning invocation boundary |
| Non-cacheable | Unversioned shared-cache result | Shared-cache boundary |
| Non-cacheable | Partial, failed, ambiguous, or unverifiable observation | Operation boundary |

Solution parsing (`.sln`, `.slnx`, `.slnf`) uses a separate key/report and feeds project
evaluation requests into this model.

## Filesystem strategy

`IFileSystem` is the primary seam, but it is not proof of complete coverage.

Every evaluation-affecting direct use of `FileSystems.Default`, `System.IO`, a
`*NoThrow` helper, or a process-wide cache must be:

1. routed through the per-evaluation filesystem;
2. observed explicitly at its semantic boundary; or
3. made non-cacheable.

Important semantic observers:

- source acquisition owns root/import XML identity;
- `FileMatcher` owns glob membership, including expansion-cache hits;
- search helpers own ordered upward/fallback probes;
- `Expander.Function` owns filesystem property-function classification.

Missing paths and missing nearer search candidates are dependencies.

Glob records retain a membership fingerprint and invalidation index data. Full member
lists are diagnostic-only.

## Environment strategy

Environment observation has several levels.

### Imported environment-derived properties

- A present `$(NAME)` read records the exact imported value.
- An undefined property read records a negative imported-environment dependency.
- Validation compares against the next effective imported environment-property table.
- If another property source overwrote the environment value before the read, it is not
  attributed to the original environment.
- Observation tracking is independent from existing environment-read log emission.

### Live `System.Environment`

| Operation | Policy |
| --- | --- |
| `GetEnvironmentVariable(name)` | Record name and exact returned value/missing. |
| `GetEnvironmentVariables()` | Record the exact returned environment snapshot. |
| `ExpandEnvironmentVariables(text)` | Non-cacheable until expansion executes against an immutable observed provider. |
| `CurrentDirectory` | Record the exact live value and repeat the same read during hit validation. |
| Stable properties such as `ProcessorCount` | Record as typed ambient values if policy allows. |
| Time, random, tick count, and similar unstable values | Non-cacheable. |

### Engine and SDK inputs

Engine-owned environment reads move behind request/provider snapshots or named providers.
SDK-injected environment values record name, value, and later reads. Resolver internals
are opaque. Existing SDK-cache identity is recorded, but it is not a resolver dependency
contract.

Opaque custom property-function code is non-cacheable. A full environment snapshot is not
sufficient because such code may also read files, Registry, network, or private process
state.

There is no portable notification for arbitrary process environment mutation. Known
engine mutations bump an environment generation; a generation mismatch makes the report
non-cacheable.

Raw environment values remain internal and must not appear in logs, binlogs, telemetry,
or diagnostic reports.

## Registry strategy

Two separate paths are observed:

- `$(Registry:...)` in `PropertyExpander.ExpandRegistryValue`;
- `[MSBuild]::GetRegistryValue*` through `Expander.Function` and
  `IntrinsicFunctions`.

Records contain the exact request, views/default where applicable, returned typed value
or string, and failure outcome.

Current APIs do not always distinguish missing key from missing value or a default value
equal to stored data. The observer records only what was authoritatively consumed until a
typed Registry provider is introduced.

Built-in Registry enumeration moves behind that provider. Opaque extension Registry
access is non-cacheable.

Registry notifications may later accelerate Windows invalidation; validation remains the
correctness mechanism.

## SDK and shared-cache strategy

An SDK observer records:

- complete SDK reference and project/solution context;
- success/failure;
- resolved paths/version/properties/items;
- cache hit/miss.

Resolver discovery, manifests, files, and internal dependencies are opaque. The SDK
cache owner, scope, epoch, key, and entry identity show whether the same cached result is
still live; they do not show whether the resolver's underlying dependencies changed.

Until resolvers expose either a complete dependency manifest or an authoritative
generation/token with defined scope, lifetime, and invalidation semantics, any
correctness-capable evaluation cache, including a process-local MSBuild Server cache,
must reject SDK-bearing evaluations.

A measurement-only experiment may bind a candidate to the exact SDK cache entry while
that entry remains current. Normal build entries are submission- or node-build-scoped,
but a retained `EvaluationContext` can keep its own SDK entry current across independent
evaluations. Entry currentness is never sufficient for correctness-capable admission:
the policy still rejects a cross-build Server candidate without the resolver contract.
Such shared-context SDK benchmarks measure an invalidation-disabled upper bound, not
submission-cache behavior or cache correctness. The normative contract is defined in
[SDK boundary and future dependency contract](evaluation-observation-layer-design-details.md#sdk-boundary-and-future-dependency-contract).

An inner shared cache can skip work that an outer observer would otherwise see.

Every shared cache must:

1. replay the original dependency set;
2. expose an authoritative generation; or
3. make the evaluation non-cacheable.

This applies to filesystem, glob, PRE/loaded-project, toolset, host, and SDK-result
caches. SDK cache-entry identity remains useful evidence, but is not an exception to the
dependency-contract requirement.

Any sharing policy or process-global cache that reuses one of those caches remains
non-cacheable until that cache satisfies this contract.

## Validation and invalidation

The first in-memory cache validates candidate dependencies on lookup.

Examples:

- compare source/provider versions;
- hash or version file content;
- repeat typed probes and Registry operations;
- compare glob/search generations or membership;
- compare the next effective imported environment table;
- repeat named live environment reads;
- compare canonical full-environment snapshots;
- repeat recorded ambient reads such as live current directory;
- validate the resolver-provided dependency manifest or validity token; without that
  contract, reject the SDK-bearing candidate. Exact-entry checks are measurement-only,
  limited to the current owner-defined lifetime, and never an admission alternative;
- replay toolset dependencies.

The cached evaluation baseline is immutable. Each build receives a deep copy or
copy-on-write execution overlay.

Validation uses:

- a complete manifest check before materialization;
- provider epochs where available;
- a second complete check after materialization for dependencies without a stable epoch.

Any mismatch discards the hit. A dependency that cannot be rechecked or fenced is
non-cacheable.

Watchers, journals, and Registry/host notifications are later accelerators. Overflow,
event loss, or unsupported roots fall back to validation.

## Overhead

### Disabled

- No session, recorder, collections, or report allocations.
- Predictable null/feature checks at evaluation entry and instrumented static hot paths.
- No logging or semantic changes.

### Enabled

| Area | Main cost |
| --- | --- |
| Property/environment reads | Name lookup and deduplicated record update |
| Filesystem probes/metadata | Session gate, normalization, keyed update |
| File content | Hashing when no authoritative provider token exists |
| Globs/enumeration | Membership fingerprinting and optional diagnostics |
| Property functions | Classification and typed record/non-cacheable reason |
| Registry | Copy request and returned value |
| SDK/toolset | SDK request/result/hit plus toolset provenance |
| Completion | Freeze records and calculate counters |

Primary risks:

- property and property-function hot-path overhead;
- glob membership memory;
- duplicate content hashing;
- shared-cache dependency retention;
- observer lock contention;
- validation cost erasing cache benefit;
- sensitive value retention.

In practice, a small project that reads a few properties should add only keyed record
updates. A glob-heavy project can retain or hash thousands of paths, so glob membership
and validation are expected to dominate enabled-mode cost.

Implementation guidance:

- prefer ordinary dictionaries under a session gate unless profiling requires otherwise;
  the prototype deliberately uses concurrent dictionaries plus a gate until concurrency
  measurements justify simplifying it;
- use existing MSBuild comparers;
- keep hashing outside locks;
- store fingerprints rather than diagnostic member lists;
- avoid LINQ and closures in hot paths;
- share immutable request/provider snapshots.

## Measurement gate

Measure:

- observation disabled and enabled;
- cache miss;
- valid hit including validation and materialization;
- stale candidate followed by reevaluation.

Workloads:

- small SDK project;
- property-function-heavy project;
- glob-heavy project;
- large real solution;
- concurrent graph evaluation;
- repeated Server requests.

Metrics:

- wall-clock and CPU evaluation time;
- allocations, GC, and peak retained memory;
- lock contention;
- retained bytes by category;
- finalization, validation, and materialization time.

The team must approve CPU, allocation, and retained-memory budgets before default
enablement. Cache-hit validation and materialization must remain materially cheaper than
reevaluation.

## Verification

- Observation on/off produces identical evaluation results, errors, and log/binlog
  sequence.
- Coverage tests exercise every category and every known bypass.
- New categories default to incomplete.
- Conflicting repeated reads become non-cacheable.
- Shared-context concurrent evaluations remain isolated.
- Windows Detours compares process-level access with native observations where
  attribution is possible.

Detours is not the production architecture because it is Windows-specific, process-wide,
and cannot represent semantic inputs such as in-memory project versions.

## Phases

1. **Current observation prototype:** load-time source hashing/stamping and failure
   capture, an isolated evaluator session, typed records across the implemented taxonomy,
   partial coverage, and no hits.
2. **Observation completeness/contracts:** close remaining provider and shared-cache
   provenance gaps, add resolver contracts and host versions, and promote only proven
   categories to complete.
3. **Eligibility/performance:** encode required contract state, derive admission, and
   accept overhead on representative workloads.
4. **Decision-grade filesystem validation spike:** compare timestamp-only filesystem
   checks with reevaluation and verify tracked-mutation stale fallback without enabling
   cache admission or reuse.
5. **In-memory Server cache and non-filesystem validation:** implement the key, baseline,
   validators, admission gate, execution copy, and eviction; admit only categories whose
   Phase 3 requirements are satisfied, and keep disk-backed hits experimental until a
   production filesystem validator exists.
6. **Production filesystem validation/invalidation:** shared change service, fallback
   validation, reverse index, watchers/journals, and overflow recovery.
7. **Persistence:** serialization, versioning, and security.

## Decisions for the meeting

1. Confirm the process-local MSBuild Server as the first production target.
2. Confirm that SDK-bearing entries require the resolver dependency contract even for
   process-local correctness-capable reuse.
3. Confirm opaque third-party code and unclassified property functions are
   non-cacheable.
4. Approve the phase ordering.
5. Select benchmark workloads and the process for setting overhead budgets.
6. Decide when a redacted diagnostic report sink is needed.

Accepting decision 2 means normal SDK-style .NET projects remain measurement workloads,
not correctness-capable cache candidates, until the resolver contract exists.

## Compatibility

Observation-only remains internal, opt-in, and silent, so no ChangeWave is required.

Serving cache hits is a behavioral change. It must initially be opt-in behind a
reversible feature gate or ChangeWave, with opt-out, logging, resolver, mutation
isolation, and fallback tests.

No new warnings are proposed.
