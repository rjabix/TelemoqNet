# Simulated Linux procfs

The GenericLinux profile presents a finite `/proc` tree describing the fictional
HG-200 gateway. It never reads the host's procfs, processes, interfaces, or socket
tables. Each Telnet connection receives its own emulator and clock baseline.

`Honeypot:ProcfsEnabled` controls creation of this entire tree and defaults to
`true` in options and appsettings.json. Set it to `false`, or use the environment
variable `Honeypot__ProcfsEnabled=false`, to disable it. No `/proc` entries or
generated files are registered, and `mount` and `/etc/mtab` omit the proc mount.
Reads, listings, and cd into `/proc` report missing paths. The namespace remains
reserved against writes, touch, and mkdir so commands cannot recreate it.
Other simulated commands (`ps`, `free`, `uptime`, etc.) continue to work.
Restart the application to apply a configuration change.

| Path | Simulated contents |
| --- | --- |
| `/proc/version` | Linux 2.6.36 and the configured fake hostname |
| `/proc/cpuinfo` | Single-core MediaTek/MIPS device at 580 MHz |
| `/proc/meminfo` | Fixed 32768 kB total and 18432 kB free, matching `free` |
| `/proc/uptime` | Elapsed seconds and simulated idle seconds (90% of elapsed) |
| `/proc/cmdline` | Fixed firmware boot arguments |
| `/proc/loadavg` | Fixed load averages, process count, and last fake PID |
| `/proc/mounts` | Writable JFFS2 root and read-only proc mount, matching `mount` and initial `/etc/mtab` |
| `/proc/{1,23,42}/` | init, telnetd, and the shell listed by `ps` |
| `/proc/31/` | mqttd, present only when MQTT is enabled |
| `/proc/self/` | Fixed view of shell PID 42 |
| `/proc/net/dev` | Fake lo/eth0 interfaces with zero traffic counters |
| `/proc/net/route` | Direct eth0 route to 192.168.1.0/24 |
| `/proc/net/tcp` | Fake Telnet port 23 and conditional configured MQTT listener |

Each process directory contains `status`, `comm`, and a NUL-terminated `cmdline`.
All process arguments are fixed; no host credentials or environment data are
included. The shell is represented as running, the daemons as sleeping, and all
fake processes as single-threaded root processes. Network tables never reflect
real attacker connections. IPv4 words use the fake profile's little-endian
representation, independently of the host, and TCP ports are hexadecimal.

The process and network formats follow the relevant conventions described in
the [Linux kernel procfs documentation](https://docs.kernel.org/filesystems/proc.html)
and [TCP table documentation](https://docs.kernel.org/networking/proc_net_tcp.html).
This is a bounded firmware simulation, not a complete Linux procfs implementation.

## Commands and errors

```text
ls /proc
cat /proc/cpuinfo
cat /proc/uptime
cat /proc/self/status
cd /proc/23; cat comm
cat /proc/net/tcp
```

The braces in the path table are shorthand for the listed directories; the shell
does not expand braces or globs. `cat /proc` reads a directory, not its children,
and returns `cat: /proc: Is a directory` with failure status. Unknown files/PIDs
return `No such file or directory`. Directory errors also apply to `busybox cat`.

Creation and modification under `/proc` fail with `Read-only file system`,
including `touch`, `mkdir`, `>`, and `>>`. Paths are normalized before enforcing
protection. `ls -l` displays read-only permissions for proc files and directories.
Ordinary virtual user files remain writable within the existing bounds.

## Clock, bounds, and deliberate simplifications

Uptime starts at zero for each session and advances using a monotonic time source.
`uptime` and `/proc/uptime` share that source; simulated `reboot` resets it without
resetting another session or deleting virtual user files. Numeric proc fields use
invariant formatting. Tests inject a controllable clock instead of sleeping.
`date` uses that clock's UTC time, which is not reset by reboot. `dmesg` includes
the simulated link-up event only after its timestamp has been reached since boot.
The kernel release/build shown by `uname` and `/proc/version` share the same
profile constants; `uname` supports `-s`, `-n`, `-r`, `-v`, `-m`, `-o`, `-a`,
and combinations of those flags, including through `busybox uname`.

The writable root mount matches the existing virtual command behavior. File
creation does not model flash wear or recalculate the fixed `df` disk figures;
memory, load averages, and network counters are also bounded profile facts.
Editing ordinary `/etc` files changes their virtual contents, but does not
reconfigure running services or kernel state. This scope does not simulate
service restarts or full firmware boot scripts.

There are at most four process directories plus one fixed `self` view, ten system
and network files, and three files per process view. Each generated file reserves
4096 characters from the VFS's existing 1048576-character total budget. Generated
registration also counts against the 1024-entry limit. Snapshots larger than a
provider's reservation are rejected, and directory listing materializes bounded
snapshots. Existing per-file and command-response limits still apply.

`self` deliberately represents the session shell instead of creating a fake PID
for every cat invocation; it is not a general symlink engine. No arbitrary
processes, `/proc/sys`, sysctl writes, host inspection, or new shell syntax are
implemented. The mount describes this entire simulated proc tree as read-only.
