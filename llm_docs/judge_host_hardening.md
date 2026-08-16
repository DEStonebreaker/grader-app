# Judge host hardening runbook

Target: `ubuntu-code-runner`, Ubuntu 24.04, DigitalOcean droplet, x86_64.
Baseline: Lynis 3.1.8, hardening index 65, 2 warnings, 31 suggestions.

Purpose of this box: execute untrusted C, C++, and Python submissions under Isolate.
Threat model is **local privilege escalation from code you deliberately run**, plus
**outbound abuse** if something escapes. That is different from a normal web server,
so some Lynis suggestions are skipped and some controls Lynis never mentions are top priority.

Work through the phases in order. Phase 1 is lockout-sensitive — read each step before running it.

---

## Phase 1 — Immediate exposure (do first)

### 1. Add a DigitalOcean Cloud Firewall

Do this in the DO control panel before touching the host, so there is a filter at the
network edge that survives any mistake you make in `ufw`.

- Inbound: SSH (22) from your IP only, if you have a static address. Otherwise 22 from anywhere.
- Inbound: nothing else yet.
- Outbound: leave permissive for now; tightened in Phase 4.

Attach it to the droplet.

### 2. Turn on the host firewall

Lynis found `iptables module(s) loaded, but no rules active [FIRE-4512]` and
`Chain INPUT (target: ACCEPT)`. Every port is currently reachable.

```bash
ufw status verbose            # expect: inactive
ufw default deny incoming
ufw default allow outgoing    # tightened in Phase 4
ufw allow 22/tcp
ufw --force enable
ufw status verbose            # confirm: deny (incoming), 22 allowed
```

### 3. Create a non-root user

You are currently operating as root. Create an admin user and move your key across.

```bash
adduser --disabled-password --gecos "" ops
usermod -aG sudo ops
mkdir -p /home/ops/.ssh
cp /root/.ssh/authorized_keys /home/ops/.ssh/authorized_keys
chown -R ops:ops /home/ops/.ssh
chmod 700 /home/ops/.ssh
chmod 600 /home/ops/.ssh/authorized_keys
passwd ops                    # needed for sudo; SSH stays key-only
```

Setting a password here is correct. `sudo` requires one, and SSH password auth is
disabled separately in the next step.

**Verify from a second terminal before continuing:**

```bash
ssh ops@<droplet-ip> 'sudo -n true || sudo true; echo OK'
```

Do not proceed until that succeeds.

### 4. Harden SSH

Lynis reported every OpenSSH option as `NOT FOUND` — you are on stock defaults.

Use a drop-in rather than editing `sshd_config`. **Ordering matters:** Ubuntu's
`sshd_config` has `Include /etc/ssh/sshd_config.d/*.conf` near the top, files are read in
lexical order, and in OpenSSH **the first value obtained wins**. DigitalOcean images ship a
`50-cloud-init.conf`. Use a lower number so yours takes precedence.

`/etc/ssh/sshd_config.d/10-judge-hardening.conf`:

```
PermitRootLogin no
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitEmptyPasswords no
AllowUsers ops
MaxAuthTries 3
MaxSessions 4
LoginGraceTime 30
X11Forwarding no
AllowAgentForwarding no
AllowTcpForwarding no
PermitTunnel no
PermitUserEnvironment no
ClientAliveInterval 300
ClientAliveCountMax 2
LogLevel VERBOSE
```

```bash
sshd -t                       # syntax check — must be silent
systemctl restart ssh
```

**Keep your existing root session open** while you open a new one as `ops` to confirm.
If you lock yourself out, DigitalOcean's web console is your recovery path — which is why
you keep `droplet-agent` enabled in Phase 2.

`AllowTcpForwarding no` is deliberate: if a submission ever escapes the sandbox, SSH
port forwarding is a ready-made tunnel out.

### 5. Install fail2ban

```bash
apt-get install -y fail2ban
```

