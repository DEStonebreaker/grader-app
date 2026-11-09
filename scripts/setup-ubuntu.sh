#!/usr/bin/env bash
# Run on a disposable native Ubuntu VM. Installs privileged Isolate helpers, not a root grader.
set -euo pipefail
if [[ $(uname -s) != Linux ]] || [[ $(uname -m) != x86_64 && $(uname -m) != aarch64 ]]; then
  echo 'This setup targets Ubuntu x64 or ARM64.' >&2
  exit 1
fi
source /etc/os-release
if [[ $ID != ubuntu ]] || [[ ! -f /sys/fs/cgroup/cgroup.controllers ]]; then
  echo 'Ubuntu with cgroup v2 and systemd is required.' >&2
  exit 1
fi
# Pin the exact upstream source rather than compiling a moving branch.
isolate_revision=8f185bb37f3f23e29b33b0c7727c91c13429abe3
sudo apt-get update
sudo env DEBIAN_FRONTEND=noninteractive apt-get install -y \
  dotnet-sdk-10.0 build-essential git libcap-dev libsystemd-dev libseccomp-dev pkg-config python3
build_dir=$(mktemp -d /tmp/valimark-isolate-build.XXXXXX)
echo "Building Isolate in $build_dir"
git clone "${VALIMARK_ISOLATE_SOURCE:-https://github.com/ioi/isolate.git}" "$build_dir/source"
git -C "$build_dir/source" checkout --detach "$isolate_revision"
make -C "$build_dir/source" -j2 isolate isolate-cg-keeper isolate-check-environment default.cf systemd/isolate.service
sudo make -C "$build_dir/source" install
# The pinned Isolate configuration reads this user's automatically allocated
# subordinate UID/GID ranges. A system user would not receive those ranges.
if ! id isolate >/dev/null 2>&1; then
  sudo useradd --no-create-home --shell /usr/sbin/nologin isolate
fi
for ranges in /etc/subuid /etc/subgid; do
  if ! awk -F: '$1 == "isolate" && $3 >= 1000 { found = 1 } END { exit !found }' "$ranges"; then
    echo "Allocate at least 1000 non-overlapping subordinate IDs to isolate in $ranges." >&2
    exit 1
  fi
done
sudo systemctl daemon-reload
sudo systemctl enable --now isolate.service
if ! id judge >/dev/null 2>&1; then
  sudo useradd --create-home --shell /bin/bash judge
fi
/usr/local/bin/isolate --version
# Ensure the installed helper retains its required mode after first execution
# (including translated amd64 execution in OrbStack).
sudo chmod 4755 /usr/local/bin/isolate
dotnet --version
gcc --version | head -1
g++ --version | head -1
systemctl is-active isolate.service
sudo -u judge /usr/local/bin/isolate --cg --box-id=97 --init
if ! sudo -u judge /usr/local/bin/isolate --cg --box-id=97 --time=1 --wall-time=2 --run -- /bin/true; then
  sudo -u judge /usr/local/bin/isolate --cg --box-id=97 --cleanup
  echo 'Isolate execution failed. Use a native-architecture Linux host; translated namespace syscalls may be unsupported.' >&2
  exit 1
fi
sudo -u judge /usr/local/bin/isolate --cg --box-id=97 --cleanup
echo 'Setup complete. Run validation as judge from a writable Linux-native checkout.'
