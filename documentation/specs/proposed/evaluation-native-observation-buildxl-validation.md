# BuildXL differential validation of native evaluation observation

> **Scope correction:** this report remains accurate for its original
> scenario, but it is not a completeness result. The broader adversarial
> campaign found confirmed missing and incorrect records. See
> [evaluation-native-observation-buildxl-adversarial-report.md](evaluation-native-observation-buildxl-adversarial-report.md).

## Result

On 2026-08-27, the evaluator-native observer at commit `d17808b37a` was
compared with a BuildXL Detours trace of the same MSBuild evaluation on
Windows, .NET Framework 4.7.2, x64.

For the sampled evaluation:

- BuildXL reported 50 filesystem events over 22 unique scenario paths in
  both the cold and warm runs.
- Every one of the 22 BuildXL paths had an exact native record or a native
  semantic owner such as a glob or directory-enumeration request.
- No BuildXL-only path remained unexplained.
- The comparison found one likely native over-observation:
  `Directory.GetParent` was recorded as file metadata even though it only
  normalized the supplied path in this case.
- BuildXL did not emit the isolated successful
  `Exists('probe-only-dir')` access that the native observer recorded. This
  demonstrates that this Detours configuration is useful as an independent
  oracle, but is not sufficient as the only proof of directory-probe
  coverage.

The result is therefore **no missing native filesystem dependency detected
for this scenario**, not a proof that all possible evaluation paths are
covered.

## Measurement setup

| Property | Value |
| --- | --- |
| Observer commit | `d17808b37a` |
| Operating system | Windows |
| Evaluation process | `MSBuild.Benchmarks.exe`, .NET Framework 4.7.2, x64 |
| Sandbox | `Microsoft.BuildXL.Processes` `0.1.0-20260612.4`, `SandboxKind.Default` |
| Access policy | Report all accesses; monitor child processes |
| Trace boundary | File-access markers immediately before and after the measured evaluation |
| Path scope | Paths under the synthetic scenario root |
| Cold run | First evaluation in a new process |
| Warm run | One unmeasured evaluation, then one measured evaluation in the same process |

The sandbox listener retained the operation, requested access, status, error,
path, and enumeration pattern. The native side retained typed report records,
including project sources, file reads, probes, metadata, enumerations, globs,
searches, SDK results, property functions, and task registrations.

The comparison used **semantic ownership**, not literal set intersection:

- a project or import open must have a matching `ProjectSource` or
  `FileRead`;
- a probe must have a matching `PathProbe`;
- directory traversal may be owned by a matching `DirectoryEnumeration` or
  `Glob` request even when BuildXL reports internal directories or
  nonmatching entries;
- matching members returned by a glob are native semantic dependencies even
  if Detours reports only the directory enumeration syscall.

## Scenario

The project exercised:

- root project, explicit import, and wildcard imports;
- `Sdk.props` and `Sdk.targets` from a scenario-local SDK;
- valid `Directory.Parse.config`;
- positive and negative file probes;
- positive directory probes, including one directory used only by `Exists`;
- `File.ReadAllText`;
- item metadata `ModifiedTime`;
- `GetPathOfFileAbove`;
- recursive `Directory.GetFiles(..., AllDirectories)`;
- `DirectoryInfo.GetFiles("*.props")`;
- an item glob over 30 C# files in five subdirectories;
- `MakeRelative`;
- SDK resolution and `UsingTask` registration.

The native report completed evaluation successfully. Its only incompleteness
reason was `UnversionedToolsetInputs`, which is outside this filesystem
differential and remains an explicit cache-reuse blocker.

## Counts

| Metric | Cold | Warm |
| --- | ---: | ---: |
| BuildXL raw filesystem events | 50 | 50 |
| BuildXL unique operation records | 38 | 37 |
| BuildXL unique paths | 22 | 22 |
| BuildXL paths with a native semantic owner | 22 | 22 |
| BuildXL paths without a native semantic owner | **0** | **0** |
| Native path probes | 8 | 8 |
| Native directory enumerations | 2 | 2 |
| Native metadata observations | 2 | 2 |
| Native file reads | 8 | 8 |
| Native project sources | 6 | 6 |
| Native globs | 2 | 2 |

The cold-only unique operation record was an additional
`GetFileAttributesEx` probe for `Sdk.props`. The cold and warm path sets were
otherwise identical.

### Unique BuildXL operation records

