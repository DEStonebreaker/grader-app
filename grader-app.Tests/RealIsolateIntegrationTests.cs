using grader_app.Isolate;

namespace grader_app.Tests;

[Trait("Category", "RealIsolate")]
public sealed class RealIsolateIntegrationTests
{
    private static int _nextBoxId = 80;

    [RealIsolateFact]
    public async Task C17SubmissionCompilesAndRunsInsideFreshBoxes()
    {
        const string source = """
                              #include <stdio.h>
                              int main(void) {
                                  _Static_assert(sizeof(int) >= 2, "C17 compiler is usable");
                                  puts("c17-ok");
                                  return 0;
                              }
                              """;

        var result = await CompileAndRunAsync(SourceLanguage.C, source);

        Assert.Equal("c17-ok\n", result.StandardOutput);
    }

    [RealIsolateFact]
    public async Task Cpp23SubmissionCompilesAndRunsInsideFreshBoxes()
    {
        const string source = """
                              #include <iostream>
                              int main() {
                                  auto value = 23uz;
                                  std::cout << "cpp" << value << '\n';
                                  return 0;
                              }
                              """;

        var result = await CompileAndRunAsync(SourceLanguage.Cpp, source);

        Assert.Equal("cpp23\n", result.StandardOutput);
    }

    [RealIsolateFact]
    public async Task ReusingABoxIdDoesNotLeakFilesBetweenExecutions()
    {
        const string source = """
                              #include <stdio.h>
                              #include <unistd.h>
                              int main(void) {
                                  if (access("marker", F_OK) == 0) {
                                      puts("dirty");
                                      return 1;
                                  }
                                  FILE *marker = fopen("marker", "w");
                                  if (marker == NULL) return 2;
                                  fclose(marker);
                                  puts("clean");
                                  return 0;
                              }
                              """;

        using var workspace = new IntegrationWorkspace();
        var executablePath = await CompileAsync(workspace, SourceLanguage.C, source);
        var executor = CreateExecutor();
        var runBoxId = NextBoxId();

        var first = await RunAsync(executor, runBoxId, executablePath);
        var second = await RunAsync(executor, runBoxId, executablePath);

        Assert.Equal("clean\n", first.StandardOutput);
        Assert.Equal("clean\n", second.StandardOutput);
    }

    private static async Task<ExecutionResult> CompileAndRunAsync(
        SourceLanguage language,
        string source)
    {
        // Keep the artifact alive only long enough to transfer it into a separate runtime box.
        using var workspace = new IntegrationWorkspace();
        var executablePath = await CompileAsync(workspace, language, source);
        return await RunAsync(CreateExecutor(), NextBoxId(), executablePath);
    }

    private static async Task<string> CompileAsync(
        IntegrationWorkspace workspace,
        SourceLanguage language,
        string source)
    {
        // Compile through the production Compiler so the test covers staging, flags, and retrieval.
        var sourcePath = workspace.CreateFile(
            language == SourceLanguage.C ? "main.c" : "main.cpp",
            source);
        var executablePath = Path.Combine(workspace.Path, "main");
        var compiler = new Compiler(CreateExecutor());
        var result = await compiler.CompileAsync(
            NextBoxId(),
            sourcePath,
            executablePath,
            new CompilationOptions(language),
            CompilationOptions(),
            CancellationToken.None);

        Assert.True(
            result.Succeeded,
            $"Compilation failed: {result.Outcome}{Environment.NewLine}{result.Execution.StandardError}");
        return executablePath;
    }

    private static async Task<ExecutionResult> RunAsync(
        IsolateExecutor executor,
        uint boxId,
        string executablePath)
    {
        // Transfer the artifact into a newly initialized runtime box for every invocation.
        var request = new IsolateExecutionRequest(
            boxId,
            RuntimeOptions(),
            "./main",
            [],
            [new SandboxInputFile(executablePath, "main")],
            []);
        var result = await executor.ExecuteAsync(request, CancellationToken.None);
        Assert.Equal(TerminationReason.Exited, result.Termination);
        Assert.Equal(0, result.ExitCode);
        return result;
    }

    private static IsolateExecutor CreateExecutor()
    {
        var isolatePath = Environment.GetEnvironmentVariable("VALIMARK_ISOLATE_PATH")
                          ?? "/usr/local/bin/isolate";
        return new IsolateExecutor(isolatePath);
    }

    private static IsolateRunOptions CompilationOptions()
    {
        return new IsolateRunOptions(
            CpuSeconds: 10,
            WallSeconds: 20,
            MemoryKb: 262_144,
            MaxProcesses: 16,
            EnvironmentVariables: new Dictionary<string, string>
            {
                ["PATH"] = "/usr/bin:/bin"
            });
    }

    private static IsolateRunOptions RuntimeOptions()
    {
        return new IsolateRunOptions(
            CpuSeconds: 2,
            WallSeconds: 4,
            MemoryKb: 65_536,
            MaxProcesses: 1,
            EnvironmentVariables: new Dictionary<string, string>());
    }

    private static uint NextBoxId()
    {
        return (uint)Interlocked.Increment(ref _nextBoxId);
    }

    private sealed class IntegrationWorkspace : IDisposable
    {
        public IntegrationWorkspace()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"valimark-integration-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateFile(string name, string contents)
        {
            var filePath = System.IO.Path.Combine(Path, name);
            File.WriteAllText(filePath, contents);
            return filePath;
        }

        public void Dispose()
        {
            // Remove only the unique integration workspace created by this test instance.
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
