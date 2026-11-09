#!/usr/bin/env bash
# Run as the unprivileged judge user on a configured native Linux host.
set -euo pipefail
if [[ $(uname -s) != Linux || $(id -u) == 0 ]]; then
  echo 'Validation requires Linux and an unprivileged user.' >&2
  exit 1
fi
case $(uname -m) in
  x86_64) runtime=linux-x64 ;;
  aarch64) runtime=linux-arm64 ;;
  *) echo 'Supported architectures: x64 and ARM64.' >&2; exit 1 ;;
esac
cd "$(dirname "$0")/.."
isolate_path=${VALIMARK_ISOLATE_PATH:-/usr/local/bin/isolate}
[[ -x $isolate_path ]] || { echo "Missing Isolate executable: $isolate_path" >&2; exit 1; }
export VALIMARK_ISOLATE_PATH="$isolate_path"
export VALIMARK_RUN_ISOLATE_TESTS=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
"$isolate_path" --cg --box-id=97 --init
if ! "$isolate_path" --cg --box-id=97 --time=1 --wall-time=2 --run -- /bin/true; then
  "$isolate_path" --cg --box-id=97 --cleanup
  echo 'Isolate cannot execute on this host. Check privileges and native architecture support.' >&2
  exit 1
fi
"$isolate_path" --cg --box-id=97 --cleanup
mkdir -p artifacts/validation
uname -a
cat /etc/os-release
dotnet --version
"$isolate_path" --version
gcc --version | head -1
g++ --version | head -1
# A missing/unusable Isolate must cause failures, never a silently skipped real lane.
dotnet test grader-app.sln -c Release --logger 'trx;LogFileName=linux.trx' --results-directory artifacts/validation
dotnet publish grader-app/grader-app.csproj -c Release -r "$runtime" --self-contained false -o "artifacts/$runtime"
cli="$PWD/artifacts/$runtime/grader-app"
"$cli" grade examples/sum examples/sum/main.cpp --box-id 96 | tee artifacts/validation/cpp.txt
"$cli" grade examples/sum examples/sum/main.c --box-id 96 --format json > artifacts/validation/c.json
python3 - <<'PY'
import json
from pathlib import Path
report = json.loads(Path('artifacts/validation/c.json').read_text())
assert report['verdict'] == 'Passed', report
assert report['exitCode'] == 0
assert len(report['cases']) == 2
assert all(case['verdict'] == 'Passed' for case in report['cases'])
print('Published C/C++ CLI and trusted driver smoke tests passed.')
PY
