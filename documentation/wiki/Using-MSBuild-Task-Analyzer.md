# Make an MSBuild task multithreaded with the task analyzer

This guide is for task authors who want their task to run in MSBuild's multithreaded task host. The analyzer finds process-global APIs, unsafe relative paths, and parameter types that should be migrated before the task opts into shared-process execution.

## Migration overview

Use this order:

1. Add the analyzer to the task project.
2. Apply `[MSBuildMultiThreadableTask]` to every concrete task class.
3. Implement `IMultiThreadableTask` when the task needs `TaskEnvironment`.
4. Fix unsafe API and call-chain diagnostics.
5. Migrate path inputs to typed parameters.
6. Test through a real MSBuild project with relative paths.

Do not begin by changing every `string` to `AbsolutePath`. First make the task independent of process-global state, then use typed parameters where they match the task's public contract.

## Enable the analyzer

The analyzer is not yet shipped as part of `Microsoft.Build.Framework`. Until it is packaged, reference its project or built assembly.

In the MSBuild repository, enable it for the Tasks project with:

```powershell
.\eng\common\dotnet.cmd msbuild src\Tasks\Microsoft.Build.Tasks.csproj `
  -restore `
  -t:Rebuild `
  -p:BuildAnalyzer=true `
  -v:minimal
```

For another repository, use a project reference while developing against an MSBuild checkout:

```xml
<ItemGroup>
  <ProjectReference Include="..\msbuild\src\TaskAnalyzer\TaskAnalyzer.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
```

## Opt into multithreaded task analysis

The typed-parameter diagnostics MSBuildTask0006-MSBuildTask0008 only apply when `[MSBuildMultiThreadableTask]` is applied directly to the task class:

```csharp
[MSBuildMultiThreadableTask]
public sealed class MyTask : Task, IMultiThreadableTask
{
    public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;

    public override bool Execute() => true;
}
```

The attribute is not inherited. Apply it to every concrete task, even when a base class already implements `IMultiThreadableTask`.

Only implement `IMultiThreadableTask` when the task needs the per-task environment:

```csharp
public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;
```

Use it instead of process-global state:

```csharp
AbsolutePath path = TaskEnvironment.GetAbsolutePath(InputPath);
string? value = TaskEnvironment.GetEnvironmentVariable("MY_VARIABLE");
ProcessStartInfo startInfo = TaskEnvironment.GetProcessStartInfo();
```

## Fix diagnostics in order

Start with diagnostics that identify unsafe behavior:

| Diagnostic | Action |
| --- | --- |
| MSBuildTask0001 | Remove APIs that are never safe, such as `Console.*` or `Environment.Exit` |
| MSBuildTask0002 | Replace process-global APIs with `TaskEnvironment` |
| MSBuildTask0003 | Resolve paths before file-system access |
| MSBuildTask0004 | Review reflection and assembly-loading behavior |
| MSBuildTask0005 | Follow and migrate the reported transitive call chain |

After the task is safe, address the typed-parameter diagnostics:

| Diagnostic | Action |
| --- | --- |
| MSBuildTask0006 | Replace a path-like `string` input with `AbsolutePath`, `FileInfo`, or `DirectoryInfo` |
| MSBuildTask0007 | Replace `ITaskItem` parsing with `ITaskItem<T>.Value` |
| MSBuildTask0008 | Move a relative default into `Execute()`, where `TaskEnvironment` is available |

## Choose a typed parameter

| Task input | Recommended type |
| --- | --- |
| A path that is not directly queried as a file or directory | `AbsolutePath` |
| A file whose `FileInfo` members are used | `FileInfo` |
| A directory whose `DirectoryInfo` members are used | `DirectoryInfo` |
| A path plus MSBuild item metadata | `ITaskItem<AbsolutePath>` |
| A file plus MSBuild item metadata | `ITaskItem<FileInfo>` |
| A directory plus MSBuild item metadata | `ITaskItem<DirectoryInfo>` |
| Multiple typed items | `ITaskItem<T>[]` |

For a typed item, use `Value` instead of parsing `ItemSpec`:

```csharp
public ITaskItem<FileInfo> InputFile { get; set; } = null!;

public override bool Execute()
{
    FileInfo file = InputFile.Value;
    string culture = InputFile.GetMetadata("Culture");
    return true;
}
```

## In-box migration examples

Use these existing tasks as focused examples:

- [`VerifyFileHash`](../../src/Tasks/FileIO/VerifyFileHash.cs) demonstrates a scalar `string` path migrated to `AbsolutePath`. The task no longer calls `GetAbsolutePath` itself.
- [`ZipDirectory`](../../src/Tasks/ZipDirectory.cs) demonstrates `ITaskItem<FileInfo>` and `ITaskItem<DirectoryInfo>`. The task retains item semantics while using `Value` for file-system operations.
- [`GetFileHash`](../../src/Tasks/FileIO/GetFileHash.cs) demonstrates an `ITaskItem<AbsolutePath>[]` input. The task reads each typed `Value`, updates item metadata, and returns the items through an output property.

