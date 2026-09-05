# Deployment scripts

From the repository root, deploy the included smoke-test problem with:

```sh
./scripts/deploy_smoke.sh ops@ubuntu-code-runner
```

The command publishes a framework-dependent `linux-x64` build, uploads it through
`/tmp`, runs server preflight checks, installs a versioned release beneath
`/opt/valimark/releases`, and smoke-tests it as `judge`. The new release becomes
`/opt/valimark/current` only after the smoke test succeeds. The existing
`/opt/judge` directory is not changed.

To supply a different smoke test:

```sh
./scripts/deploy_smoke.sh ops@ubuntu-code-runner \
  path/to/problem.toml \
  path/to/main.cpp \
  cpp \
  path/to/input.txt
```

Use `c` with a C source file or `cpp` with a C++ source file. SSH must work for
the target account, and that account must be allowed to run `sudo`; the script
will request the sudo password in the terminal when needed.

The installer prints the exact rollback command whenever a previous active
release exists.
