# Two-case grading example

Build the trusted driver once, using the same grader source/release:

```sh
dotnet build examples/sum/driver/SumDriver.csproj -c Release
```

Then, on the configured Linux judge host:

```sh
dotnet run --project grader-app -- grade examples/sum examples/sum/main.cpp
dotnet run --project grader-app -- grade examples/sum examples/sum/main.c --format json
```

Expected: two Passed cases and exit code 0. The driver runs each case through
`GradingContext.RunAsync`, then parses the answer as an integer. It is a trusted
class library loaded in the grader process, not a student program. Build output
and dependencies must accompany the driver DLL. The judge needs the .NET runtime;
only driver authors/build hosts need the SDK.

Remove `driver_assembly` and `driver_type` from problem.toml to use the built-in
checker. `tokens` compares whitespace-separated tokens using ordinal comparison;
`exact` compares the complete decoded UTF-8 strings, including newlines.
