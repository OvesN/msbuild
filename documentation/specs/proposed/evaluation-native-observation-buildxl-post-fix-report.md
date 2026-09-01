# Post-fix BuildXL comparison for native evaluation observation

## Result

On 2026-08-27, the native observer at commit `e2723fec46` was compared with
BuildXL Detours for the six filesystem-correctness scenarios fixed after the
original adversarial campaign.

- BuildXL reported 40 accesses over 17 unique scenario paths.
- Native observation had exact identities for 15 of the 17 BuildXL paths.
- The two exact-set differences are semantically explained:
  - one trailing-directory-separator alias;
  - one intermediate directory traversed by a recursive enumeration.
- No BuildXL path lacked a native dependency or semantic owner.
- Missing reads, malformed imports, and malformed roots now produce one native
  report with the accessed path.

SDK-resolver implementation files, custom filesystem providers, and
post-evaluation mutation detection remain outside this comparison.

## Setup

| Property | Value |
| --- | --- |
| Observer commit | `e2723fec46` |
| Platform | Windows |
| Sandbox | BuildXL Detours, `SandboxKind.Default` |
| BuildXL package | `Microsoft.BuildXL.Processes` `0.1.0-20260612.4` |
| Child runtime | .NET Framework 4.7.2, x64 |
| Runs | One cold evaluation per scenario |
| Trace boundary | Marker accesses immediately before and after evaluation |
| Trace scope | Each synthetic scenario root |
| Comparison | Exact canonical path set, followed by semantic ownership analysis |

## Scenario results

| Scenario | Native reports | Native paths | BuildXL paths | Exact overlap | Native-only | BuildXL-only |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Path-only calculations | 1 | 6 | 4 | 3 | 3 | 1 |
| Relative paths | 1 | 5 | 6 | 5 | 0 | 1 |
| Normal plus `\\?\` path | 1 | 2 | 2 | 2 | 0 | 0 |
| Missing `ReadAllText` | 1 | 2 | 2 | 2 | 0 | 0 |
| Malformed import | 1 | 2 | 2 | 2 | 0 | 0 |
| Malformed root | 1 | 1 | 1 | 1 | 0 | 0 |
| **Total** | **6** | **18** | **17** | **15** | **3** | **2** |

Counts are per isolated scenario, so the total is a sum rather than one
combined path set.

## Differences from BuildXL

### Trailing directory separator

In the path-only scenario:

```text
Native:  ...\path-only\src\
BuildXL: ...\path-only\src
```

These identify the same directory. This accounts for one native-only and one
BuildXL-only entry. The dependency is observed, but exact identity
canonicalization still does not remove a trailing separator from a non-root
directory.

### Recursive traversal directory

BuildXL reported:

```text
...\relative-paths\enum\nested
```

Native observation recorded:

- enumeration root `...\relative-paths\enum`;
- search pattern `*.txt`;
- `SearchOption.AllDirectories`;
- result `...\relative-paths\enum\nested\b.txt`;
- the complete enumeration result fingerprint.

The intermediate directory is an implementation-level traversal access, not
a separate evaluation result. It is semantically owned by the recursive
enumeration.

### Expected native-only semantic paths

The remaining native-only paths in the path-only scenario were:

- the semantic glob request root;
- the matching glob result `src\a.cs`.

BuildXL reported the directory scan but did not report a separate file open
for the matched item. Native observation intentionally retains the semantic
member because it affects evaluation.

## What changed after the six fixes

| Original issue | Post-fix result |
| --- | --- |
| Path-only calculations appeared as filesystem metadata | `NativeMetadataReads=0`; the values remain ambient/path-resolution observations |
| Relative operations retained relative identities | All filesystem records in the scenario are absolute; five of six BuildXL paths match exactly |
| `C:\x` and `\\?\C:\x` produced duplicate identities | Root plus input are the only two native paths; both match BuildXL |
| A failed read lacked a typed missing path | Root and `missing.txt` both match BuildXL exactly |
| Malformed import had only a positive probe | Root and import match; native now has two file reads, a failed import source, and a typed parse failure |
| Malformed root produced no report | Native now emits one report, one root source/file read, and one typed parse failure |

The malformed-import path was already superficially present in the old
native path set because of its positive probe. The important correction is
the record shape: raw bytes, source role/outcome, and typed failure are now
retained.

## What the native layer tracks

### Filesystem inputs

- **Project sources:** root/import role, `Parsed`/`ParseFailure`/`LoadFailure`,
  canonical path, raw-byte or parsed-XML hash domain, encoding, provider,
  consumed last-write time, and read-time timestamp stability.
- **File reads:** canonical path, provider, content hash, hash domain, and
  whether the content identity is verifiable.
- **Probes:** file, directory, or file-or-directory kind; canonical path;
  provider; and the consumed Boolean result.
- **Metadata:** attributes, timestamps, length, link target, operation, path,
  provider, and returned value.
- **Directory enumerations:** root, pattern, recursion/options identity,
  files/directories kind, completion state, count, ordered fingerprint, and
  optional retained members.
- **Globs:** semantic root, include, excludes, lazy/drive-enumerating state,
  result count/fingerprint, and optional retained members.
- **Searches:** ordered candidate sequence/fingerprint and ordered selected
  path sequence/count/fingerprint. Candidate details are optional; selected
  paths are retained because they are direct dependencies. An empty selected
  sequence represents a miss, while ignored wildcard matches remain selected.
- **Failures:** affected category, operation, canonical path and provider when
  applicable, exception type, HRESULT, and diagnostic-only message.

### Related evaluation inputs

The report also tracks request/global-property state, environment values,
Registry reads, classified property functions, SDK request/result/cache
identity, toolset selection, task registration, shared-cache regimes, and
unsupported side effects. Path-only calculations are retained in the ambient
domain instead of being represented as filesystem metadata.

## Benchmark snapshot

The same tip was measured with the existing evaluation-only benchmark:
50 evaluations per child process, 10 aggregate samples, baseline versus native
observation.

These timing and allocation figures predate the semantic-equivalence preflight,
duplicate-import fixture, and uniform duplicate-import recording now used by
the harness. They remain historical observer-overhead evidence but are not
directly reproducible with the current benchmark shape.

| Scenario | Baseline, ms | Native, ms | Time overhead | Added allocation | Added per evaluation |
| --- | ---: | ---: | ---: | ---: | ---: |
| Typical | 239.946 | 263.569 | 23.623 ms / 9.85% | 715,373 B / 4.07% | 14,307 B |
| Glob-heavy | 462.768 | 495.009 | 32.241 ms / 6.97% | 746,468 B / 0.95% | 14,929 B |
| Ambient and SDK | 313.428 | 351.046 | 37.618 ms / 12.00% | 1,376,575 B / 6.39% | 27,532 B |

BenchmarkDotNet's outer ratios were `1.10`, `1.06`, and `1.12`,
respectively. These are evaluation-only microbenchmark costs, not whole-build
overhead; the extra time per individual evaluation was approximately
0.47-0.75 ms.

## Conclusion

For the six corrected scenarios, there are no unexplained BuildXL-only
filesystem paths. Four scenarios have exact path-set equality. The other two
differences are one canonical trailing-separator alias and one
enumeration-internal traversal directory.
