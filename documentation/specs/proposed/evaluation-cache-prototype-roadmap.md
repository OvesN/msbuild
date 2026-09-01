# In-memory evaluation cache prototype roadmap

Status: proposed  
Target: partial invalidation prototype by October 20

Related:

- [Native evaluation observation report](evaluation-native-observation-report.md)
- [Native evaluation observation inventory](evaluation-native-observation-inventory.md)
- [PerfStar](perfStar.md)

## Scope

The prototype is an in-memory cache owned by one long-lived MSBuild server process.
It is not persistent or shared across processes, users, or machines.

The initial supported scenario is a CLI build of a disk-backed project. Unsaved project
XML, host-owned in-memory sources, IDE/project-system evaluation, and object-model
remoting are out of scope. SDK-bearing projects remain outside correctness-capable cache
admission until their resolvers provide dependency manifests or authoritative validity
tokens. They may still be used in performance experiments that explicitly identify
whether they stay within an existing SDK entry lifetime or bypass SDK admission to
measure an invalidation-disabled upper bound.

Consequently, normal SDK-style .NET projects are measurement workloads rather than
correctness-capable cache candidates until the resolver contract exists.

The work is split into three stages:

1. measure the maximum benefit with invalidation disabled;
2. add non-filesystem invalidation;
3. add filesystem invalidation with BuildXL.

Before stage 2, a separate decision-grade spike may measure timestamp-only filesystem
validation against full reevaluation. It keeps admission and reuse disabled and does not
change the production correctness sequence.

The October milestone is a partial invalidation prototype, not yet a correctness-complete
cache for normal disk-backed projects.

## Milestone 1: next sprint

### Observation layer

The observation layer is a prerequisite for cache invalidation. It records the inputs
used to create a cached `ProjectInstance`; invalidation later uses those records to mark
the result stale when an input changes.

- Add interceptors in evaluation code that record every consumed input whose change can
  change the evaluated `ProjectInstance`: project/import files, other file reads and
  probes, globs, environment variables, Registry values, property-function results, SDK
  requests, and SDK results.
- At the SDK boundary, this records the complete request and returned result, not
  resolver-internal dependencies; those require the resolver contract described below.
- For file-content reads, store the normalized path and SHA-256 of the exact bytes consumed
  by evaluation; for text-only APIs, hash the exact returned text encoded as UTF-8. Hash
  the buffer already returned to evaluation; do not read the file again.
- Do not support non-disk/IDE sources, custom or unrestricted property functions,
  volatile/side-effecting operations, or unverifiable custom providers; mark them
  ineligible. Keep observation disabled by default.

### Cached evaluation result

- Add an in-memory cache shell.
- Prototype an immutable cached `ProjectInstance` baseline.
- Measure direct sharing for read-only consumers.
- Measure private mutable materialization or `DeepCopy` for build execution.
- Verify that one build cannot mutate state observed by another build.
- Test concurrent requests for the same project.

### Shared evaluation context

Keep a shared `EvaluationContext` on the server so cache misses can reuse existing
file-probe and glob-expansion caches. Do not reuse its SDK-resolution cache across build
requests until the resolver dependency contract exists.

The current sharing policies do not separate those caches: `Shared` also retains the
context-owned SDK resolver service, while `SharedSDKCache` retains only that service.
Before correctness-capable Server reuse, add internal cache composition that gives each
build request a fresh SDK resolver service, or explicitly clear and epoch-advance the
context-owned service between requests. Until then, a server-held shared context is
benchmark-only. A retained SDK entry can remain current across evaluations, but that
currentness is not admission evidence.

Once invalidation exists, tie the supported shared caches to the same epochs as the
evaluation-result cache.

### No-invalidation benchmark

Use an explicit benchmark-only mode that returns the cached result without validation.
This measures the maximum possible cache benefit; it is not a correctness claim.

Add PerfStar measurements for:

- normal evaluation;
- observation only;
- cache miss plus insertion;
- cache lookup;
- direct cached-result reuse;
- mutable result materialization or copy;
- allocations and retained cache memory.

## Milestone 2: through October 20

Implement invalidation mechanisms that do not require the shared filesystem change
service.

### Lookup-key inputs