`/etc/fail2ban/jail.local`:

```ini
[DEFAULT]
bantime  = 1h
findtime = 10m
maxretry = 5

[sshd]
enabled  = true
backend  = systemd
```

```bash
systemctl enable --now fail2ban
fail2ban-client status sshd
```

### 6. Reboot

Lynis: `Check if reboot is needed [YES]`. You are running a kernel with pending
security patches, and local kernel privilege escalation is the primary attack you are
defending against on this box. This is not optional.

```bash
reboot
```

Reconnect as `ops` afterwards. Everything below assumes `sudo`.

---

## Phase 2 — Reduce attack surface

### 7. Disable unneeded services

48 enabled services on a box that needs a fraction of that. Each of these is a
root-privileged process reachable from a machine running attacker-supplied code.

```bash
sudo systemctl disable --now \
  ModemManager.service \
  open-vm-tools.service \
  vgauth.service \
  multipathd.service multipathd.socket \
  iscsid.service iscsid.socket \
  udisks2.service \
  tpm-udev.service \
  ubuntu-advantage.service
```

Snapd scored 9.9 (the worst on the box) and has no place on a judge host:

```bash
sudo systemctl disable --now snapd.service snapd.socket snapd.seeded.service
sudo apt-get purge -y snapd
```

**Keep these two**, despite their Lynis scores:

- `droplet-agent` — powers the DO web console. This is your lockout recovery path.
- `do-agent` — droplet metrics. Useful for spotting a runaway submission.

**Keep `unattended-upgrades`.** Lynis flagged its exposure value, but automatic kernel
and security patching is the single highest-value control on this host.

`open-vm-tools` and `vgauth` are VMware guest tools and are simply inert on a KVM droplet.
`multipathd` and `iscsid` are only needed if you attach a DO Block Storage volume — re-enable
them if you ever do.

### 8. Blacklist exotic network protocols

Lynis flagged dccp, sctp, rds, and tipc. These are auto-loadable kernel modules with poor
security histories, reachable by any local process calling `socket()` — including a submission.

```bash
printf 'install %s /bin/false\n' dccp sctp rds tipc \
  | sudo tee /etc/modprobe.d/judge-blacklist.conf
```

Also disable USB storage (`USB-1000`), meaningless on a VPS but free:

```bash
echo 'install usb-storage /bin/false' \
  | sudo tee -a /etc/modprobe.d/judge-blacklist.conf
```

### 9. Apply sysctl hardening

Lynis listed 15 `DIFFERENT` values. Most are network-edge hygiene. The ones that matter
here close local privilege-escalation paths.

`/etc/sysctl.d/99-judge-hardening.conf`:

```ini
# Local privilege escalation surface — the ones that matter for this box
kernel.unprivileged_bpf_disabled = 1
net.core.bpf_jit_harden = 2
kernel.kptr_restrict = 2
kernel.dmesg_restrict = 1
vm.unprivileged_userfaultfd = 0
kernel.yama.ptrace_scope = 1
fs.suid_dumpable = 0
kernel.sysrq = 0
dev.tty.ldisc_autoload = 0

# Filesystem
fs.protected_fifos = 2
fs.protected_regular = 2
fs.protected_hardlinks = 1
fs.protected_symlinks = 1

# Network hygiene (Lynis KRNL-6000)
net.ipv4.conf.all.accept_redirects = 0
net.ipv4.conf.default.accept_redirects = 0
net.ipv6.conf.all.accept_redirects = 0
net.ipv6.conf.default.accept_redirects = 0
net.ipv4.conf.all.send_redirects = 0
net.ipv4.conf.default.send_redirects = 0
net.ipv4.conf.all.rp_filter = 1
net.ipv4.conf.all.log_martians = 1
net.ipv4.conf.default.log_martians = 1
```

```bash
sudo sysctl --system
sudo sysctl kernel.unprivileged_bpf_disabled vm.unprivileged_userfaultfd
```

