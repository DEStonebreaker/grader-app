# Ubuntu setup and validation

Use a disposable native Ubuntu x64 or ARM64 VM with systemd and cgroup v2. Setup installs .NET 10
SDK, GCC/G++, a pinned upstream Isolate 2.7 build, its setuid helper and systemd
cgroup service, an Isolate subordinate-ID account, and an unprivileged `judge` user:

```sh
./scripts/setup-ubuntu.sh
```

The package feed must provide `dotnet-sdk-10.0`. Use a supported Ubuntu release
for a long-lived judge. Check the
[Microsoft Ubuntu installation guide](https://learn.microsoft.com/en-us/dotnet/core/install/linux-ubuntu-install)
if your package feed differs. Isolate setup follows the
[upstream build](https://github.com/ioi/isolate) and
[Isolate manual](https://www.ucw.cz/isolate/isolate.1.html).

Setup changes the VM's packages and `/usr/local` Isolate installation. Run it on
a dedicated judge VM. It does not deploy the application or change active releases.
It retains its temporary source tree so the installed revision can be inspected.
For an offline rebuild, set `VALIMARK_ISOLATE_SOURCE` to an existing upstream clone;
the script still checks out the pinned commit. The final preflight runs a sandbox
as judge and fails if namespaces or privileges are unavailable.

Copy the source into a Linux-native directory writable by `judge`, then run:

```sh
sudo -u judge -- bash -c 'cd /path/to/grader-app && ./scripts/validate-linux.sh'
```

The script runs all tests with real-Isolate tests enabled, publishes the CLI for
the native architecture, and grades C/C++ with the example trusted C# driver. It validates JSON
and retains TRX and smoke-test reports in `artifacts/validation`. Tests reserve
box IDs 80–95; published smoke tests use 96 and preflight uses 97. Do not run unrelated Isolate jobs
with those IDs during validation. Successful sandbox tests run as `judge`, not root.

## OrbStack

Inspect existing machines with `orb list`. On Apple Silicon, use a native ARM64
machine for real Isolate tests. OrbStack uses
[Rosetta for amd64 userspace](https://docs.orbstack.dev/architecture); on this
machine the translated amd64 Isolate failed with `clone failed: Function not
implemented`, and a cancellation test also stalled. That environment cannot
establish native x64 sandbox correctness. Native x64 release validation requires
a native x64 Linux host.

For a native VM named `valimark-testing`, run
setup against the macOS shared source tree, then validate a separate native copy:

```sh
orb -m valimark-testing bash /Users/donovan/dev/grader-app/scripts/setup-ubuntu.sh
```

Use a fresh directory beneath `/home/judge` for the source copy, excluding `.git`,
`bin`, `obj`, `artifacts`, and local agent/handoff files. This avoids sharing .NET
intermediate build files between macOS arm64 and Linux x64. Run validation as
`judge` from that native copy. The final CLI and example driver can then be copied
together to another configured host; a framework-dependent release needs .NET 10
runtime there. Published driver builds must match the grader release.

The old `deploy_smoke.sh` described previously was absent from this repository.
These scripts provide local VM setup and verification, not remote deployment or
an automatic release switch.

A fast build/format/test workflow runs on pushes and pull requests. The separate
manual **Native x64 Isolate validation** GitHub Actions workflow prepares a native
Ubuntu hosted runner and executes the full judge-user validation script. It must
be run after the changes are pushed; creating the workflow is not evidence of a
successful native x64 sandbox run.
