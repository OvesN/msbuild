# Native Evaluation Observation Timing Report

## Scenario

- Benchmark: `OrchardCoreNoOpBuildBenchmark`
- Project: `src/OrchardCore/OrchardCore/OrchardCore.csproj`
- Framework: `net10.0`
- Command: warm external `dotnet build --no-restore`
- Observer overhead across three independent runs: **+3.7%, +3.8%, and +5.2%**
- Aggregate means: 4.910 s -> 5.107 s (**approximately +4.0%, +197 ms**)

## Method

Temporary `Stopwatch.GetTimestamp()` scopes recorded inclusive and exclusive CPU time.
Five no-op builds produced 65 evaluation reports across 30 MSBuild processes. CPU time is
summed across parallel nodes and therefore does not equal wall-clock time.

Timing self-overhead was noisy but bounded. BenchmarkDotNet reported ratio 1.00
(5.016 s vs 5.053 s median). A six-pair spot check had an 84 ms median increase
(about 1.7%) with one large outlier. Treat individual activity values as approximate;
the ranking was stable across five repeated telemetry runs.

### Subtractive Wall-Time Cross-Check

A separate diagnostic build disabled one activity at a time with no timing scopes.
Twenty paired external builds were collected for the three strongest candidates:

| Disabled activity | Median saving | Mean saving | Saving standard deviation | Faster runs |
| --- | ---: | ---: | ---: | ---: |
| Report finalization | 23 ms | 73 ms | 602 ms | 11/20 |
| Filesystem records | 27 ms | 55 ms | 112 ms | 13/20 |
| Source observation | -2 ms | -71 ms | 200 ms | 10/20 |

These marginal wall-time effects are below the external-process/VM noise floor and are
**not** used as attributable savings in this report. They do not contradict the CPU
timings: evaluation runs across several parallel MSBuild processes, while the subtractive
test measures one noisy end-to-end wall clock.

## Exclusive Time

| Activity | ms/evaluation | Calls/evaluation | Share of instrumented CPU |
| --- | ---: | ---: | ---: |
| Report finalization | 7.20 | 1 | 19.1% |
| Filesystem records | 5.19 | 243 | 13.8% |
| Initial request snapshot | 4.71 | 1 | 12.5% |
| XML source hashing | 4.70 | 834 | 12.5% |
| Session creation | 3.13 | 1 | 8.3% |
| Property-function observation | 3.13 | 197 | 8.3% |
| Project-source records | 2.36 | 85 | 6.3% |
| Environment records | 2.04 | 705 | 5.4% |
| Glob records | 1.15 | 6 | 3.1% |
| External inputs | 1.03 | 65 | 2.7% |
| SDK result records | 0.96 | 2 | 2.6% |
| Property lookup | 0.82 | 703 | 2.2% |
| Task registration | 0.82 | 79 | 2.2% |
| Other | 0.38 | - | 1.0% |
| **Total instrumented CPU** | **37.62** | **2,069** | **100%** |

At 13 evaluations per build this is approximately 489 ms of observer CPU work. Parallel
evaluation reduces its observed wall-clock contribution to about 180 ms.

## Findings

1. **Report finalization remains the largest single cost.** Sorting is gone; the remaining
   cost is array creation, collection copying, category projection, and report construction.
2. **Filesystem recording is the largest repeated-record cost.** Path normalization,
   locking, key hashing, and dictionary updates occur about 243 times per evaluation.
3. **The request snapshot repeats mostly process-static work.** Runtime, OS, architecture,
   culture, feature-switch, provider, and toolset values are rebuilt for every evaluation.
4. **Raw XML hashing is material.** The observer hashes project/import bytes across about
   834 stream reads per evaluation.
5. **Property-function classification runs frequently.** Around 197 calls per evaluation
   enter the observation classifier, including calls that are ultimately classified pure.
6. **Environment tracking is high-cardinality.** Environment recording and lookup paths
   are entered roughly 700 times per evaluation.

## Improvement Priority

1. Transfer collection ownership to the report or materialize arrays only when storing a
   cache entry.
2. Cache the process-static portion of the request snapshot and record only per-evaluation
   values.
3. Reduce low-level filesystem records under semantic owners and batch keyed updates.
4. Reuse authoritative PRE/source hashes and avoid hashing the same source more than once.
5. Cache property-function classifications and bypass observer dispatch for known-pure calls.
6. Lazily allocate category dictionaries and short-circuit environment recording before
   normalization/locking.

The first target should be report finalization plus request snapshotting. Together they
consume about 12 ms of exclusive CPU per evaluation and require no loss of dependency
coverage.

## Confidence

The report publishes only:

- whole-build overhead reproduced in three independent BenchmarkDotNet runs;
- exclusive activity CPU time stable across five runs, 65 evaluations, and 30 processes;
- allocation attribution collected across all MSBuild processes.

Per-category subtractive wall-time estimates are retained only as a noise-floor check.
