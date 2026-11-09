# Grader tests

Run the normal test suite from the repository root:

```sh
dotnet test
```

Most tests use a temporary fake Isolate executable. They exercise the real
argument construction, path validation, file transfer, metadata parsing,
bounded output capture, and cleanup code without requiring cgroups or root
access.

The real-Isolate tests are skipped by default. Run them on a configured Ubuntu
development or CI judge host with the .NET SDK installed:

```sh
sudo -u judge -- env \
  VALIMARK_RUN_ISOLATE_TESTS=1 \
  VALIMARK_ISOLATE_PATH=/usr/local/bin/isolate \
  dotnet test --filter Category=RealIsolate
```

These integration tests compile C17 and C++23 submissions, execute their
artifacts in separate boxes, and verify that reusing a box ID does not leak
files from an earlier execution.

The production judge server only needs the .NET runtime. Do not install the SDK
there solely for these tests; the deployment smoke test remains its end-to-end
release check until a dedicated Linux test runner is available.

## How to read a failure

The test runner prints the test method that failed, followed by the expected and
actual values. The method names describe the behavior being protected. Start by
running just one failing test with a name filter, for example:

```sh
dotnet test --filter FullyQualifiedName~RunFormatsDecimalLimits
```

After changing C# code, the normal verification sequence is:

```sh
dotnet format
dotnet build
dotnet test
```

The multi-case grading tests exercise the CLI with a fake Isolate, including a
real loaded C# driver assembly, verdicts, JSON diagnostics, truncation, cancellation,
and cross-process box leases. The real-Isolate lane also grades two cases through
the C# example driver and checks that runtime files do not leak between cases.

For a full published-artifact check on an already configured Ubuntu x64 host,
run `./scripts/validate-linux.sh` as `judge`. It enables every real sandbox test
and grades both example C/C++ submissions through the published CLI. See
[VM setup](../scripts/README.md) for the toolchain and cgroup setup.