Unprivileged BPF and `userfaultfd` have both appeared in real kernel exploit chains.
`kptr_restrict=2` stops leaking the kernel pointers those chains need to build.

**Deliberately omitted:** `kernel.modules_disabled = 1`. It is irreversible until reboot
and will block module loading mid-setup. Consider it once the box is stable and unchanging.

---

## Phase 3 — Judge-specific layer

None of this appears in the Lynis report. It matters more than most of what does.

### 10. Create the judge service account

```bash
sudo adduser --system --group --home /opt/judge --shell /usr/sbin/nologin judge
```

A system account with no login shell. The worker runs as this user; Isolate provides
root privileges via its setuid bit, so the worker itself never needs them.

### 11. Contain the disk

Your `/`, `/tmp`, `/var`, and `/home` are one partition with no quotas (Lynis `FILE-6310` ×3).
One submission writing unbounded output fills the disk and takes the whole judge down.

Give Isolate's box root its own size-capped filesystem. A tmpfs is a good fit: fast,
self-cleaning on reboot, and physically unable to exceed its limit.

Add to `/etc/fstab`:

```
tmpfs  /var/local/lib/isolate  tmpfs  rw,nosuid,nodev,mode=755,size=2G  0 0
```

```bash
sudo mkdir -p /var/local/lib/isolate
sudo mount -a
findmnt /var/local/lib/isolate
```

**Do not add `noexec`.** Isolate copies the compiled submission binary into the box and
executes it there; `noexec` breaks every C and C++ submission. `nosuid` and `nodev` are safe.

**Size it against your RAM.** tmpfs consumes RAM as it fills. Budget:
`tmpfs size + (box count × per-box memory limit)` must sit comfortably below total RAM.
On a small droplet, start at 1G and raise it only if you hit ENOSPC.

If you would rather not spend RAM, use an LVM logical volume or a loopback ext4 image
mounted at the same path — same containment, disk-backed.

### 12. Keep swap disabled

Lynis: `Query swap partitions (fstab) [NONE]`. **Leave it that way.** You want a
memory-limited submission to be OOM-killed deterministically. With swap, it thrashes
instead, blows its wall-clock limit, and you get a TLE verdict where you should have
got MLE. Nondeterministic verdicts are the worst failure mode a judge has.

### 13. Install the toolchain and Isolate

This is where the box stops matching Lynis's model of a good server — `Installed
compiler(s) [NOT FOUND]` is about to become false, by design.

```bash
sudo apt-get update
sudo apt-get install -y build-essential python3 git pkg-config \
  libcap-dev libsystemd-dev libseccomp-dev asciidoc-base
