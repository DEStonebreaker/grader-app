namespace grader_app.Tests.TestInfrastructure;

internal sealed class FakeIsolate : IDisposable
{
    private const string Script = """
                                  #!/bin/sh
                                  set -eu

                                  script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
                                  box_id=""
                                  metadata_path=""
                                  stdin_path=""
                                  action=""

                                  for argument in "$@"; do
                                      case "$argument" in
                                          --box-id=*) box_id=${argument#--box-id=} ;;
                                          --meta=*) metadata_path=${argument#--meta=} ;;
                                          --stdin=*) stdin_path=${argument#--stdin=} ;;
                                          --init) action=init ;;
                                          --cleanup) action=cleanup ;;
                                          --run) action=run ;;
                                      esac
                                  done

                                  scenario_file="$script_dir/scenario-$box_id"
                                  if [ -f "$scenario_file" ]; then
                                      scenario=$(cat "$scenario_file")
                                  else
                                      scenario=success
                                  fi

                                  state_dir="$script_dir/boxes/$box_id"
                                  box_dir="$state_dir/box"

                                  if [ "$action" = init ]; then
                                      printf 'init:%s\n' "$box_id" >> "$script_dir/invocations.log"
                                      if [ "$scenario" = init-failure ]; then
                                          printf 'fake init failure\n' >&2
                                          exit 10
                                      fi

                                      mkdir -p "$box_dir"
                                      printf '%s\n' "$state_dir"
                                      exit 0
                                  fi

                                  if [ "$action" = cleanup ]; then
                                      printf 'cleanup:%s\n' "$box_id" >> "$script_dir/invocations.log"
                                      if [ "$scenario" = cleanup-failure ]; then
                                          printf 'fake cleanup failure\n' >&2
                                          exit 11
                                      fi

                                      rm -rf -- "$state_dir"
                                      exit 0
                                  fi

                                  printf 'run-start:%s\n' "$box_id" >> "$script_dir/invocations.log"
                                  for argument in "$@"; do
                                      printf 'arg:%s:%s\n' "$box_id" "$argument" >> "$script_dir/invocations.log"
                                  done

                                  case "$scenario" in
                                      success|cleanup-failure)
                                          printf 'status:RE\nexitcode:0\ntime:0.125\ntime-wall:0.25\ncg-mem:321\n' > "$metadata_path"
                                          printf 'fake stdout'
                                          printf 'fake stderr' >&2
                                          ;;
                                      exited-nonzero)
                                          printf 'status:RE\nexitcode:23\ntime:0.01\ntime-wall:0.02\n' > "$metadata_path"
                                          ;;
                                      time-limit)
                                          printf 'status:TO\nmessage:Time limit exceeded\ntime:1.5\ntime-wall:3\n' > "$metadata_path"
                                          ;;
                                      memory-limit)
                                          printf 'status:RE\ncg-oom-killed:1\ncg-mem:2048\n' > "$metadata_path"
                                          ;;
                                      signaled)
                                          printf 'status:SG\nexitsig:11\n' > "$metadata_path"
                                          ;;
                                      sandbox-error)
                                          printf 'status:XX\nmessage:policy: denied: detail\n' > "$metadata_path"
                                          ;;
                                      wrapper-failure)
                                          printf 'wrapper failed without metadata\n' >&2
                                          exit 12
                                          ;;
                                      malformed-metadata)
                                          printf 'status:RE\nexitcode:not-a-number\ntime:invalid\nmax-rss:invalid\n' > "$metadata_path"
                                          ;;
                                      memory-fallback)
                                          printf 'status:RE\nexitcode:0\nmax-rss:444\n' > "$metadata_path"
                                          ;;
                                      memory-preference)
                                          printf 'status:RE\nexitcode:0\ncg-mem:333\nmax-rss:444\n' > "$metadata_path"
                                          ;;
                                      create-output)
                                          printf 'artifact contents\n' > "$box_dir/result.txt"
                                          printf 'status:RE\nexitcode:0\n' > "$metadata_path"
                                          ;;
                                      create-output-nonzero)
                                          printf 'must not escape\n' > "$box_dir/result.txt"
                                          printf 'status:RE\nexitcode:9\n' > "$metadata_path"
                                          ;;
                                      symlink-output)
                                          ln -s /etc/passwd "$box_dir/result.txt"
                                          printf 'status:RE\nexitcode:0\n' > "$metadata_path"
                                          ;;
                                      symlink-parent-output)
                                          ln -s /etc "$box_dir/out"
                                          printf 'status:RE\nexitcode:0\n' > "$metadata_path"
                                          ;;
                                      copy-executable)
                                          cp "$box_dir/program" "$box_dir/result"
                                          printf 'status:RE\nexitcode:0\n' > "$metadata_path"
                                          ;;
                                      echo-stdin)
                                          cat "$box_dir/$stdin_path"
                                          printf 'status:RE\nexitcode:0\n' > "$metadata_path"
                                          ;;
                                      compiler-success)
                                          printf '#!/bin/sh\nexit 0\n' > "$box_dir/main"
                                          chmod 755 "$box_dir/main"
                                          printf 'status:RE\nexitcode:0\n' > "$metadata_path"
                                          ;;
                                      compiler-error)
                                          printf 'status:RE\nexitcode:1\n' > "$metadata_path"
                                          printf 'syntax error\n' >&2
                                          ;;
                                      compiler-time-limit)
                                          printf 'status:TO\n' > "$metadata_path"
                                          ;;
                                      compiler-memory-limit)
                                          printf 'status:RE\ncg-oom-killed:1\n' > "$metadata_path"
                                          ;;
                                      compiler-sandbox-error)
                                          printf 'status:XX\nmessage:compiler sandbox failed\n' > "$metadata_path"
                                          ;;
                                      compiler-signaled)
                                          printf 'status:SG\nexitsig:9\n' > "$metadata_path"
                                          ;;
                                      compiler-output-missing)
                                          printf 'status:RE\nexitcode:0\n' > "$metadata_path"
                                          ;;
                                      large-output)
                                          cat "$script_dir/large-output.txt"
                                          cat "$script_dir/large-error.txt" >&2
                                          printf 'status:RE\nexitcode:0\n' > "$metadata_path"
                                          ;;
                                      slow-success)
                                          sleep 0.25
                                          printf 'status:RE\nexitcode:0\n' > "$metadata_path"
                                          ;;
                                      block)
                                          : > "$script_dir/run-started-$box_id"
                                          while :; do sleep 1; done
                                          ;;
                                      *)
                                          printf 'unknown fake scenario: %s\n' "$scenario" >&2
                                          exit 13
                                          ;;
                                  esac

                                  printf 'run-end:%s\n' "$box_id" >> "$script_dir/invocations.log"
                                  """;