### Scalar path example

Before:

```csharp
[Required]
public string File { get; set; } = null!;

public override bool Execute()
{
    AbsolutePath path = TaskEnvironment.GetAbsolutePath(File);
    return ProcessFile(path);
}
```

After:

```csharp
[Required]
public AbsolutePath File { get; set; }

public override bool Execute()
{
    return ProcessFile(File);
}
```

### Item with metadata example

Before:

```csharp
[Required]
public ITaskItem SourceDirectory { get; set; } = null!;

DirectoryInfo directory =
    new(TaskEnvironment.GetAbsolutePath(SourceDirectory.ItemSpec));
```

After:

```csharp
[Required]
public ITaskItem<DirectoryInfo> SourceDirectory { get; set; } = null!;

ProcessDirectory(SourceDirectory.Value);
```

`SourceDirectory.GetMetadata(...)` remains available when the task needs custom MSBuild metadata.

## Apply code fixes carefully

Visual Studio offers code fixes through `Ctrl+.`. The fixer only applies when every reference to the property can be safely rewritten. A diagnostic can therefore appear without an available fix.

From the command line:

```powershell
$env:BuildAnalyzer = 'true'

.\eng\common\dotnet.cmd format src\Tasks\Microsoft.Build.Tasks.csproj analyzers `
  --diagnostics MSBuildTask0006 MSBuildTask0007 MSBuildTask0008 `
  --severity warn `
  --framework net11.0 `
  --no-restore
```

Review the generated change instead of assuming the conversion is behavior-preserving. Changing an existing public task property type is a source and binary compatibility break for callers that instantiate the task directly.

## Missing, empty, and whitespace-only paths

Typed parameters centralize path conversion, but they do not remove every validation concern.

| Input | Current behavior |
| --- | --- |
| Required parameter is missing | MSBuild reports the missing `[Required]` parameter before `Execute()` |
| Empty scalar value | The value is not assigned; a required parameter is reported as missing |
| Empty `TaskItem<T>` identity | Typed-item construction rejects it |
| Whitespace-only value | Not yet rejected consistently on every platform |
| Task instantiated directly with `new` | MSBuild binding and `[Required]` validation do not run |

Do not assume that changing `string` to `AbsolutePath`, `FileInfo`, or `ITaskItem<T>` makes whitespace-only input invalid in every scenario. Central whitespace-only path validation is tracked by [#14487](https://github.com/dotnet/msbuild/issues/14487).

If the task must reject whitespace today, retain explicit contextual validation:

```csharp
if (string.IsNullOrWhiteSpace(input))
{
    Log.LogError("The Input parameter must specify a path.");
    return false;
}
```

For tasks that are also public .NET APIs, direct callers can bypass MSBuild:

- `AbsolutePath` can be left as `default`.
- `FileInfo`, `DirectoryInfo`, and `ITaskItem<T>` properties can be `null`.
- `[Required]` is enforced only when MSBuild binds and executes the task.

Keep guards when direct construction is supported, or document that the task must be created by MSBuild.

## Validate the migration

A migration test should exercise MSBuild parameter binding, not only instantiate the task directly.

Verify:

1. Relative inputs resolve against the project directory, not the process current directory.
2. `ITaskItem<T>` input and output metadata is preserved.
3. Scalar item parameters still reject multiple items.
4. Missing, empty, malformed, and whitespace-only values have the intended diagnostics.
5. Error and log messages preserve the path as entered by the user.
6. `[Output]` values do not unexpectedly become absolute.
7. The task works on both .NET and .NET Framework when both are supported.
8. Binlog replay shows the same task parameter values as the live build.

## Related diagnostics

| Diagnostic | Purpose |
| --- | --- |
| MSBuildTask0001 | API must not be used by an MSBuild task |
| MSBuildTask0002 | Use a `TaskEnvironment` alternative |
| MSBuildTask0003 | File-system API requires an absolute path |
| MSBuildTask0005 | A transitive call reaches an unsafe API |
| MSBuildTask0006 | Prefer a typed path property |
| MSBuildTask0007 | Prefer `ITaskItem<T>` over parsing `ItemSpec` |
| MSBuildTask0008 | Move a relative typed-path default into `Execute()` |
| MSBuildTask0009 | `ITaskItem<T>` uses an unsupported type |
| MSBuildTask0010 | `ITaskItem<T>` relies on a culture-sensitive conversion |
