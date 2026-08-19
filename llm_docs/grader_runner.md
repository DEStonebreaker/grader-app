# grader-app runner

Companion to `judge_host_hardening.md`. That runbook builds the host; this one covers
the application that runs on it — what the sandbox integration does, why it is shaped
the way it is, and how to build, deploy, and run it.

Target: `ubuntu-code-runner`, Isolate 2.x, .NET 10 runtime, worker running as `judge`.

---

## Part 1 — What the code does

### Run lifecycle

`ProblemRunner.Run(executablePath)` grades one submission against one problem:

1. `isolate --cleanup` — a box left behind by a crashed run makes `--init` fail, so
   every run starts by clearing the slate.
2. `isolate --init` — prints the box root on stdout. The working directory is its
   `box` subdirectory; that path is parsed from stdout rather than assumed.
3. Copy the submission binary into the box as `executable.o`, mode 0755.
4. For each test: copy `<name>.in` into the box, run it, parse the meta file, compare.
5. `finally` — delete the temp meta directory and `isolate --cleanup`, so a thrown
   exception still tears the box down.

### Why files get copied into the box

Isolate resolves `--stdin`, `--stdout`, and `--stderr` **inside the sandbox**, not on
the host. Passing a host path like `/var/lib/grader/problems/two-sum/tests/01.in` fails:
that path does not exist in the box's mount namespace. So inputs are copied in and
referenced as bare names (`01.in`), and outputs are read back out of the box directory
afterwards.

`--meta` is the exception — it is written by Isolate on the host side, so it takes a
real host path. It goes to a temp directory created per run and deleted in the `finally`.

### The isolate flag set

Taken from `judge_host_hardening.md` step 16. `Commands.Run` emits:

| Flag | Why |
|---|---|
| `--cg` | cgroup mode; required by Isolate 2.x and by the host's baseline test |
| `--processes=1` | one process. Blocks fork bombs — and any threaded submission |
| `--time` / `--wall-time` | CPU limit from the problem, wall limit at `2× + 1` |
| `--extra-time=0.5` | grace before a hard kill, so `TO` is reported rather than a signal |
| `--cg-mem` | memory limit from the problem, default 256 MB |
| `--fsize=8192` | output cap in KB. Without it one submission can fill the 2 GB box tmpfs |
| `--stack=65536` | stack cap |
| `--env=PATH` / `--env=HOME` | Isolate clears the environment; these put back the minimum |
| `--stdin` / `--stdout` / `--stderr` | box-relative, per the section above |

`--share-net` is deliberately never passed. Isolate's default is an empty network
namespace, which is what a judge wants.

Limits come from `[limits]` in `problem.toml`, falling back to 4 s / 256 MB. `--fsize`
and `--stack` are constants in `Commands.cs`; promote them to config when a problem
needs different values.

### Verdicts

The meta file is `key:value` per line. `status` is **absent on a clean exit** — that
absence is the signal that the program ran to completion and its output is worth
comparing.

| meta `status` | Verdict |
|---|---|
| absent, `diff` exit 0 | `Accepted` |
| absent, `diff` exit 1 | `WrongAnswer` |
| `TO` | `TimeLimitExceeded` |
| `RE`, `SG` | `RuntimeError` |
| anything else (`XX`) | `InternalError` |

`time` and `cg-mem` are read from the same file and reported per test.

Comparison is `diff -u` between the answer file and the box's stdout capture — a crude
byte comparison. `CheckerConfig` in `ProblemConfig.cs` (exact / token / float, tolerance,
whitespace options) is parsed from the toml but **not yet honoured**.

### Exit codes

`grader-app` returns `0` all tests passed, `1` at least one failed, `2` usage error or
wrong OS.

### Output

One line per test — verdict, time, memory. On failure, sample tests additionally print
expected, actual, and a 20-line-capped unified diff. Hidden tests print the verdict only,
so their data is not leaked into logs or into whatever surfaces the output.

---

## Part 2 — Changes made, and why

Everything below was broken or missing in the first pass. Recorded so the same mistakes
are not reintroduced.

### `ProblemRunner` (`Isolate/Runner.cs`)