sudo git clone https://github.com/ioi/isolate /opt/isolate
cd /opt/isolate
sudo make isolate && sudo make install
```

`libsystemd-dev` and `libseccomp-dev` are hard requirements on Isolate 2.x and are easy
to miss. Without them the build fails with either a pkg-config error for `libsystemd`
or `fatal error: seccomp.h: No such file or directory`. If you hit that, install the
packages and run `sudo make clean` before rebuilding.

### 13a. Allocate the sandbox UID range

Isolate 2.x runs each box as a distinct unprivileged UID and sources that block from
`/etc/subuid`. The shipped config at `/usr/local/etc/isolate` contains
`subid_user = isolate`, and the service will fail to start with
`User isolate not found in /etc/subuid` until that user exists with an allocation.

Check existing allocations first — Ubuntu hands out 65536-wide blocks starting at 100000:

```bash
cat /etc/subuid /etc/subgid
```

Create the user and allocate a clearly separated block:

```bash
sudo adduser --system --group --no-create-home --shell /usr/sbin/nologin isolate
sudo usermod --add-subuids 600000-665535 --add-subgids 600000-665535 isolate
grep isolate /etc/subuid /etc/subgid
```

**Alternative:** skip subuid entirely by editing `/usr/local/etc/isolate`:

```
# subid_user = isolate
first_uid = 60000
first_gid = 60000
num_boxes = 100
```

Confirm those UIDs are free (`getent passwd 60000`) and stay clear of `nobody` at 65534.

### 13b. Start the cgroup keeper

Isolate 2.x requires cgroup v2 and ships a systemd unit for its cgroup keeper, which
establishes `isolate.scope` — a cgroup subtree systemd delegates to Isolate.

```bash
sudo isolate --check-config
sudo systemctl enable --now isolate
systemctl status isolate
```

### 13c. Tune for measurement reproducibility

Same config file. Pin each box to a fixed core — the scheduler otherwise migrates tasks
between CPUs and you pay cache-migration cost, which shows up as timing jitter:

```
box0.cpus = 2
box1.cpus = 3
```

Then audit the host:

```bash
sudo isolate-check-environment
```

It checks ASLR, CPU frequency scaling, turbo boost, transparent huge pages, and SMT.
On a shared-vCPU droplet you will not be able to satisfy all of it — that is the concrete
evidence for moving to dedicated CPU before you trust your time limits.

Note that `CPU frequency scaling ... SKIPPED (not detected)` is not a pass. It means the
guest cannot see or control host CPU frequency, which is the shared-vCPU problem restated.

### 13d. Act on the check-environment failures

**Core file pattern — fix this.** Ubuntu pipes cores to apport. Isolate's docs warn that
pipe-delivered cores bypass the `--core` limit entirely, so a submission segfaulting in a
loop burns host CPU and disk regardless of your sandbox settings.

```bash
sudo systemctl disable --now apport.service
sudo systemctl mask apport.service
echo 'kernel.core_pattern = core' | sudo tee /etc/sysctl.d/99-judge-core.conf
sudo sysctl --system
```

**Transparent hugepages — fix this.** No security downside; THP causes latency spikes and
noisy memory accounting. It lives in sysfs, not sysctl, so it needs a unit to persist.
`/etc/systemd/system/judge-thp.service`:

```ini
[Unit]
Description=Disable transparent hugepages for judge reproducibility
DefaultDependencies=no
After=sysinit.target local-fs.target
Before=isolate.service

[Service]
Type=oneshot
RemainAfterExit=yes
ExecStart=/bin/sh -c 'echo never > /sys/kernel/mm/transparent_hugepage/enabled'
ExecStart=/bin/sh -c 'echo never > /sys/kernel/mm/transparent_hugepage/defrag'
ExecStart=/bin/sh -c 'echo 0 > /sys/kernel/mm/transparent_hugepage/khugepaged/defrag'

[Install]
WantedBy=basic.target
```

```bash
sudo systemctl daemon-reload && sudo systemctl enable --now judge-thp
```

**ASLR — skip, deliberately.** `isolate-check-environment` wants
`kernel.randomize_va_space = 0`, which disables address space randomization host-wide.
That undoes a genuinely valuable exploit mitigation on a box built to run hostile code,
and it contradicts step 9.

The IOI checklist wants it off because contest grading happens at the margin with tight
limits. A LeetCode-style judge with generous limits sees ASLR's timing contribution as
noise next to shared-vCPU jitter. Leave `randomize_va_space` at 2. If you later trace real
variance to it, disable per-process with `setarch -R` rather than host-wide.

### 13e. Check your core count

```bash
nproc
```

If this returns 1, the `box0.cpus` pinning above is meaningless and your box pool is a pool
of one — fine for development, but concurrency is capped at one submission at a time.

Check whether your version restricts the setuid binary to a group. If so, add the
service account:

```bash
ls -l $(command -v isolate)
# if group-restricted, e.g. root:isolate 4750:
sudo usermod -aG isolate judge
```

### 14. Baseline test before hardening further

Establish that Isolate works *before* you change namespace settings, so you know which
change broke it if something does.

```bash
sudo -u judge isolate --cg --box-id=0 --init
echo 'int main(){return 0;}' > /tmp/t.c && gcc -o /tmp/t /tmp/t.c
sudo cp /tmp/t /var/local/lib/isolate/0/box/
sudo -u judge isolate --cg --box-id=0 --meta=/tmp/meta.txt --run -- ./t
cat /tmp/meta.txt
sudo -u judge isolate --cg --box-id=0 --cleanup
```

You want a meta file with `status` absent (clean exit) and a plausible `time` value.

### 15. Restrict unprivileged user namespaces

Historically one of the largest local privesc surfaces on Linux.

Ubuntu 24.04 already ships an AppArmor-based restriction. Check its state first:

```bash
sysctl kernel.apparmor_restrict_unprivileged_userns
```

If it returns `1`, you already have the protection and can stop here. For a harder block:

```bash
echo 'kernel.unprivileged_userns_clone = 0' \
  | sudo tee /etc/sysctl.d/99-judge-userns.conf
