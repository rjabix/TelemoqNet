# TelemoqNet Agent Guide

## Project purpose

TelemoqNet is a honeypot that simulates an IoT device and records attacker
interactions. The current implementation exposes a partially implemented
Telnet service. MQTT is planned but is not implemented yet.

The project is intentionally deceptive, but it must remain isolated and safe:
device behavior is simulated, credentials are fake examples, and commands must
not execute on the host operating system.

## Repository structure

This repository currently contains one .NET worker project:

```text
TelemoqNet.slnx
TelemoqNet.Api/
  Program.cs                         Dependency injection and host startup
  Worker.cs                          Hosted-service entry point
  Configuration/
    HoneyPotOptions.cs               Honeypot runtime settings
  Emulation/
    IDeviceEmulator.cs               Device behavior abstraction
    GenericLinuxDevice.cs            Fake BusyBox/Linux device behavior
  Telnet/
    TelnetProtocol.cs                Telnet command and option constants
    TelnetServer.cs                  TCP listener, limits, and timeouts
    TelnetSession.cs                 Negotiation, login, shell, and capture flow
  Logging/
    HoneypotSession.cs               Session and event data model
    ISessionStore.cs                 Session persistence abstraction
    LocalCsvSessionStore.cs          Local CSV persistence and console logging
    AzureBlobSessionStore.cs         JSON persistence in Azure Blob Storage
    LoggingDependencyInjection.cs    Logging and store registration
  appsettings.json                   Production configuration defaults
  appsettings.Development.json       Development logging defaults
```

`bin/` and `obj/` are build output and should not be edited or committed.

## Runtime flow

1. `Program.cs` builds the generic host, binds the `Honeypot` configuration
   section, registers the fake device and session store, and starts `Worker`.
2. `Worker` runs `TelnetServer.RunAsync` until the host cancellation token is
   signaled.
3. `TelnetServer` listens on `Honeypot:TelnetPort`, limits concurrent clients
   with `Honeypot:MaxConnections`, and applies
   `Honeypot:SessionTimeoutSeconds` per connection.
4. `TelnetSession` performs basic Telnet negotiation, captures one login
   attempt, and starts the emulated shell after successful authentication.
5. `GenericLinuxDevice` returns fixed banners, credentials, command responses,
   and prompt text. It must never invoke a real shell or external process.
6. The session is finalized and passed to `ISessionStore`. Development and
   `Local` environments use `LocalCsvSessionStore`, which appends to the
   ignored `data/honeypot-sessions.csv` file and logs the session to the
   console. Other environments use `AzureBlobSessionStore`, which serializes
   sessions as JSON under `yyyy/MM/dd/{SessionId}.json`.
   Application Insights/Azure Monitor telemetry follows the same environment
   boundary and is not registered locally.

## Configuration and local operation

The project targets `net10.0`. The main settings are:

| Setting | Purpose | Default |
| --- | --- | --- |
| `Honeypot:TelnetPort` | TCP port for Telnet | `2323` |
| `Honeypot:MaxConnections` | Concurrent session limit | `100` |
| `Honeypot:SessionTimeoutSeconds` | Per-session timeout | `300` |
| `Honeypot:MaxLineLength` | Maximum captured input line | `2048` |
| `Honeypot:Device` | Selected device profile name | `GenericLinux` |
| `Honeypot:Banner` | Emulated device banner | BusyBox banner |
| `AzureStorage:ConnectionString` | Required Blob Storage credential | unset |
| `AzureStorage:Container` | Session container name | `honeypot-sessions` |
| `LocalStorage:CsvPath` | Optional local session CSV path | `data/honeypot-sessions.csv` |

Do not put connection strings or other secrets in tracked JSON files. Use .NET
user secrets, environment variables, or an external secret provider. The
development launch profile sets `DOTNET_ENVIRONMENT=Development`.

Useful commands:

