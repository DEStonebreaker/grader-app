# Grader CLI

Grade C17 and C++23 submissions on Linux using Isolate. Problems live in local
folders, with named test cases and optional trusted C# driver assemblies. The CLI
compiles once, runs every case in a fresh sandbox, and reports text or JSON.

## Quickstart

Build on a machine with the .NET 10 SDK:

```sh
dotnet build examples/sum/driver/SumDriver.csproj -c Release
dotnet publish grader-app/grader-app.csproj -c Release -r linux-x64 --self-contained false -o artifacts/linux-x64
```

On a configured Ubuntu x64 judge host, with .NET 10 runtime, GCC/G++, and Isolate:

```sh
./artifacts/linux-x64/grader-app grade examples/sum examples/sum/main.cpp
./artifacts/linux-x64/grader-app grade examples/sum examples/sum/main.c --format json
./artifacts/linux-x64/grader-app --help
```

Both cases in the [sum example](examples/sum/README.md) should pass. Keep the
example folder, including its built driver and dependencies, together when
copying it to the judge host. You can also run from source on Linux:

```sh
dotnet run --project grader-app -- grade examples/sum examples/sum/main.cpp
```

See [Linux setup and validation](scripts/README.md) for the reproducible VM flow.
On ARM64 Linux (including native OrbStack on Apple Silicon), publish with
`-r linux-arm64 -o artifacts/linux-arm64` and use that executable instead.
The judge runs as an ordinary user; only Isolate's installed helper needs elevated
privileges. The SDK is needed to build the application/drivers and run tests; a
published framework-dependent grader needs only the runtime on the judge host.

## Problem folders

Each folder contains `problem.toml`, input files, expected answers, and optionally
an instructor-built driver. Paths below are relative to the problem folder.

```toml
[problem]
title = "Add two integers"

[limits]
ct_seconds = 10.0
ct_memory_kb = 262144
rt_seconds = 1.0
rt_memory_kb = 65536

[grading]
checker = "tokens"

[[tests]]
name = "positive"
input = "tests/positive.in"
expected = "tests/positive.out"

[[tests]]
name = "negative"
input = "tests/negative.in"
expected = "tests/negative.out"
```

Cases run in manifest order. Names must be unique. Asset paths must stay inside
the folder and cannot contain symlinks below its root. All limits must be positive
and finite; wall time is twice the configured CPU time. Compilation allows up to
16 processes, runtime one. Output capture retains at most 1 MiB per stream and
continues draining; a truncated runtime stream yields `OutputLimitExceeded`.

The built-in `tokens` checker compares whitespace-separated tokens with ordinal,
case-sensitive comparison. `exact` compares the full UTF-8 decoded output,
including newlines. Expected answer files remain on the host, outside the sandbox.
Submission failures continue to the next case; driver/infrastructure failures stop
the job. A successful process exit alone is never a passing grading verdict.

## Trusted C# drivers

A driver implements `grader_app.Grading.IGradingDriver` (API version 1) in a .NET
10 class library built against this grader release. It has a public parameterless
constructor. Configure its fully qualified type and relative assembly path:

```toml
[grading]
driver_assembly = "driver/bin/Release/net10.0/SumDriver.dll"
driver_type = "SumDriver.NumericDriver"
```

```csharp
public sealed class MyDriver : IGradingDriver
{
    public async Task<CheckResult> GradeAsync(GradingContext context, CancellationToken token)
    {
        var execution = await context.RunAsync(token);
        return new CheckResult(execution.StandardOutput.Trim() == context.ExpectedOutput.Trim());
    }
}
```

Each case gets a new context; the driver instance lasts for one job. Drivers call
`RunAsync` exactly once per case. It executes the compiled submission in a fresh
Isolate box with that case's stdin, configured limits, and bounded output. The
host owns resource/runtime verdicts; drivers return a pass/fail answer check.
See the [buildable driver example](examples/sum/driver/NumericDriver.cs) and its
[project configuration](examples/sum/driver/SumDriver.csproj). Distribute driver
build output/dependencies together. Drivers must use the same host contract;
`ApiVersion` other than 1 is rejected.

Drivers run in the grader process and have the judge user's permissions. Only
load problem folders from trusted instructors. Assembly loading is not a security
boundary. Drivers must honor cancellation and must not run submissions directly.
Driver exceptions become `DriverError`; Console stdout is redirected to diagnostic
stderr during grading. An infinite loop in trusted driver code requires external
process supervision. This first API supports non-interactive stdin/stdout grading,
not interactive or multi-run drivers.

## Reports and exit codes

`--format text` is the default. `--format json` writes one JSON document to stdout;
use the published executable rather than `dotnet run` when build messages would
interfere with automation. Reports include `schemaVersion: 1`, problem, overall
verdict, compilation diagnostics, ordered case results, raw sandbox metrics,
truncation flags, and `exitCode`. Invalid input and cancellation return an error
object with `error`, `message`, `schemaVersion`, and `exitCode`.

| Code | Meaning |
| --- | --- |
| 0 | Every case passed |
| 1 | Infrastructure or trusted-driver failure |
| 2 | Invalid CLI arguments, problem configuration, or missing input/tool path |
| 3 | Submission compilation failed or exceeded compilation limits |
| 4 | Wrong answer, runtime error, time/memory limit, or output limit |
| 130 | Cancelled; sandbox cleanup is attempted before return |

Choose `--language c` or `--language cpp`, or infer it from `.c`, `.cpp`, `.cc`, or
`.cxx`. Set an absolute `--isolate` path, or `VALIMARK_ISOLATE_PATH`; the default is
`/usr/local/bin/isolate`.

`--box-id` defaults to 0, with supported CLI range 0–999 (also constrained by the
host's Isolate configuration). A file lease reserves the ID for the complete job;
conflicting CLI jobs fail clearly instead of resetting each other's boxes. Run
all grading processes under the same judge account and `TMPDIR`. Use distinct IDs
for concurrent jobs. Other Isolate clients and direct library calls must reserve
their own non-overlapping IDs. Do not delete lease files while jobs are running.

## Development

```sh
dotnet format grader-app.sln --verify-no-changes
dotnet build grader-app.sln
dotnet test grader-app.sln
```

Most tests use fake Isolate and can run on macOS. Real Linux tests are opt-in; see
[the test guide](grader-app.Tests/README.md). `scripts/validate-linux.sh` runs the
full real-Isolate lane and published CLI smoke tests on an already configured
native Ubuntu x64 or ARM64 host. Translated amd64 OrbStack machines on Apple
Silicon cannot validate Isolate's namespace syscalls; use native ARM64 locally
and native x64 Linux for x64 release qualification. Agent instructions and engineering handoffs are local files and
are intentionally ignored by Git.