| Problem | Fix |
|---|---|
| `_problem` was never assigned in the constructor — NRE on the first `_problem.Tests` | assigned from the ctor parameter |
| `Cli.Wrap(runcmd)` passed a whole command string as the executable path | `Cli.Wrap("isolate").WithArguments(string[])` — target and args are separate in CliWrap |
| CliWrap throws on non-zero exit by default, and both `isolate` (RE/TLE) and `diff` (mismatch) exit non-zero on *expected* outcomes | `.WithValidation(CommandResultValidation.None)` on both; the meta file decides the verdict |
| No `--init` or `--cleanup` — the box was never created | cleanup → init → run → cleanup, with cleanup in a `finally` |
| Host paths passed as `--stdin`/`--stdout` | inputs copied into the box, referenced by bare name |
| Box directory hardcoded | parsed from `--init` stdout |
| `Run()` returned `Dictionary<TestMeta, bool>` — `TestMeta` is a record, so two tests with identical fields collide, and a bool cannot express TLE vs RE | returns `List<TestResult>` with verdict, time, memory, expected, actual, diff, message |
| Inherited working directory | pinned to `/` — see below |

### `Commands` (`Isolate/Commands.cs`)

| Problem | Fix |
|---|---|
| Free function at file scope | C# has no free functions; wrapped in `static class Commands` |
| `string.Join(metaFileBase, "-usr-out.txt")` — this is `Join(separator, values)`, so it returns just `"-usr-out.txt"`, silently dropping the path | replaced with normal path construction |
| Returned command strings | returns `string[]` argument lists, which is what CliWrap consumes |
| Missing `--cg`, `--processes`, `--extra-time`, `--fsize`, `--stack`, `--env` | full flag set per the hardening runbook |
| Missing `--run` before `--` | added |
| `$"--time={timeSec}"` uses the current culture — a comma decimal separator makes Isolate reject the limit | formatted with `CultureInfo.InvariantCulture` |

### The working-directory bug

The symptom, running as `judge` from `/home/ops`:

```
Win32Exception (13): An error occurred trying to start process
'/usr/local/bin/isolate' with working directory '/home/ops'. Permission denied
```

.NET's `Process.Start` **chdir()s into the working directory in the forked child**.
The inherited cwd was `/home/ops`, mode 0750 `ops:ops`, which `judge` cannot traverse —
so the chdir fails with EACCES before `isolate` is ever exec'd.

This is why `sudo -u judge g++ ...` worked from the same shell: `sudo` does not chdir,
the child simply inherits the already-open cwd descriptor. Only the .NET fork performs
an explicit chdir.

Both `Cli.Wrap` calls now pin `.WithWorkingDirectory("/")`, so the grader does not care
where it was invoked from.

### `Program.cs`

Rewritten from the print-debugging scaffold into the real entry point: argument parsing,
a Linux guard (`isolate` does not exist on a dev mac), the per-test report, and
meaningful exit codes.

### New files

- `samples/problems/two-sum/` — sample problem: `problem.toml`, `statement.md`,
  a reference `solution.cpp`, and three tests (two sample, one hidden).
- `scripts/patch_judge_host.py` — host patcher, see Part 3.

---

## Part 3 — Running it

### First-time host setup

The hardening runbook installs the submission toolchain in step 13 and then assumes
`/usr/bin/dotnet` exists in step 16 — but never installs it. `scripts/patch_judge_host.py`
closes that gap and audits the Isolate prerequisites the grader depends on.

```bash
# mac
scp scripts/patch_judge_host.py ops@ubuntu-code-runner:/tmp/

# server
sudo python3 /tmp/patch_judge_host.py --check     # audit, changes nothing
sudo python3 /tmp/patch_judge_host.py --dry-run   # preview
sudo python3 /tmp/patch_judge_host.py             # apply
```

It repairs a broken .NET install, installs the .NET **runtime** (not the SDK), prepares
`/opt/judge`, checks the setuid bit and isolate group membership, subuid allocation,
`isolate --check-config`, `isolate.service`, the box tmpfs and its mount options, swap,
and core count — then runs the runbook's step 14 smoke test as `judge`.

Anything it reports as blocking is a runbook step that needs doing by hand. It is
idempotent and safe to re-run.

### Deploying a new build

Build on your machine, never on the judge host. The host has a C/C++ toolchain by
design; it does not need a .NET SDK as well.

```bash
# mac, repo root
dotnet publish grader-app -c Release -o out/judge
scp -r out/judge/* ops@ubuntu-code-runner:/tmp/judge-drop/

# server
sudo cp -r /tmp/judge-drop/* /opt/judge/
sudo chown -R judge:judge /opt/judge
```