| Operation | Cold | Warm |
| --- | ---: | ---: |
| `CreateFile` | 8 | 8 |
| `GetFileAttributes` | 2 | 2 |
| `GetFileAttributesEx` | 10 | 9 |
| `FindFirstFileEx` | 8 | 8 |
| `FindNextFile` | 3 | 3 |
| `NtQueryDirectoryFile` | 7 | 7 |

## Coverage mapping

| BuildXL access group | Paths | Native owner |
| --- | ---: | --- |
| Files opened for read | 8 | Eight `FileRead` records; six are also root/import `ProjectSource` records |
| Root project | 1 | Raw-byte `ProjectSource` and `FileRead` |
| Parser configuration | 1 | Positive `PathProbe`, raw-byte `FileRead`, and parse outcome |
| Explicit and wildcard imports | 3 | Import `ProjectSource`, raw-byte `FileRead`, and import `Glob` where applicable |
| SDK imports | 2 | Import `ProjectSource`, raw-byte `FileRead`, and SDK result |
| Settings file | 1 | Decoded-text `FileRead`, search candidate, file probe, and item metadata |
| Positive and negative marker probes | 2 | `PathProbe` with the consumed Boolean result |
| Wildcard-import directory | 1 | Import `Glob` and directory probe |
| Item-glob root and five subdirectories | 6 | Item `Glob` with `src/**/*.cs`, ordered result fingerprint, and 30 retained members |
| Property-function enumeration root and nested directory | 2 | Recursive `DirectoryEnumeration` with pattern `*.props` |
| Matching enumeration members | 2 | Enumeration result members |
| Nonmatching `top.targets` seen by Win32 enumeration | 1 | Owned by the `*.props` directory-enumeration request; it is not a result member |

Rows intentionally overlap when one path has several native owners.

This mapping is stronger than a literal path comparison. For example,
BuildXL reported the five directories traversed by `src/**/*.cs`, while the
native observer recorded one semantic glob plus all 30 matching C# members.

## Native records without an exact BuildXL path

| Native record | Count | Interpretation |
| --- | ---: | --- |
| Item-glob result members | 30 | Expected. BuildXL reported directory traversal, while native observation retained the semantic members that affected evaluation. |
| Glob request roots | 2 | Expected. The actual syscalls started at the static `src` and `imports` prefixes. |
| SDK result directory | 2 | Expected. It is a semantic resolver result; BuildXL separately reported reads of `Sdk.props` and `Sdk.targets`. |
| `UsingTask` assembly path | 1 | Expected. Evaluation registered the path but did not load the task assembly. |
| Positive `probe-only-dir` result | 1 | BuildXL-oracle gap. Native observation recorded `Exists=True`, but no scenario-root Detours access was emitted. |
| `Directory.GetParent` metadata record for `enum\child` | 1 | Likely native over-observation. The operation returned the parent path without filesystem I/O. |

`Directory.GetParent` should be reviewed separately and classified as path
normalization/ambient input where necessary, rather than file metadata.

## Why the old raw overlap numbers are misleading

The legacy benchmark summary reported:

- native unique paths: 53;
- BuildXL unique paths: 22;
- literal overlap: 13;
- BuildXL-only paths: 9;
- native-only paths: 40.

Those numbers do not represent nine missing dependencies:

- eight were internal directories traversed by a native glob or recursive
  enumeration;
- one was nonmatching `top.targets`, surfaced while Win32 scanned a
  `*.props` enumeration;
- the benchmark path helper also rooted relative native glob members against
  the process current directory instead of the glob root.

The typed semantic mapping reduces the unexplained BuildXL set from nine to
zero.

## Limitations

- This was one representative synthetic project, not an exhaustive corpus.
- The run validated Windows Detours behavior only. Linux and macOS need their
  own independent tracing mechanisms.
- The listener intentionally filtered to the scenario root. Runtime,
  installed toolset, Registry, and other machine-wide accesses were outside
  this filesystem comparison.
- Custom hosts, custom filesystems, custom directory caches, unrestricted
  property functions, and arbitrary SDK resolver internals were not tested.
- A warm run with one prior evaluation does not represent every possible PRE,
  SDK, filesystem, or server-cache state.
- BuildXL reports low-level accesses; the native observer records semantic
  dependencies. Automated comparison must preserve this ownership mapping.

## Follow-up

1. Reclassify `Directory.GetParent` so it does not create a false file
   metadata dependency.
2. Turn this semantic mapping into a repeatable differential test; do not use
   raw path intersection as the pass criterion.
3. Add scenarios for malformed parser configuration, missing imports,
   failed reads, empty and changing globs, shared-cache reuse, and
   out-of-proc evaluation.
4. Repeat on large real-world projects after the synthetic differential is
   stable.
