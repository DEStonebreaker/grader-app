using System.Globalization;
using System.Text;
using CliWrap;
using CliWrap.Buffered;

namespace grader_app.Isolate;

public enum TerminationReason
{
    Exited,
    TimeLimitExceeded,
    MemoryLimitExceeded,
    Signaled,
    SandboxError
}

public sealed record ExecutionResult(
    TerminationReason Termination,
    int? ExitCode,
    int? ExitSignal,
    TimeSpan CpuTime,
    TimeSpan WallTime,
    long? PeakMemoryKb,
    string StandardOutput,
    string StandardError,
    string? SandboxMessage);

public sealed record SandboxInputFile(
    string HostPath,
    string SandboxPath);

public sealed record SandboxOutputFile(
    string SandboxPath,
    string HostPath);

public sealed record IsolateExecutionRequest(
    uint BoxId,
    IsolateRunOptions Options,
    string Executable,
    IReadOnlyList<string> ExecutableArguments,
    IReadOnlyList<SandboxInputFile> InputFiles,
    IReadOnlyList<SandboxOutputFile> OutputFiles,
    string? StandardInputPath = null);

public sealed class IsolateExecutor
{
    private const int MaxCapturedStreamBytes = 1024 * 1024;
    private readonly string _isolateBinary;
    private readonly string _workingDirectory;
    private readonly SemaphoreSlim _runtimeSlots = new(1);

    public IsolateExecutor(string isolateBinary = "/usr/local/bin/isolate")
    {
        // Use a trusted absolute executable and an accessible host directory for every child process.
        ArgumentException.ThrowIfNullOrWhiteSpace(isolateBinary);

        if (!Path.IsPathFullyQualified(isolateBinary))
        {
            throw new ArgumentException("Isolate path must be absolute.", nameof(isolateBinary));
        }

        _isolateBinary = Path.GetFullPath(isolateBinary);
        _workingDirectory = Path.GetFullPath(Path.GetTempPath());
    }

    public async Task<ExecutionResult> ExecuteAsync(
        uint boxId,
        IsolateRunOptions options,
        string executable,
        IEnumerable<string>? executableArgs,
        CancellationToken cancellationToken)
    {
        // Preserve the simple execution API for commands that do not transfer sandbox files.
        return await ExecuteAsync(
            new IsolateExecutionRequest(
                boxId,
                options,
                executable,
                executableArgs?.ToArray() ?? [],
                [],
                []),
            cancellationToken);
    }

    public async Task<ExecutionResult> ExecuteAsync(
        IsolateExecutionRequest request,
        CancellationToken cancellationToken)
    {
        // Serialize the entire staging and execution lifecycle so box operations remain bounded.
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        await _runtimeSlots.WaitAsync(cancellationToken);

        try
        {
            return await ExecuteBoxAsync(request, cancellationToken);
        }
        finally
        {
            _runtimeSlots.Release();
        }
    }

    private async Task<ExecutionResult> ExecuteBoxAsync(
        IsolateExecutionRequest request,
        CancellationToken cancellationToken)
    {
        // Own the complete box lifecycle here so every initialization attempt reaches cleanup.
        var metadataPath = Path.Combine(
            _workingDirectory,
            $"valimark-isolate-{request.BoxId}-{Guid.NewGuid():N}.meta");

        ExecutionResult? executionResult = null;
        var initializationAttempted = false;

        try
        {
            initializationAttempted = true;
            var initResult = await Cli
                .Wrap(_isolateBinary)
                .WithArguments(Isolate.Init(request.BoxId))
                .WithWorkingDirectory(_workingDirectory)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(cancellationToken);

            if (!initResult.IsSuccess)
            {
                executionResult = SandboxFailure(
                    initResult.StandardOutput,
                    initResult.StandardError,
                    initResult.RunTime,
                    $"Isolate failed to initialize box {request.BoxId} (exit code {initResult.ExitCode}).");
            }
            else
            {
                var boxDirectory = GetBoxDirectory(initResult.StandardOutput);
                await CopyInputFilesAsync(
                    request.InputFiles,
                    boxDirectory,
                    cancellationToken);

                var runCommand = Cli
                    .Wrap(_isolateBinary)
                    .WithArguments(Isolate.Run(
                        request.BoxId,
                        request.Options,
                        request.Executable,
                        request.ExecutableArguments,
                        metadataPath,
                        request.StandardInputPath))
                    .WithWorkingDirectory(_workingDirectory)
                    .WithValidation(CommandResultValidation.None);

                var runResult = await ExecuteCapturedAsync(runCommand, cancellationToken);
                var metadata = await ReadMetadataAsync(metadataPath, cancellationToken);
                executionResult = CreateExecutionResult(runResult, metadata);

                if (IsSuccessfulExecution(executionResult))
                {
                    await CopyOutputFilesAsync(
                        request.OutputFiles,
                        boxDirectory,
                        cancellationToken);
                }
            }
        }
        finally
        {
            if (initializationAttempted)
            {
                executionResult = await CleanupBoxAsync(
                    _isolateBinary,
                    _workingDirectory,
                    request.BoxId,
                    executionResult);
            }

            TryDeleteMetadata(metadataPath);
        }

        return executionResult
               ?? throw new InvalidOperationException("Isolate execution completed without a result.");
    }

