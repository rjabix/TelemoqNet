# Security and Isolation Model

TelemoqNet is designed to observe hostile input without turning that input into
host activity. The following model should be included in the thesis security
and limitations discussion.

## Trust boundaries

```text
Untrusted TCP client
        |
        v
Bounded protocol parser
        |
        v
Per-connection session state -----> Structured session store
        |
        v
Simulated device / in-process broker
```

The client controls bytes received over Telnet or MQTT. It must not control
process creation, host filesystem paths, outbound network destinations,
configuration, or the persistence schema.

## Safety controls

- Telnet input is capped by `Honeypot:MaxLineLength`.
- MQTT remaining lengths, packet bodies, topics, subscriptions, events, and
  outbound queues are bounded by validated options.
- Connection limits are enforced globally and per source IP where configured.
- Session lifetime and CONNECT handshake timeouts prevent abandoned clients from
  consuming resources indefinitely.
- Device commands are dispatched to explicit simulation strategies.
- The virtual filesystem is process-local and path resolution cannot escape its
  simulated root.
- `wget` and `curl` return simulation responses and never create network
  requests.
- No received command is passed to a shell, process launcher, script engine, or
  host filesystem API.
- MQTT payloads are represented by bounded metadata and optional Base64 samples;
  they are not interpreted or forwarded.
- Passwords are captured only where required for session telemetry and are
  excluded from information-level logs.

## Threats and mitigations

| Threat | Mitigation | Residual risk |
|---|---|---|
| Oversized packet or line causes memory exhaustion | Parser and input bounds | Configuration must not be changed to unsafe values |
| Slow client exhausts connections | Global/per-IP limits, timeouts, bounded outbound queues | A sufficiently large permitted client population still consumes resources |
| Command injection into the host | Strategy dispatch and no host command execution | Future emulator features must preserve this boundary |
| Path traversal in simulated commands | Virtual filesystem path normalization and root containment | The virtual filesystem is intentionally not a complete POSIX filesystem |
| Payload-triggered outbound request | Simulated download responses and no outbound client | Deployment egress policy remains a defense in depth |
| Credential leakage in logs | Password exclusion from structured information logs | Session persistence can contain sensitive telemetry and requires access control |
| Malformed MQTT/Telnet input crashes the listener | Protocol-error handling and independent worker supervision | Full protocol conformance is outside scope |
| Spreadsheet formula injection in CSV exports | CSV neutralization in local store | Downstream analysis tools still require normal data-handling controls |

## Deployment assumptions outside application code

The application should run as an unprivileged user in a container or isolated
host, with no host filesystem mounts, no unnecessary Linux capabilities, strict
CPU/memory/process limits, and deny-by-default egress. These controls are
deployment requirements and should be validated separately from the application
tests.