sudo sysctl --system
```

**Re-run step 14 immediately.** Isolate runs setuid root and creates its namespaces
*as root*, so it should be unaffected — but verify rather than assume. If the sandbox
fails to initialize, remove `/etc/sysctl.d/99-judge-userns.conf` and rely on the
AppArmor restriction alone.

### 16. Write the worker systemd unit

**This is the step where conventional systemd hardening advice will break you.**

`NoNewPrivileges=yes` sets `PR_SET_NO_NEW_PRIVS`, which prevents setuid binaries from
gaining privileges — Isolate will fail. Worse, systemd turns `NoNewPrivileges` on
*implicitly* when you set any of `SystemCallFilter`, `ProtectKernelTunables`,
`ProtectKernelModules`, `RestrictAddressFamilies`, `RestrictNamespaces`, or
`MemoryDenyWriteExecute`. A unit file that looks well-hardened will silently break the sandbox.

Keep the unit minimal. `/etc/systemd/system/judge-worker.service`:

```ini
[Unit]
Description=Judge worker
After=network-online.target isolate.service
Requires=isolate.service

[Service]
Type=simple
User=judge
Group=judge
WorkingDirectory=/opt/judge
ExecStart=/usr/bin/dotnet /opt/judge/JudgeWorker.dll
Restart=always
RestartSec=5

NoNewPrivileges=false
ProtectHome=true
ProtectSystem=full
ReadWritePaths=/var/local/lib/isolate /opt/judge
PrivateDevices=false
RestrictSUIDSGID=false