Framework-dependent, matching the runbook's `ExecStart=/usr/bin/dotnet /opt/judge/*.dll`.

### Layout on the judge

```
/opt/judge/                       grader-app.dll and its dependencies
/var/lib/grader/problems/         problem sets, one directory per problem
/var/lib/grader/build/            compiled submissions
/var/local/lib/isolate/           box root, size-capped tmpfs (runbook step 11)
```

All owned by `judge`. `/tmp` is deliberately unused — under the host's hardening the
`judge` account cannot reliably write there.

### Grading a submission

```bash
sudo -u judge g++ -O2 -std=c++20 \
  -o /var/lib/grader/build/two-sum \
  /var/lib/grader/problems/two-sum/solution.cpp

sudo -u judge dotnet /opt/judge/grader-app.dll \
  /var/lib/grader/problems/two-sum/problem.toml \
  /var/lib/grader/build/two-sum \
  0
```

Arguments: `<problem.toml> <executable> [box-id]`. Box id defaults to 0.

Box ids must stay within the pool configured in `/usr/local/etc/isolate`. The runbook
pins `box0.cpus` and `box1.cpus`, so ids 0 and 1 are the reproducible ones. Concurrent
runs must use different ids — a box is not safe to share.

Compile as `judge`, into a directory `judge` owns. Compiling as another user leaves a
binary the grader cannot copy into the box.

### Adding a problem

```
<problem-id>/
  problem.toml
  statement.md
  tests/
    01.in   01.ans
    02.in   02.ans
```

`problem.toml`:

```toml
[problem]
id = "two-sum"
title = "Two Sum"
revision = 1

[limits]
time_sec = 1.0
cg_memory_kb = 262144

[checker]
type = "token"          # parsed, not yet enforced

[[tests]]
name = "01"
input = "tests/01.in"
answer = "tests/01.ans"
sample = true
```

`input` and `answer` are relative to the toml's own directory. `name` is used for the
in-box filenames, so keep it short and filesystem-safe. `sample = true` means the test's
data may be shown on failure; omit it and the test reports its verdict only.

Ship it with `scp -r` into `/var/lib/grader/problems/` and `chown -R judge:judge`.

---

## Part 4 — Troubleshooting

| Symptom | Cause |
|---|---|
| `Error: [/usr/lib/dotnet/host/fxr] does not exist` | Broken .NET install — the muxer without a runtime, usually left behind when runbook step 7 purged snapd. Run the patch script |
| `Win32Exception (13) ... working directory ... Permission denied` | Fixed in code by pinning cwd to `/`. If it recurs, the binary itself is unreadable by `judge` |
| `isolate --init failed` | Check `isolate.service` is active, the subuid allocation exists (step 13a), and `judge` is in the isolate group if the setuid binary is group-restricted (step 13e) |
| `ld: cannot open output file ... Permission denied` | Compiling as `judge` into a directory it does not own. Build into `/var/lib/grader/build` |
| Every test `RuntimeError`, empty message | Usually `--processes=1` against a threaded runtime, or a missing shared library — Isolate's box has no `/usr/lib` beyond what it mounts |
| Every test `WrongAnswer` with an empty actual | The program wrote nothing. Check the `.err` capture surfaced in `Message` |
| `InternalError` on every test | Isolate configuration problem, not the submission. Run `sudo isolate --check-config` |

---

## Part 5 — Known gaps

- **Checker config is ignored.** `type`, `tolerance`, and the whitespace options are
  parsed and then unused; comparison is always `diff -u`. Token and float checkers are
  the next real piece of work.
- **No MLE verdict.** An OOM kill arrives as `SG` and is reported as `RuntimeError`.
  Isolate's meta file carries enough to tell them apart.
- **No compile step.** The grader takes an already-compiled binary. Compilation, its
  own sandboxing, and a `CompileError` verdict are not implemented.
- **`--processes=1` is hardcoded.** Fine for single-threaded C and C++; it will need to
  be per-language once a threaded runtime is added.
- **One box, sequentially.** No pool, no concurrency. `nproc` on the droplet caps how
  much that would buy anyway.
- **No worker service.** The runbook's `judge-worker.service` (step 16) has nothing to
  run yet — `grader-app` is a one-shot CLI, not a queue consumer.