```bash
dotnet restore TelemoqNet.slnx
dotnet build TelemoqNet.slnx
dotnet run --project TelemoqNet.Api/TelemoqNet.Api.csproj
```

`TelemoqNet.IntegrationTests` exercises the real loopback Telnet server. Its
tests use `TcpClient`, an in-memory session store, and a structured log capture
provider, so they cover wire behavior as well as persisted session state.
Run it with:

```bash
dotnet test TelemoqNet.IntegrationTests/TelemoqNet.IntegrationTests.csproj
```

Add focused integration coverage when changing protocol parsing, session
capture, emulator behavior, or persistence.

## Change guidelines

- Preserve async cancellation and connection limits in network code.
- Keep protocol parsing separate from device emulation; Telnet transport code
  should not grow device-specific command logic.
- Treat all remote input as untrusted. Bound input, avoid host-side command
  execution, and avoid logging secrets unless the capture requirement
  explicitly needs them.
- Keep emulated commands isolated behind `IDeviceCommand` strategies and
  mutate only the in-memory `VirtualFileSystem`; never call the host shell or
  host filesystem from a device command.
- Session records are security-sensitive telemetry. Keep stable timestamps,
  session IDs, remote endpoint data, login attempts, commands, and responses
  backward-compatible when possible.
- Prefer options binding and dependency injection over static configuration.
- Use `ILogger` structured properties rather than interpolated log messages.
- Do not commit generated output, local IDE state, credentials, or captured
  attacker data.

## Planned MQTT support

MQTT should be added as another protocol surface, not folded into the Telnet
classes. A future implementation should have its own listener/session or
connection layer and use shared abstractions for device emulation and session
storage where the event model fits.

Before implementing MQTT, decide and document:

- broker port(s), TLS behavior, and connection limits;
- authentication and topic/subscription simulation;
- packet-size, keep-alive, and per-client timeout limits;
- how MQTT events map into `HoneypotSession` or a versioned event model;
- whether the broker is a deliberately limited simulator or a standards-level
  implementation.

Do not use a real MQTT broker configuration that can bridge, publish to, or
subscribe from external production systems. Keep the simulated broker
self-contained and ensure malformed packets cannot crash the worker.

## Emulated BusyBox command surface

The `GenericLinux` profile supports a deliberately bounded subset of common
BusyBox-style commands. Current simulated commands include:

```text
help echo env pwd ls cat touch mkdir rm
uname id whoami hostname ifconfig ip ps netstat
df free mount dmesg date uptime busybox
wget curl reboot clear true false exit logout
```

`touch` and `mkdir` mutate only the process-local virtual filesystem.
`wget` and `curl` return a simulation message and never make network requests.
`rm` is intentionally non-destructive for now. Unsupported commands return a
simulated `sh: <command>: not found` response.

BusyBox command availability depends on the firmware build and compile-time
applets. Common applets in BusyBox 1.x builds include shell and file tools
(`ash`, `cat`, `cp`, `cut`, `echo`, `grep`, `ln`, `ls`, `mkdir`, `mv`, `rm`,
`sed`, `sh`, `sleep`, `tar`, `touch`, `tr`, `vi`, `wc`), filesystem tools
(`df`, `du`, `find`, `mount`, `umount`), process/system tools (`dmesg`,
`free`, `hostname`, `kill`, `logger`, `ps`, `reboot`, `top`, `uname`, `uptime`),
network tools (`arp`, `ftpget`, `ftpput`, `ifconfig`, `ip`, `netstat`, `ping`,
`telnet`, `tftp`, `wget`) and init/service tools (`init`, `halt`, `poweroff`,
`start-stop-daemon`). The exact list must be treated as profile-specific, not
as a promise that every BusyBox image includes every applet.

## Validation expectations

For code changes, run the narrowest relevant existing validation, normally
`dotnet build TelemoqNet.slnx`. If tests are added, run the relevant test
project as well. For protocol changes, manually verify cancellation, malformed
input, connection-limit behavior, session persistence failures, and clean
shutdown.