[Install]
WantedBy=multi-user.target
```

`ProtectSystem=full` gives read-only `/usr`, `/boot`, and `/etc` without the
`strict` mode that would block Isolate's box writes. `ReadWritePaths` re-opens exactly
what is needed. This addresses Lynis `BOOT-5264` in the only way compatible with the
workload.

The real isolation lives in Isolate's own flags, not in the unit file:

```
--cg --processes=1 --time=2 --wall-time=6 --extra-time=0.5
--cg-mem=262144 --fsize=8192 --stack=65536
--env=PATH=/usr/bin:/bin --env=HOME=/box
--meta=/tmp/meta-N.txt
```

Never pass `--share-net`. Isolate's default is an empty network namespace, which is
what you want.

---

## Phase 4 — Egress and operations

### 17. Restrict outbound traffic

Your highest-value remaining control. Submissions already have no network (empty
namespace), so this is defense-in-depth for a post-escape scenario — where the realistic
monetization is crypto mining, spam relay, or scanning.

```bash
sudo ufw default deny outgoing
sudo ufw allow out 53                    # DNS
sudo ufw allow out 123/udp               # NTP
sudo ufw allow out 80/tcp                # apt
sudo ufw allow out 443/tcp               # apt, git, your API
sudo ufw allow out to <web-tier-ip> port 5432 proto tcp
sudo ufw reload
sudo apt-get update                      # verify apt still works
```

Be honest about what this buys: leaving 80 and 443 open means exfiltration over HTTPS
is still possible. What it blocks is everything else — SMTP, IRC, mining pool ports,
arbitrary high ports — which covers the large majority of automated abuse. A destination
allowlist via an egress proxy is the stricter version; not worth the complexity yet.

Mirror these rules in the DO Cloud Firewall's outbound section so they apply at the edge too.

### 18. Re-run Lynis

```bash
sudo /usr/local/lynis/lynis audit system
```

**Expect the hardening index to fall.** You have installed compilers, which Lynis counts
against you. That drop is the honest signal: your remaining risk now lives in the sandbox
configuration, not the OS baseline. Do not chase the number.

Compare warnings and suggestions against this list rather than the score. `FIRE-4512`
and `KRNL-5830` should both be gone.

### 19. Optional, once you have real users

- **auditd** (`ACCT-9628`) — forensic trail for when something unexplained happens.
  Genuinely useful on this box; skip until the judge is functional.
- **Remote logging** (`LOGG-2154`) — logs an attacker can't erase. Worth it once the
  judge is public.
- **`hidepid` on /proc** — lower value than it looks. Isolate mounts a fresh `/proc`
  inside a new PID namespace, so submissions already can't see host processes.

---

## Deliberately skipped

These are generic-server checklist items that do not map to this threat model.
Skipping them is a decision, not an oversight.

| Lynis finding | Why skipped |
|---|---|
| GRUB password (`BOOT-5122`) | Anyone with your DO console already controls the droplet. |
| Legal banners (`BANN-7126`, `BANN-7130`) | Theatre. No deterrent value. |
| Password aging, min length, pam_cracklib (`AUTH-9230/9262/9286`) | Key-only SSH auth. No passwords to age. |
| Malware scanner (`HRDN-7230`) | Signature scanners on Linux servers are mostly noise. |
| debsums, apt-show-versions (`PKGS-7370/7394`) | Package-management hygiene, not security. |
| Separate `/home` partition (`FILE-6310`) | Handled more directly by the Isolate tmpfs in step 11. |
| `kernel.modules_disabled` | Irreversible until reboot; revisit when the box stops changing. |

---

## Checklist

```
Phase 1 — Immediate
[ ]  1. DO Cloud Firewall attached
[ ]  2. ufw enabled, default deny incoming
[ ]  3. ops user created, key copied, sudo verified
[ ]  4. sshd drop-in 10-judge-hardening.conf, sshd -t clean, new session verified
[ ]  5. fail2ban running, sshd jail active
[ ]  6. Rebooted

Phase 2 — Surface
[ ]  7. Unneeded services disabled, snapd purged, droplet-agent kept
[ ]  8. dccp/sctp/rds/tipc/usb-storage blacklisted
[ ]  9. sysctl hardening applied and verified

Phase 3 — Judge
[ ] 10. judge system account created
[ ] 11. tmpfs mounted at /var/local/lib/isolate, no noexec
[ ] 12. Swap confirmed absent
[ ] 13. Toolchain + Isolate installed (incl. libsystemd-dev, libseccomp-dev)
[ ] 13a. isolate user created with subuid/subgid range
[ ] 13b. isolate --check-config clean, isolate.service running
[ ] 13c. Box CPU pinning set, isolate-check-environment reviewed
[ ] 13d. apport masked + core_pattern fixed, THP unit enabled, ASLR left at 2
[ ] 13e. nproc checked, box pool sized accordingly
[ ] 14. Baseline sandbox test passes
[ ] 15. userns restriction checked, sandbox retested
[ ] 16. judge-worker.service written WITHOUT NoNewPrivileges

Phase 4 — Egress
[ ] 17. Outbound default deny, allowlist applied, apt verified
[ ] 18. Lynis re-run, FIRE-4512 and KRNL-5830 cleared
[ ] 19. auditd / remote logging deferred
```

---