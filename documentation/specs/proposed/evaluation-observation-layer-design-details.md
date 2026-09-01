# Evaluation observation layer technical reference

Status: proposed

Prototype: [dotnet/msbuild#14689](https://github.com/dotnet/msbuild/pull/14689)

Meeting document: [Evaluation observation layer design](evaluation-observation-layer-design.md)

## Decision summary

An evaluation cache may reuse an evaluated project only when every result-affecting input
is:

1. part of the candidate key;
2. recorded as an observed dependency;
3. covered by an authoritative provider generation; or
4. classified non-cacheable.

The observation layer records inputs while evaluation consumes them. It does not infer
dependencies afterward from the evaluated `ProjectInstance`.

The production design uses:

- one isolated `EvaluationObservationSession` per evaluation, with explicit transport
  where practical and narrowly scoped current-observer access for existing static and
  extension seams;
- evaluator-native semantic interception;
- existing MSBuild filesystem, property, property-function, source, SDK, and host seams;
- default-deny coverage states;
- non-cacheable fallback for opaque managed extension code;
- validation on cache lookup as the first correctness mechanism;
- watchers and journals only as later invalidation accelerators;
- Detours only as a Windows verification oracle.

The current prototype remains off by default, instrumentation-only, and unable to serve
cache hits.

## Design meeting guide

The rest of this document is a technical reference. For the design meeting, review these
three groups separately.

### Proposed commitments for approval

- Observation is evaluator-native and fail closed.
- Per-evaluation state is owned by the session and is not stored on shared context or by
  a process-global owner; scoped current-observer access is transport only.
- Coverage is default deny.
- Opaque third-party code and unclassified property functions are non-cacheable.
- Shared caches must replay dependencies, expose an authoritative generation, or make
  the evaluation non-cacheable.
- The first cache target is process-local MSBuild Server reuse.
- Watchers and journals accelerate invalidation but do not define correctness.

### Phase 1 commitments

Phase 1 must:

- keep observation off by default;
- preserve evaluation and logging behavior;
- hash and stamp file-based root sources while they are acquired, retain failure
  metadata when loading fails, and produce one isolated report per evaluation from the
  session created in `Evaluator`;
- record typed observations across the implemented request, source, filesystem,
  environment, Registry, property-function, SDK-boundary, toolset, registration,
  side-effect, and failure seams;
- keep every non-completion implementation category `Partial`;
- measure disabled and enabled overhead;
- keep cache hits disabled.

Phase 1 must not add:

- cache lookup;
- persistence;
- watcher/journal code;
- a public extension API;
- environment or Registry interception hidden behind process-global state that outlives
  the active evaluation.

### Implemented seams and proposed contracts

The detailed sections below define the target contract. The current-status and
implementation-phase sections distinguish the observation seams already present from
provider contracts, validation, admission, invalidation, and reuse that remain proposed.

### Deferred designs

Separate proposals are required for:

- a public extension dependency-reporting contract;
- persistent-cache security and serialization;
- cross-machine reuse;
- exact platform event-backend selection.

### Decisions required now

1. Confirm the process-local MSBuild Server as the first production target.
2. Confirm that SDK-bearing entries require the resolver dependency contract even for
   process-local correctness-capable reuse.
3. Approve default non-cacheable behavior for opaque code.
4. Approve the phase ordering.
5. Select benchmark workloads and the process for setting CPU/allocation/memory budgets.

Accepting decision 2 means normal SDK-style .NET projects remain measurement workloads,
not correctness-capable cache candidates, until the resolver contract exists.

## Scope

This document defines the target observation contract. It does not require implementing
all categories in one pull request.

### First milestone

The current prototype milestone is limited to:

- load-time source hashing/stamping and failure capture followed by one per-evaluation
  session in `Evaluator`;
- typed request, source, filesystem, environment, Registry, property-function,
  SDK-boundary, toolset, registration, side-effect, and failure records;
- explicit coverage and non-cacheable reasons;
- semantic and concurrency tests plus BuildXL-differential and overhead benchmark
  harnesses.

The prototype has no eligibility computation, cache admission, or cache-hit path. Every
non-completion implementation category remains `Partial`.

### Later milestones

Later pull requests add:

- remaining shared-cache, provider, toolset, host, and SDK resolver dependency contracts;
- coverage evidence required to promote supported categories from `Partial` to
  `Complete`;
- validation and an in-memory MSBuild Server cache;
- non-filesystem and filesystem invalidation;
- persistence.

## Non-goals

The initial observation work does not provide:

- cache lookup or materialization;
- serialization or persistence;
- a reverse dependency index;
- filesystem or Registry event monitoring;
- a public dependency-reporting API for third-party extensions;
- transparent sandboxing of arbitrary managed code.

Until a public extension contract is designed, opaque property-function and host
extension code is non-cacheable. SDK resolver request/result data is observed at the
MSBuild boundary, but resolver-internal dependencies require a separate resolver
contract before correctness-capable cache admission.

## Technical reference

## Core data model

### Candidate-key inputs

Inputs known before evaluation select candidate entries:

- project/source-provider identity;
- complete global properties;
- requested and effective ToolsVersion, including explicitness;
- evaluation stage;
- result-affecting `ProjectLoadSettings`;
- interactive and Visual Studio mode;
- startup directory and node count;
- culture and UI culture;
- OS, architecture, path comparison, and evaluation semantic identity;
- relevant Traits, feature switches, and ChangeWaves;
- host/provider identity.

The exact key belongs to the cache design. The observation report carries the request
snapshot used to form it.

### Observed dependencies

Dependencies discovered during evaluation are typed records:

- project source versions;
- file reads;
- path probes;
- glob and directory membership;
- file metadata;
- imported and live environment reads;
- Registry values and enumerations;
- SDK and toolset resolution;
- stable machine/process values;
- host document versions.

### Non-cacheable inputs

Examples:

- an incomplete or failed observation;
- a partial stream read without an authoritative content token;
- a Boolean probe that cannot distinguish failure from missing;
- unclassified property functions;
- opaque extension execution, including SDK resolver internals;
- time, random, network, or arbitrary host callbacks without a stable token;
- shared-cache results without replayable provenance.

## Coverage and eligibility

Define a closed `EvaluationInputCategory` enum matching the normative coverage matrix.
The full enum is required after applying an explicit platform-applicability map. There is
no manually selected subset of "required" categories.

Static implementation coverage has:

```text
ImplementationCoverageState
  NotImplemented   // default
  Partial
  Complete
```

Adding an enum member must fail a coverage test until it is explicitly classified.

A completed report separately records the per-evaluation state:

```text
CategoryObservationState
  NotExercised
  Observed
  Incomplete
  Unsupported
```

Implementation coverage and the per-evaluation state are independent. Future admission
rejects an applicable category whose implementation coverage is not `Complete`; it does
not rewrite an otherwise exact `Observed` operation to `Incomplete`. Platform-specific
`NotExercised` is permitted only when the semantic snapshot proves that category cannot
affect evaluation on that platform.

The report exposes:

- category coverage states;
- typed dependency records;
- non-cacheable reasons;
- dropped/incomplete record counters;
- retained-size counters.

The cache, not the observer, derives eligibility:

```text
eligible =
  evaluation succeeded
  AND every applicable category has implementation coverage Complete
  AND no report category is Incomplete or Unsupported
  AND no non-cacheable reason exists
  AND every SDK resolution has an accepted dependency manifest or authoritative
      validity token
  AND ObservationIncomplete is absent
  AND no observation was dropped or truncated
```

The current prototype records no resolver dependency contract, computes no eligibility,
implements no admission path, and keeps every non-completion implementation category
`Partial`, so the predicate cannot be satisfied. `SdkResolutionWithoutCacheLifetime` is
only an additional blocker for a resolution performed with the existing SDK cache
disabled. Its absence is not resolver dependency provenance. Before production admission
exists, the report or admission schema must represent the accepted contract for each
resolution and fail the predicate when it is missing.

## Session creation and transport

### Ownership

One `EvaluationObservationSession` belongs to one evaluation.

It must not be stored on:

- a shared or user-supplied `EvaluationContext`;
- `ProjectCollection`;
- a process-global observer;
- an `AsyncLocal` used as the production transport.

### Creation

File-based `Project` and `ProjectInstance` root acquisition computes a raw-content hash
and publishes the hash, timestamp, encoding, source kind, and stability metadata on the
resulting `ProjectRootElement`. An `EvaluationProjectSourceLoadCapture` retains the hash
and related metadata only so a failed load or parse can produce a report; it does not
retain the source bytes.

After a `ProjectRootElement` exists, `Evaluator` creates the
`EvaluationObservationSession`. Successful root observation reads the retained
`ProjectRootElement` source stamp. When root loading throws, the entry-point catch uses
the temporary capture to create a minimal failure report because no evaluator session
exists.

During evaluation, the session is passed to or made current for:

- `Evaluator`;
- `PropertyTrackingEvaluatorDataWrapper`;
- `Expander`;
- `Expander.Function`;
- SDK resolver services;
- host/source providers.

Existing static and extension seams use the narrowly scoped current observer only while
the evaluation is active.

### Filesystem composition

The per-evaluation filesystem is installed on an internal context clone through
`EvaluationContext.ContextWithFileSystem`, matching the existing directory-cache
composition pattern. The session itself is not stored on the context.

```text
physical/custom provider
  -> existing EvaluationContext caches
  -> host DirectoryCacheFileSystemWrapper
  -> RecordingFileSystem
  -> evaluator
```

### Completion

Completion is an atomic boundary.

- Evaluation-time observations accepted before completion are frozen into the report.
- Project API calls after evaluation bypass recording.
- Observation exceptions never escape into evaluation.
- Any observation exception, allocation failure, truncation, or dropped evaluation-time
  record sets `ObservationIncomplete`.
- A dropped-record counter must be zero before an entry can be eligible.

### Repeated-observation conflicts

Deduplication never discards a different consumed value.

For the same dependency identity:

- an equivalent repeated result is deduplicated;
- a more authoritative result may replace a weaker result, for example a full content
  hash replacing an unverifiable stream marker;
- a different value, outcome, provider identity, or completion state sets
  `ConflictingObservation` and makes the evaluation non-cacheable;
- diagnostic mode may retain all conflicting results, but cache eligibility never
  selects one arbitrarily.

This rule applies to environment values, file content and metadata, Registry results,
source versions, and shared-provider generations.

## Existing MSBuild mechanisms to reuse

| Existing mechanism | Reuse |
| --- | --- |
| `EvaluationContext` | Reuse its filesystem, `FileMatcher`, and SDK resolver service. Preserve the caller's sharing policy. |
| `ContextWithFileSystem` | Install the per-evaluation outer recorder without changing public API. |
| Internal `IFileSystem` | Record file reads, probes, enumerations, and metadata that pass through this seam. |
| `DirectoryCacheFileSystemWrapper` | Keep host caching semantics and record its returned value from the outer wrapper. |
| `FileMatcher` | Record semantic glob requests and returned membership, including expansion-cache hits. A Framework-layer internal callback carries primitive glob data to a Build-layer session adapter. |
| `ProjectRootElementCache` | Reuse source object identity and versions, but distinguish its different cache lifetimes and reload policies. |
| `ProjectRootElement.Version` and XML change events | Observe in-memory and unsaved project source generations. |
| `PropertyTrackingEvaluatorDataWrapper` | Observe present environment-derived property reads independently from diagnostic logging. |
| `PropertiesUseTracker` | Observe undefined property reads that may become environment-derived properties later. |
| `EnvironmentVariableReadEventArgs` | Preserve existing diagnostics only; do not reconstruct cache records from events. |
| `Expander` and `Expander.Function` | Observe property functions before MSBuild escapes returned strings. |
| `PropertyExpander.ExpandRegistryValue` | Observe classic `$(Registry:...)` syntax. |
| `IntrinsicFunctions.GetRegistryValue*` | Observe `[MSBuild]::GetRegistryValue*` operations. |
| `ISdkResolverService`, `SdkReference`, `SdkResult` | Record inline at the canonical SDK request/result boundary. |
| `BuildParameters` and `ProjectCollection.EnvironmentProperties` | Reuse the effective environment sources evaluations already consume. |
| `BuildEventContext.EvaluationId` | Correlate one report with one evaluation. |
| `FileUtilities` and MSBuild name comparers | Preserve platform and MSBuild comparison semantics. |
| Existing Detours reporting | Compare process-level accesses with evaluator-native records on Windows. |

## Normative coverage matrix

Each row has one primary semantic owner. Lower-level records may support that owner but do
not create a second independently validated dependency for the same consumed value.

| Class | Input category | Primary owner/interceptor | Record or policy |
| --- | --- | --- | --- |
| Key | Project and provider identity | Evaluation entry point | Canonical identity |
| Key | Global properties | Evaluation entry point | Complete property snapshot |
| Key | ToolsVersion, load settings, evaluation stage, modes | Evaluation entry point | Semantic request snapshot |
| Key | Culture, startup directory, node count | Evaluation entry point | Exact values |
| Key | Evaluation implementation semantics | Evaluation entry point | OS/architecture/comparison/features semantic ID |
| Observed | Root project XML | Source acquisition / PRE provider | One source identity plus version or consumed content hash |
| Observed | Imported project XML | Import loader / PRE provider | One source identity plus version or consumed content hash |
| Observed | Non-PRE file content | `RecordingFileSystem` or provider | Full consumed-content identity |
| Observed | File/directory existence | `RecordingFileSystem` or typed bypass | Present/missing/failure |
| Observed | Upward and fallback searches | Search helper | Ordered candidate probes and ordered selected results |
| Observed | Glob membership | `FileMatcher.GetFiles` boundary | Pattern plus returned membership fingerprint |
| Observed | Raw directory enumeration | `RecordingFileSystem` | Request, members, completion |
| Observed | File metadata and link identity | Filesystem/provider | Exact returned fields and provider semantics |
| Observed | Filesystem property functions | `Expander.Function` / well-known path | Route through `IFileSystem`, record typed result, or non-cacheable |
| Observed | Present imported environment property | Property tracking wrapper | Name and exact effective imported value |
| Observed | Missing imported environment property | `PropertiesUseTracker` | Name and missing state |
| Observed | Named live environment read | `Expander.Function` | Name and exact returned value/missing |
| Observed | Full live environment enumeration | `Expander.Function` | Exact returned snapshot |
| Observed | SDK-injected environment property | Evaluator data / SDK result | Name, injected value, and later read |
| Observed | Engine-owned environment input | Owning provider/service | Named value or provider generation |
| Observed | Classic Registry expression | `PropertyExpander.ExpandRegistryValue` | Exact request and returned string/outcome |
| Observed | Registry intrinsic | `Expander.Function` and `IntrinsicFunctions` | Exact request and typed returned value/outcome |
| Observed | Built-in Registry discovery | Typed Registry provider | Value or enumeration result |
| Observed | SDK resolution | SDK resolver services with scoped current-observer access | Request, final result, and cache hit/miss |
| Observed | Toolset discovery | Toolset provider | Selected result and source generation |
| Observed | Stable ambient value | Property-function/host observer | Typed value |
| Observed | Unsaved IDE/object-model source | Host source provider | Document identity and monotonic version |
| Non-cacheable | Opaque extension execution | Property-function/host boundary | `OpaqueManagedCode` |
| Non-cacheable | Unclassified property function | `Expander.Function` | `UnclassifiedPropertyFunction` |
| Non-cacheable | Time, random, network, arbitrary callback | Function/host boundary | Category-specific reason |
| Non-cacheable | Unversioned shared-cache hit | Cache boundary | `UnversionedSharedCache` |
| Non-cacheable | Partial/failed/unverifiable read | Operation boundary | Category-specific reason |

### Solution inputs

Solution and solution-filter parsing are not project evaluation and must not be folded
into a project evaluation entry.

`.sln`, `.slnx`, and `.slnf` processing requires a separate candidate key and observation
report covering:

- solution/filter content;
- selected configurations;
- project membership and mappings;
- generated metaproject inputs.

The resulting project evaluation requests then use this document's project-level
contract.

## Filesystem observation

### Call-site completeness

`IFileSystem` is the primary seam, not proof of complete coverage.

Evaluation-affecting code that currently calls `FileSystems.Default`,
`FileUtilities.*NoThrow`, `System.IO`, or process-wide caches directly must be inventoried.
Each call site must be:

1. routed through the per-evaluation filesystem;
2. observed explicitly at its semantic boundary; or
3. classified non-cacheable.

Coverage remains `Partial` while any relevant bypass exists.

### Property-function I/O

Filesystem property functions require explicit classification.

- `[MSBuild]::FileExists` and `DirectoryExists` should use the evaluation filesystem.
- Allowlisted `System.IO.File` and `System.IO.Directory` functions are observed at
  `Expander.Function`.
- A known function is routed to an equivalent `IFileSystem` operation where semantics
  match.
- A function that cannot be represented without changing behavior is non-cacheable.

### Project source ownership

Root and imported XML produce one source record owned by source acquisition or the PRE
provider.

The generic filesystem recorder must tag or suppress the corresponding lower-level read
so one consumed XML source is not validated twice under different rules.

`ProjectRootElement.Version` is authoritative for an in-memory PRE object. It is not by
itself an authoritative disk generation when a cache has auto-reload disabled.

### Probes and searches

```text
PathProbeObservation
  path
  requested kind: File / Directory / Either
  Present(actual kind) / Missing / Failure
```

Missing and failure are different.

Upward searches such as `GetPathOfFileAbove` and import fallback searches record:

- ordered candidate paths;
- every negative probe that affected selection;
- the ordered selected-path sequence, count, and fingerprint;
- the search semantics.

The selected sequence is retained even when candidate details are omitted because those
paths are direct dependencies. An empty sequence is a miss. Wildcard matches remain in
the sequence when evaluation ignores them as self, circular, or duplicate imports.

A newly created nearer file invalidates the old result.

### Globs

Glob observation belongs at `FileMatcher.GetFiles`, because that boundary knows:

- root;
- include/exclude pattern and `SearchAction`;
- recursion;
- comparison semantics;
- final membership returned to evaluation.

Record the returned membership even when it came from `FileEntryExpansionCache`.

`FileMatcher.Default` and any other matcher not created with the per-evaluation callback
remain explicit bypasses until rerouted or classified non-cacheable.

The semantic record includes:

```text
GlobObservation
  root
  include and exclude expressions
  SearchAction and recursion
  comparison semantics
  membership fingerprint
  Complete / Partial / Failure
```

The cache representation stores:

- a canonical membership fingerprint;
- reverse-index data needed for invalidation.

Full member lists are optional diagnostic data, not a required retained cache payload.

### Raw enumeration

The filesystem recorder still records non-glob directory enumeration:

```text
DirectoryEnumerationObservation
  root, pattern, recursion, entity kind
  membership fingerprint
  Complete / Partial / Failure
```

Partial or failed enumeration is non-cacheable.

### Symlinks, reparse points, and permissions

Provider semantics must state whether identity follows:

- the logical path;
- the link object;
- the resolved target;
- both.

Access/authorization failure is not missing. Until typed outcomes exist, ambiguous
negative probes remain non-cacheable.

## Environment observation

Environment inputs have distinct sources and must be validated against the same source
that evaluation consumed.

### Imported environment-derived properties

`ProjectCollection.EnvironmentProperties` and backend build parameters supply the
environment-derived MSBuild property table.

When observation is enabled:

- property-read tracking runs independently from `Traits.Instance.LogPropertyTracking`;
- event/binlog emission remains controlled by the existing diagnostic trait;
- reading a present environment-derived property records its exact effective imported
  value;
- reading an undefined property through `PropertiesUseTracker` records a negative
  imported-environment dependency for that property name;
- if XML, global, command-line, toolset, or SDK data overwrote the environment-derived
  value before the read, the read is not attributed to the original environment value.

Validation compares against the next evaluation's effective imported environment table,
not a later direct CLR environment read.

### Live `System.Environment` property functions

Observe raw results before MSBuild string escaping.

| Operation | Policy |
| --- | --- |
| `GetEnvironmentVariable(name)` | Record requested name and exact returned value/missing. |
| `GetEnvironmentVariables()` | Record the exact returned snapshot and platform comparison semantics. |
| `ExpandEnvironmentVariables(text)` | Non-cacheable until MSBuild executes equivalent expansion against an immutable observed environment provider. Parsing `%NAME%` approximately is not sufficient. |
| `CurrentDirectory` | Record the exact live value and repeat the same read during hit validation. |
| Stable properties such as `OSVersion` or `ProcessorCount` | Record as typed ambient values if policy allows caching them. |
| `TickCount`, time, random, and similar unstable values | Non-cacheable. |

The overload taking `EnvironmentVariableTarget` records the target as part of the
operation. User and machine targets require platform-specific validation.

### Engine-owned environment inputs

Reads that affect evaluation should move behind an owning provider or request snapshot,
including:

- toolset selection;
- evaluator feature and trait choices;
- node/build environment state;
- evaluator-specific escape hatches.

The record contains the named value or the authoritative provider generation.

### SDK-injected environment variables

`AddSdkResolvedEnvironmentVariable` can introduce values during evaluation.

Record:

- variable name and injected value;
- whether evaluation later read that property;
- conflict with an imported value.

### Opaque extension code

Arbitrary managed property-function or host extension code can read:

- live environment;
- files;
- Registry;
- network;
- private process state.

A whole-environment snapshot does not cover those other inputs and cannot prove the exact
value consumed during concurrent mutation.

Therefore opaque property-function and host extension code is non-cacheable until a
separate dependency-reporting contract exists. SDK resolvers are not exempt: their
result cache proves only entry lifetime, not resolver dependency validity.

Built-in extensions become cacheable only after their ambient reads are routed through
observable providers and covered by tests.

### Environment mutation

There is no portable notification for arbitrary process-environment mutation.

- Engine-owned mutations must bump an internal environment generation.
- Sessions compare the generation at start and completion.
- A generation mismatch sets `ConflictingObservation` and makes the evaluation
  non-cacheable.
- Direct live reads record their returned values.
- Out-of-band mutation by arbitrary in-process code is covered by making that code
  non-cacheable, not by comparing start/end snapshots.

### Confidentiality

Environment values may contain credentials.

Use two projections:

1. an internal value store used only for in-memory validation;
2. a diagnostic projection containing names, counts, and redacted keyed hashes.

Raw values must not be emitted to:

- normal logs;
- binlogs;
- telemetry;
- node IPC diagnostics;
- persisted manifests without a separate security design.

## Registry observation

### Classic syntax

`$(Registry:...)` is intercepted in `PropertyExpander.ExpandRegistryValue`.

Record:

- original key/value request;
- platform behavior;
- exact returned string;
- exception/failure outcome.

Current APIs may not distinguish missing key from missing value. Do not claim that
distinction until a single-operation typed provider supplies it authoritatively.

### `[MSBuild]` intrinsics

`[MSBuild]::GetRegistryValue*` is intercepted at `Expander.Function` and
`IntrinsicFunctions`.

Record before string conversion:

```text
RegistryValueObservation
  hive/key/value request
  ordered Registry views
  default value
  exact typed returned value
  success/failure
```

If the returned default is indistinguishable from a stored value equal to that default,
the observation records only the exact consumed result, not an invented missing state.

### Built-in Registry discovery

Toolset and extension discovery may enumerate keys or values.

Move those reads behind an internal typed Registry provider and record:

```text
RegistryEnumerationObservation
  hive, key, view
  subkeys or value names returned
  Complete / Partial / Failure
```

### Opaque Registry access

Arbitrary extension Registry access is non-cacheable without a dependency contract.

Registry notification may later accelerate Windows invalidation, but validation remains
the correctness mechanism and non-Windows behavior is validated according to the actual
MSBuild semantic returned on that platform.

## SDK and toolset observation

Record inline at the canonical `ISdkResolverService` request/result boundary through the
scoped current observer.

Record:

```text
SdkResolutionObservation
  complete SdkReference
  project/solution context
  interactive and Visual Studio mode
  success/failure
  resolved paths/version
  returned properties/items
  cache hit/miss
```

### SDK boundary and future dependency contract

Resolver discovery, manifests, assemblies, file probes, and internal dependencies are not
observed.

The observer records:

- the complete SDK request record;
- the final `SdkResult`; and
- whether the existing SDK cache returned the result.

The current cache is primarily keyed by SDK name within its existing scope. The observer
records the complete request separately from that narrower effective key; changing the
SDK cache key or its production lifetime policy is separate work.

In-process, custom, and out-of-process resolver internals are treated identically as
opaque implementations behind this cache boundary. The recorded owner, scope, epoch,
key, and entry identity can prove only that the same `SdkResult` cache entry remains
live. It cannot prove that the resolver's files, environment, Registry, network, or
other inputs are unchanged.

A correctness-capable resolver contract must provide either:

1. a complete dependency manifest expressed in the observation taxonomy; or
2. an authoritative validity token/generation with explicit scope, lifetime, and
   invalidation semantics.

Until then, any correctness-capable evaluation cache, including a process-local MSBuild
Server cache, rejects SDK-bearing evaluations.

A measurement-only experiment may bind to the exact live cache entry for its
owner-defined lifetime. Normal main-node build entries are submission-scoped and cleared
at submission completion; worker-node entries are node-build-scoped. A cache owned by a
retained `EvaluationContext` can remain current across independent evaluations because
it is not automatically cleared between them.

Entry currentness is therefore necessary only for an exact-entry measurement and is
never sufficient for correctness-capable admission. The admission policy rejects a
cross-build Server candidate without the resolver contract even if a context-owned entry
still reports current. Such a shared-context benchmark is an invalidation-disabled
upper-bound experiment, not submission-cache behavior or cache-correctness evidence.

## Shared cache provenance

An outer recorder cannot see work skipped by an inner cache.

Each shared cache must provide one of:

1. dependency replay from the operation that populated the entry;
2. an authoritative provider generation;
3. a non-cacheable reason.

This applies to:

- `CachingFileSystemWrapper`;
- file-existence caches;
- `FileMatcher` expansion caches;
- host directory caches;
- loaded-project/PRE caches;
- toolset/configuration caches.

Any sharing policy or process-global cache that reuses one of those caches remains
non-cacheable until that cache satisfies this contract. SDK result caches follow the
same rule; their live-entry identity is correlation and lifetime evidence, not resolver
dependency provenance.

## Validation and invalidation

### First in-memory cache

Validation on lookup is sufficient for correctness:

| Dependency | Validation |
| --- | --- |
| Source version | Compare the same provider's monotonic version/token. |
| File content | Compare provider generation or consumed-content hash. |
| Path probe | Repeat the typed probe. |
| Search | Repeat ordered probes or compare an authoritative search-root generation. |
| Glob/directory membership | Re-enumerate or compare provider directory generation. |
| Metadata/link field | Re-read the same field with the same semantics. |
| Imported environment property | Compare the next effective imported property table. |
| Live named environment read | Repeat the same CLR operation. |
| Full environment enumeration | Compare canonical snapshots. |
| Stable ambient value | Repeat the same ambient read, including live current directory. |
| Registry value/enumeration | Repeat the same operation and compare typed result. |
| SDK | Validate the resolver dependency manifest or authoritative validity token. Without that contract, reject the candidate; an exact-entry check is measurement-only and limited to the entry's current owner-defined lifetime. |
| Toolset | Compare provider generation or replayed dependencies. |
| Host document | Compare host document version. |

### Validate-to-materialize race

The server cache uses:

- an immutable cached evaluation baseline;
- a mutable deep copy or copy-on-write execution overlay per build;
- provider invalidation epochs where the provider supports them;
- a complete manifest check before materialization;
- a second complete check after materialization for dependencies without an authoritative
  epoch or snapshot fence;
- an epoch check before validation and after materialization for versioned providers.

If an epoch changes during validation/materialization, the hit is discarded.

If a non-epoch dependency changes between the two checks, the hit is discarded. A
dependency class that cannot be rechecked or fenced is non-cacheable.

Concurrent requests may share the expensive first validation result per entry, but every
waiter performs the final epoch/manifest check before receiving its materialized project.

### Live invalidation

Watchers, journals, and notifications are optional accelerators.

A reverse index maps dependencies to entries:

```text
path -> entries
search/glob root -> entries
environment property name -> entries
Registry key/value/view -> entries
source document -> entries
resolver dependency or validity-token identity -> entries
toolset generation -> entries
```

Event overflow, loss, unsupported roots, or backend failure invalidates the affected root
set or falls back to validation. Event delivery is never the sole correctness mechanism.

## Overhead model

### Disabled

When observation is disabled:

- no session, recorder, record collections, or report are allocated;
- evaluator entry points perform a predictable null/feature check;
- static property-function and Registry fast paths perform at most one predictable,
  non-allocating observer-null branch.

The disabled path must preserve logging and evaluation semantics exactly.

### Enabled

| Interception | Additional work |
| --- | --- |
| Property/environment read | Name lookup and deduplicated record update |
| Probe/metadata read | Session gate, normalization, keyed update |
| File content | Hash only when no authoritative provider token exists |
| Glob/directory enumeration | Membership fingerprint; optional diagnostic members |
| Property function | Classification plus typed record or non-cacheable reason |
| Registry value | Copy request and typed result |
| SDK/toolset | Record SDK request/result/hit and canonicalize toolset provenance |
| Completion | Freeze records, calculate counters, create diagnostic projection |

### Primary cost risks

1. Property and property-function paths are evaluation hot paths.
2. Large glob memberships can dominate retained memory.
3. Content hashing can duplicate provider work.
4. Shared-cache dependency replay can retain large manifests.
5. Locking can serialize otherwise parallel evaluation work.
6. Validation-on-hit can erase cache benefit if every file and glob is reopened.
7. Raw environment/Registry values increase sensitive retained memory.

### Implementation guidance

- Use ordinary dictionaries under a session gate unless profiling proves concurrent
  mutation is required.
- Deduplicate names and paths with existing MSBuild comparers.
- Store fingerprints rather than full glob membership in cache records.
- Retain full members only in an explicitly enabled diagnostic projection.
- Share immutable provider/request snapshots instead of copying them per project.
- Avoid LINQ and closures in evaluation hot paths.
- Keep hashing outside critical sections.

### Required measurements

Measure:

- observation disabled;
- observation enabled by phase;
- cache miss with complete observation;
- valid hit including validation/materialization;
- stale candidate followed by reevaluation.

Workloads:

- small SDK project;
- property-function-heavy project;
- glob-heavy synthetic project;
- large real solution;
- concurrent graph evaluation;
- repeated MSBuild Server requests.

Metrics:

- wall-clock evaluation time;
- evaluation CPU time;
- allocated bytes;
- GC counts;
- peak retained memory;
- observer lock contention;
- records and retained bytes by category;
- report finalization time;
- validation and materialization time.

No enabled-mode budget is selected by this document. The team must approve CPU,
allocation, and retained-memory thresholds before observation is enabled by default.

### Default-enablement gate

Observation cannot become default-on until:

- disabled-path regressions are below the agreed noise threshold on every acceptance
  workload;
- enabled-mode CPU, allocation, and retained-memory budgets are approved and met;
- no benchmark shows observer lock contention limiting graph parallelism;
- sensitive values are absent from logs, binlogs, telemetry, and diagnostic output;
- all required categories have implementation coverage `Complete`;
- cache-hit validation plus materialization remains materially cheaper than reevaluation.

## Verification

### Semantic tests

Observation on/off must produce identical:

- evaluated properties, items, metadata, and imports;
- errors and exception types;
- event/binlog sequence;
- lazy enumeration behavior;
- results under concurrent shared-context evaluation.

### Coverage tests

Add focused tests for:

- present and missing imported environment properties;
- overwritten environment properties;
- SDK-injected environment properties;
- live environment named read and enumeration;
- SDK request/result and cache-hit recording;
- both Registry syntaxes;
- Registry default-value ambiguity;
- property-function filesystem access;
- direct filesystem bypass inventory;
- glob expansion-cache hits;
- upward/fallback search dependencies;
- PRE reload-enabled and reload-disabled policies;
- SDK same-name/different-version and cached-result behavior;
- observation failure and dropped-record handling.

### Process-level verification

Use Windows Detours to compare process-level file/environment/Registry access with
evaluator-native records where attribution is possible.

Detours does not define production semantics because it is Windows-specific,
process-wide, and cannot represent in-memory source versions or reliably attribute all
accesses to concurrent evaluations.

Portable coverage tests should also assert or detect evaluation-affecting direct
`FileSystems.Default` and unclassified property-function use.

## Implementation phases

### Phase 1: current observation prototype

- Load-time root source hashing/stamping and failure capture, followed by one isolated
  session in `Evaluator`.
- Typed records across the implemented observation taxonomy.
- Atomic completion, default-deny reasons, and all non-completion implementation
  categories `Partial`.
- No eligibility computation, cache admission, or cache-hit path.

### Phase 2: observation completeness and provider contracts

- Close remaining direct-read and shared-cache provenance gaps.
- Add dependency contracts for SDK resolvers and other opaque providers.
- Complete toolset provenance and host document versions.
- Promote a category to `Complete` only after static checks, mutation tests, and
  differential evidence cover its supported boundary.

### Phase 3: performance and eligibility

- Accept disabled/enabled overhead on representative workloads.
- Encode provider and resolver contract state in the report or admission predicate.
- Derive cache eligibility from coverage, reasons, and required contract state.
- Keep cache hits opt-in.

### Phase 4: decision-grade filesystem validation spike

- Measure timestamp-only filesystem validation against full reevaluation.
- Verify tracked mutations reject stale candidates.
- Keep cache admission and result reuse disabled; this measures validation cost only.

### Phase 5: in-memory server cache and non-filesystem validation

- Candidate key and entry store.
- Implement the required validators for every admitted non-filesystem category.
- Admit a candidate only when its coverage, reasons, resolver/provider contracts, and
  validators satisfy the Phase 3 predicate.
- Do not admit disk-backed candidates unless a production filesystem validator exists;
  any earlier disk-backed hit remains experimental, opt-in, and not
  correctness-complete.
- Immutable evaluation baseline and mutable execution copy/overlay.
- Validation epochs, eviction, and memory budgets.

### Phase 6: production filesystem validation and invalidation

- Implement the shared filesystem change service with correctness-preserving fallback
  validation.
- Reverse dependency index.
- Watcher/journal acceleration.
- Overflow recovery.

### Phase 7: persistence

- Serialization/versioning/security design.

## Compatibility

The observation-only feature is internal, opt-in, and produces no user-visible
diagnostics by default. It does not need a ChangeWave while default build behavior and
output remain unchanged.

Serving evaluation-cache hits is a behavioral change. It must initially be opt-in behind
a reversible feature gate or ChangeWave, with tests for:

- enabled behavior;
- `MSBuildDisableFeaturesFromVersion` opt-out behavior;
- evaluation logging and event ordering;
- resolver warnings and side effects;
- object identity and mutation isolation;
- failure fallback to normal evaluation.

New warnings require separate compatibility treatment because warnings can break
`WarnAsError` builds.

## Current prototype status

Draft PR #14689 currently:

- hashes and stamps file-based root sources during acquisition, retains failure metadata,
  then creates one isolated session per evaluation in `Evaluator`;
- records typed request, source, filesystem, environment, Registry,
  property-function, SDK-boundary, toolset, task-registration, side-effect, and failure
  observations;
- remains off by default behind
  `MSBUILDPROTOTYPEEVALUATIONOBSERVATION=1`;
- preserves shared-context concurrency isolation;
- never declares an entry ready for cache hits;
- has no production report sink;
- explicitly flags incomplete, unsupported, conflicting, and unverifiable inputs;
- records SDK cache identity but not resolver-internal dependencies;
- has no report-level dependency validator, invalidation algorithm, cache admission, or
  result reuse; `ISdkResolverCacheValidator` validates SDK cache-entry lifetime only.