    private static void ValidateRequest(IsolateExecutionRequest request)
    {
        // Reject invalid host paths before allocating a sandbox or copying any files.
        ArgumentNullException.ThrowIfNull(request.Options);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Executable);
        ArgumentNullException.ThrowIfNull(request.ExecutableArguments);
        ArgumentNullException.ThrowIfNull(request.InputFiles);
        ArgumentNullException.ThrowIfNull(request.OutputFiles);

        if (request.StandardInputPath is not null)
        {
            ValidateSandboxPath(request.StandardInputPath);

            if (!request.InputFiles.Any(file =>
                    string.Equals(
                        file.SandboxPath,
                        request.StandardInputPath,
                        StringComparison.Ordinal)))
            {
                throw new ArgumentException(
                    "Standard input must refer to a staged sandbox input file.",
                    nameof(request));
            }
        }

        foreach (var input in request.InputFiles)
        {
            ArgumentNullException.ThrowIfNull(input);
            ValidateSandboxPath(input.SandboxPath);

            var hostPath = RequireAbsolutePath(input.HostPath, nameof(input.HostPath));
            var source = new FileInfo(hostPath);
            source.Refresh();

            if (!source.Exists || source.LinkTarget is not null)
            {
                throw new FileNotFoundException(
                    "Sandbox input must be an existing regular file, not a symbolic link.",
                    hostPath);
            }
        }

        foreach (var output in request.OutputFiles)
        {
            ArgumentNullException.ThrowIfNull(output);
            ValidateSandboxPath(output.SandboxPath);

            var hostPath = RequireAbsolutePath(output.HostPath, nameof(output.HostPath));
            var parentDirectory = Path.GetDirectoryName(hostPath);

            if (parentDirectory is null || !Directory.Exists(parentDirectory))
            {
                throw new DirectoryNotFoundException(
                    $"Sandbox output directory does not exist: {parentDirectory}");
            }

            if (File.Exists(hostPath) || Directory.Exists(hostPath))
            {
                throw new IOException($"Sandbox output path already exists: {hostPath}");
            }
        }

        var duplicateSandboxInputs = request.InputFiles
            .GroupBy(file => file.SandboxPath, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicateSandboxInputs is not null)
        {
            throw new ArgumentException(
                $"Multiple inputs target sandbox path '{duplicateSandboxInputs.Key}'.",
                nameof(request));
        }

        var duplicateSandboxOutputs = request.OutputFiles
            .GroupBy(file => file.SandboxPath, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicateSandboxOutputs is not null)
        {
            throw new ArgumentException(
                $"Multiple outputs read sandbox path '{duplicateSandboxOutputs.Key}'.",
                nameof(request));
        }