- normalized project identity;
- complete global properties;
- requested/default `ToolsVersion` and explicitness;
- load settings and other result-affecting request flags;
- culture and UI culture;
- evaluation feature, trait, and ChangeWave state;
- fingerprint of imported MSBuild environment properties.

Changing one of these values selects a different cache entry; it does not invalidate an
existing entry.

### Environment variables

Record every environment access used by evaluation:

- imported environment properties read as `$(NAME)`, including missing values;
- result-affecting environment variables read by MSBuild code;
- property functions `Environment.GetEnvironmentVariable`,
  `Environment.GetEnvironmentVariables`, and `Environment.ExpandEnvironmentVariables`.

Store the exact returned value, missing result, complete snapshot, or expanded result.
Compare it with the next build request's environment snapshot.

### Registry

- Record exact existing and missing Registry reads.
- Arm `RegNotifyChangeKeyValue` when admitting an entry.
- Close the read-to-registration race by confirming the value once after registration.
- A notification marks every dependent entry stale.

### SDK resolution

- Treat SDK resolver internals as opaque.
- Match the complete SDK request.
- Record and validate the exact SDK-result-cache owner, scope, epoch, key, and entry
  identity for controlled process-local experiments.
- Do not treat a live cache entry as resolver dependency validation.
- Require each resolver to provide either a complete dependency manifest or an
  authoritative validity token/generation with defined scope, lifetime, and invalidation
  semantics before correctness-capable admission.
- Until that contract exists, any correctness-capable evaluation cache, including the
  process-local MSBuild Server prototype, rejects SDK-bearing candidates.
- Use the exact SDK entry in a measurement only while its owner reports that entry
  current. Normal main-node build entries are submission-scoped and cleared at
  submission completion; worker-node entries are node-build-scoped. A retained
  `EvaluationContext` can keep its own entry current across evaluations.
- Treat currentness as necessary only for an exact-entry measurement, never as
  correctness-capable admission evidence. Label any shared-context or otherwise
  cross-build SDK benchmark as an invalidation-disabled upper-bound measurement, not
  submission-cache behavior or cache-correctness evidence.

The normative resolver contract is defined in
[SDK boundary and future dependency contract](evaluation-observation-layer-design-details.md#sdk-boundary-and-future-dependency-contract).

### Toolset and server state

- Treat the toolset provider and process-stable values as server/cache-scope state.
- Put requested `ToolsVersion` in the lookup key.
- Clear the evaluation cache or advance a cache-wide epoch if toolsets are reconfigured.
- Do not add a per-hit toolset fingerprint unless dynamic toolsets require it.

### October deliverable

- non-filesystem invalidation working end to end;
- stale-hit fallback to normal evaluation;
- bounded cache and eviction;
- hit, miss, stale, and ineligible diagnostics;
- PerfStar measurements for partially validated hits;
- report showing how many real evaluations remain blocked by filesystem observations,
  SDK resolver contracts, and other non-filesystem reasons.

Until filesystem invalidation exists, cache hits for ordinary disk-backed projects remain
experimental and must not be described as correctness-complete.

## Milestone 3: after October 20

Work with BuildXL on one shared filesystem change service and reverse dependency index.
It must cover:

- root project and imported file contents;
- positive and negative file/directory probes;
- import fallback and upward searches;
- directory enumeration and glob membership;
- filesystem metadata;
- symlinks and reparse points;
- accessibility and permission results;
- watcher overflow, journal gaps, root loss, and generation changes;
- races between validation and result reuse.

Root/import files are part of this phase. Handling them earlier would require rereading
or rehashing them on every hit, which is the overhead this design is intended to avoid.

The filesystem milestone turns the partial prototype into the first
correctness-capable cache prototype only for projects whose non-filesystem dependencies
also satisfy their contracts. SDK-bearing projects remain excluded until the resolver
dependency contract above is implemented.

## Decision metrics

Measure these costs separately:

- observation;
- lookup;
- insertion;
- category validation;
- cached-result materialization;
- validated hit;
- stale hit plus reevaluation;
- cache memory and eviction;
- hit and ineligible rates.

The final objective is:

```text
lookup + validation + result materialization < fresh evaluation
```

The prototype should optimize only after measurements identify which part prevents that
inequality from holding.