    public FakeIsolate()
    {
        // Give every test an isolated fake installation so parallel tests cannot share box state.
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Fake Isolate tests require a POSIX shell.");
        }

        RootPath = Path.Combine(Path.GetTempPath(), $"valimark-fake-isolate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(RootPath);
        BinaryPath = Path.Combine(RootPath, "isolate");
        InvocationLogPath = Path.Combine(RootPath, "invocations.log");
        File.WriteAllText(BinaryPath, Script);
        File.SetUnixFileMode(
            BinaryPath,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupRead |
            UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead |
            UnixFileMode.OtherExecute);
    }

    public string RootPath { get; }

    public string BinaryPath { get; }

    public string InvocationLogPath { get; }

    public void SetScenario(uint boxId, string scenario)
    {
        File.WriteAllText(Path.Combine(RootPath, $"scenario-{boxId}"), scenario);
    }

    public string CreateHostFile(string name, string contents)
    {
        var path = Path.Combine(RootPath, name);
        var parent = Path.GetDirectoryName(path)
                     ?? throw new InvalidOperationException("Test path has no parent directory.");
        Directory.CreateDirectory(parent);
        File.WriteAllText(path, contents);
        return path;
    }

    public IReadOnlyList<string> ReadInvocations()
    {
        return File.Exists(InvocationLogPath)
            ? File.ReadAllLines(InvocationLogPath)
            : [];
    }

    public async Task WaitForRunStartAsync(uint boxId, CancellationToken cancellationToken)
    {
        // Wait for the fake child to prove it entered its blocking run before cancellation is sent.
        var markerPath = Path.Combine(RootPath, $"run-started-{boxId}");

        while (!File.Exists(markerPath))
        {
            await Task.Delay(10, cancellationToken);
        }
    }

    public void CreateLargeOutputFiles(int byteCount)
    {
        // Store fixed ASCII payloads so retained byte counts and decoded character counts match.
        File.WriteAllText(Path.Combine(RootPath, "large-output.txt"), new string('o', byteCount));
        File.WriteAllText(Path.Combine(RootPath, "large-error.txt"), new string('e', byteCount));
    }

    public void Dispose()
    {
        // Remove only the unique temporary directory allocated by this test fake.
        try
        {
            Directory.Delete(RootPath, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