        var duplicateHostOutputs = request.OutputFiles
            .GroupBy(file => Path.GetFullPath(file.HostPath), GetPathComparer())
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicateHostOutputs is not null)
        {
            throw new ArgumentException(
                $"Multiple outputs target host path '{duplicateHostOutputs.Key}'.",
                nameof(request));
        }
    }

    private static string GetBoxDirectory(string initializationOutput)
    {
        // Isolate prints its initialized directory; all staged files belong in its box child.
        var isolateDirectoryText = initializationOutput.Trim();
        if (string.IsNullOrWhiteSpace(isolateDirectoryText))
        {
            throw new InvalidDataException("Isolate did not report its initialized box directory.");
        }

        var isolateDirectory = Path.GetFullPath(isolateDirectoryText);
        var boxDirectory = Path.GetFullPath(Path.Combine(isolateDirectory, "box"));

        if (!IsPathInside(isolateDirectory, boxDirectory) || !Directory.Exists(boxDirectory))
        {
            throw new InvalidDataException(
                $"Isolate reported an invalid box directory: {isolateDirectoryText}");
        }

        return boxDirectory;
    }

    private static async Task CopyInputFilesAsync(
        IReadOnlyList<SandboxInputFile> inputFiles,
        string boxDirectory,
        CancellationToken cancellationToken)
    {
        // Populate only validated paths beneath the freshly initialized box directory.
        foreach (var input in inputFiles)
        {
            var destinationPath = ResolveSandboxPath(boxDirectory, input.SandboxPath);
            var destinationDirectory = Path.GetDirectoryName(destinationPath)
                                       ?? throw new InvalidDataException(
                                           $"Sandbox path has no parent directory: {input.SandboxPath}");

            Directory.CreateDirectory(destinationDirectory);
            await CopyNewFileAsync(input.HostPath, destinationPath, cancellationToken);
        }
    }

    private static async Task CopyOutputFilesAsync(
        IReadOnlyList<SandboxOutputFile> outputFiles,
        string boxDirectory,
        CancellationToken cancellationToken)
    {
        // Retrieve regular files only; a missing artifact is classified later by its producer.
        foreach (var output in outputFiles)
        {
            var sourcePath = ResolveSandboxPath(boxDirectory, output.SandboxPath);
            var source = new FileInfo(sourcePath);
            source.Refresh();

            if (!source.Exists)
            {
                continue;
            }

            EnsureSandboxOutputHasNoSymbolicLinks(
                boxDirectory,
                sourcePath,
                output.SandboxPath);

            await CopyNewFileAsync(sourcePath, output.HostPath, cancellationToken);
        }
    }

    private static void EnsureSandboxOutputHasNoSymbolicLinks(
        string boxDirectory,
        string sourcePath,
        string sandboxPath)
    {
        // Check every component because a regular file beneath a linked directory can escape the box.
        var relativePath = Path.GetRelativePath(boxDirectory, sourcePath);
        var pathSegments = relativePath.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        var currentPath = boxDirectory;

        for (var index = 0; index < pathSegments.Length; index++)
        {
            currentPath = Path.Combine(currentPath, pathSegments[index]);
            FileSystemInfo entry = index == pathSegments.Length - 1
                ? new FileInfo(currentPath)
                : new DirectoryInfo(currentPath);
            entry.Refresh();

            if (entry.LinkTarget is not null)
            {
                throw new InvalidDataException(
                    $"Sandbox output cannot contain a symbolic link: {sandboxPath}");
            }
        }
    }

    private static async Task CopyNewFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        // Create the destination exclusively and remove a partial copy if transfer fails.
        var destinationCreated = false;

        try
        {
            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            destinationCreated = true;
            await source.CopyToAsync(destination, cancellationToken);
            ApplySafeUnixMode(sourcePath, destinationPath);
        }
        catch
        {
            if (destinationCreated)
            {
                File.Delete(destinationPath);
            }

            throw;
        }
    }

    private static void ApplySafeUnixMode(string sourcePath, string destinationPath)
    {
        // Preserve executability across boxes without copying special or writable group/other bits.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const UnixFileMode executeBits =
            UnixFileMode.UserExecute |
            UnixFileMode.GroupExecute |
            UnixFileMode.OtherExecute;

        var sourceMode = File.GetUnixFileMode(sourcePath);
        var destinationMode =
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.GroupRead |
            UnixFileMode.OtherRead;

        if ((sourceMode & executeBits) != 0)
        {
            destinationMode |= executeBits;
        }

        File.SetUnixFileMode(destinationPath, destinationMode);
    }

    private static string ResolveSandboxPath(string boxDirectory, string sandboxPath)
    {
        // Resolve after validation as defense in depth against paths escaping the box directory.
        var resolvedPath = Path.GetFullPath(Path.Combine(boxDirectory, sandboxPath));
        if (!IsPathInside(boxDirectory, resolvedPath))
        {
            throw new ArgumentException(
                $"Sandbox path escapes the box directory: {sandboxPath}",
                nameof(sandboxPath));
        }

        return resolvedPath;
    }

    private static void ValidateSandboxPath(string sandboxPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxPath);

        if (Path.IsPathFullyQualified(sandboxPath))
        {
            throw new ArgumentException(
                $"Sandbox path must be relative: {sandboxPath}",
                nameof(sandboxPath));
        }

        var normalizedPath = sandboxPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var segments = normalizedPath.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException(
                $"Sandbox path contains an invalid segment: {sandboxPath}",
                nameof(sandboxPath));
        }
    }

    private static string RequireAbsolutePath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);

        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Host path must be absolute.", parameterName);
        }

        return Path.GetFullPath(path);
    }

    private static bool IsPathInside(string parentPath, string candidatePath)
    {
        var parentWithSeparator = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentPath))
                                  + Path.DirectorySeparatorChar;
        var normalizedCandidate = Path.GetFullPath(candidatePath);
        return normalizedCandidate.StartsWith(parentWithSeparator, GetPathComparison());
    }

    private static StringComparison GetPathComparison()
    {
        return OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    private static StringComparer GetPathComparer()
    {
        return OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    }

    private static bool IsSuccessfulExecution(ExecutionResult executionResult)
    {
        return executionResult.Termination == TerminationReason.Exited
               && executionResult.ExitCode == 0;
    }

    private static async Task<ExecutionResult?> CleanupBoxAsync(
        string isolateBinary,
        string workingDirectory,
        uint boxId,
        ExecutionResult? executionResult)
    {
        // Cleanup must outlive caller cancellation; a failure invalidates an otherwise valid result.
        try
        {
            var cleanupResult = await Cli
                .Wrap(isolateBinary)
                .WithArguments(Isolate.Cleanup(boxId))
                .WithWorkingDirectory(workingDirectory)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(CancellationToken.None);

            if (cleanupResult.IsSuccess || executionResult is null)
            {
                return executionResult;
            }

            return AddCleanupFailure(
                executionResult,
                cleanupResult.StandardError,
                $"Isolate failed to clean up box {boxId} (exit code {cleanupResult.ExitCode}).");
        }
        catch (Exception exception)
        {
            if (executionResult is null)
            {
                // Preserve the exception already in flight from initialization or execution.
                return null;
            }

            return AddCleanupFailure(
                executionResult,
                string.Empty,
                $"Isolate could not clean up box {boxId}: {exception.Message}");
        }
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadMetadataAsync(
        string metadataPath,
        CancellationToken cancellationToken)
    {
        // Isolate metadata uses key:value lines, and values such as messages may contain more colons.
        if (!File.Exists(metadataPath))
        {
            return new Dictionary<string, string>();
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in await File.ReadAllLinesAsync(metadataPath, cancellationToken))
        {
            var separatorIndex = line.IndexOf(':');
            if (separatorIndex <= 0)
            {
                continue;
            }

            metadata[line[..separatorIndex]] = line[(separatorIndex + 1)..];
        }

        return metadata;
    }

    private static async Task<CapturedCommandResult> ExecuteCapturedAsync(
        Command command,
        CancellationToken cancellationToken)
    {
        // Drain both child streams while retaining only a bounded amount of untrusted output.
        var standardOutput = new BoundedOutputCapture(MaxCapturedStreamBytes);
        var standardError = new BoundedOutputCapture(MaxCapturedStreamBytes);

        var commandResult = await command
            .WithStandardOutputPipe(PipeTarget.Create(standardOutput.CopyFromAsync))
            .WithStandardErrorPipe(PipeTarget.Create(standardError.CopyFromAsync))
            .ExecuteAsync(cancellationToken);

        return new CapturedCommandResult(
            commandResult,
            standardOutput.GetText(),
            standardError.GetText());
    }

    private static ExecutionResult CreateExecutionResult(
        CapturedCommandResult capturedResult,
        IReadOnlyDictionary<string, string> metadata)
    {
        // Prefer Isolate's resource and status metadata over the wrapper process's coarse exit code.
        var commandResult = capturedResult.CommandResult;
        var status = GetMetadata(metadata, "status");
        var sandboxMessage = GetMetadata(metadata, "message");
        var exitCode = ParseInt32(metadata, "exitcode");
        var exitSignal = ParseInt32(metadata, "exitsig");

        var termination = status switch
        {
            "XX" => TerminationReason.SandboxError,
            _ when metadata.ContainsKey("cg-oom-killed") => TerminationReason.MemoryLimitExceeded,
            "TO" => TerminationReason.TimeLimitExceeded,
            "SG" => TerminationReason.Signaled,
            "RE" => TerminationReason.Exited,
            null when commandResult.IsSuccess => TerminationReason.Exited,
            _ => TerminationReason.SandboxError
        };

        if (termination == TerminationReason.SandboxError && string.IsNullOrWhiteSpace(sandboxMessage))
        {
            sandboxMessage = NonEmptyOrNull(capturedResult.StandardError)
                             ?? $"Isolate exited with code {commandResult.ExitCode}.";
        }

        return new ExecutionResult(
            termination,
            exitCode,
            exitSignal,
            ParseSeconds(metadata, "time"),
            ParseSeconds(metadata, "time-wall", commandResult.RunTime),
            ParseInt64(metadata, "cg-mem") ?? ParseInt64(metadata, "max-rss"),
            capturedResult.StandardOutput,
            capturedResult.StandardError,
            sandboxMessage);
    }

    private static ExecutionResult SandboxFailure(
        string standardOutput,
        string standardError,
        TimeSpan wallTime,
        string message)
    {
        return new ExecutionResult(
            TerminationReason.SandboxError,
            null,
            null,
            TimeSpan.Zero,
            wallTime,
            null,
            standardOutput,
            standardError,
            message);
    }

    private static ExecutionResult AddCleanupFailure(
        ExecutionResult executionResult,
        string cleanupStandardError,
        string cleanupMessage)
    {
        // Preserve the program's data while making the box lifecycle failure the final classification.
        var standardError = string.IsNullOrWhiteSpace(cleanupStandardError)
            ? executionResult.StandardError
            : string.Join(
                Environment.NewLine,
                new[] { executionResult.StandardError, cleanupStandardError }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));

        var sandboxMessage = string.Join(
            " ",
            new[] { executionResult.SandboxMessage, cleanupMessage }
                .Where(value => !string.IsNullOrWhiteSpace(value)));

        return executionResult with
        {
            Termination = TerminationReason.SandboxError,
            StandardError = standardError,
            SandboxMessage = sandboxMessage
        };
    }

    private static string? GetMetadata(
        IReadOnlyDictionary<string, string> metadata,
        string key)
    {
        return metadata.TryGetValue(key, out var value) ? value : null;
    }

    private static int? ParseInt32(
        IReadOnlyDictionary<string, string> metadata,
        string key)
    {
        return int.TryParse(
            GetMetadata(metadata, key),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }

    private static long? ParseInt64(
        IReadOnlyDictionary<string, string> metadata,
        string key)
    {
        return long.TryParse(
            GetMetadata(metadata, key),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }

    private static TimeSpan ParseSeconds(
        IReadOnlyDictionary<string, string> metadata,
        string key,
        TimeSpan fallback = default)
    {
        return double.TryParse(
            GetMetadata(metadata, key),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
            ? TimeSpan.FromSeconds(value)
            : fallback;
    }

    private static string? NonEmptyOrNull(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static void TryDeleteMetadata(string metadataPath)
    {
        // Temporary-file cleanup is best effort and must not replace the execution result.
        try
        {
            File.Delete(metadataPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record CapturedCommandResult(
        CommandResult CommandResult,
        string StandardOutput,
        string StandardError);

    private sealed class BoundedOutputCapture(int maxBytes)
    {
        private readonly MemoryStream _content = new();
        private bool _wasTruncated;

        public async Task CopyFromAsync(Stream source, CancellationToken cancellationToken)
        {
            // Keep draining after the limit so a noisy child cannot block on a full output pipe.
            var buffer = new byte[81920];
            int bytesRead;

            while ((bytesRead = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                var bytesRemaining = maxBytes - (int)_content.Length;

                if (bytesRemaining > 0)
                {
                    var bytesToKeep = Math.Min(bytesRead, bytesRemaining);
                    await _content.WriteAsync(
                        buffer.AsMemory(0, bytesToKeep),
                        cancellationToken);
                }

                if (bytesRead > bytesRemaining)
                {
                    _wasTruncated = true;
                }
            }
        }

        public string GetText()
        {
            // Make truncation explicit to consumers instead of returning apparently complete output.
            var text = Encoding.UTF8.GetString(_content.ToArray());
            return _wasTruncated
                ? text + Environment.NewLine + "[output truncated]"
                : text;
        }
    }
}
