# TelemoqNet Architecture

TelemoqNet is a deliberately bounded honeypot. It exposes simulated Telnet and
MQTT services, records attacker interactions, and never executes received
commands or makes outbound network requests on behalf of an attacker.

## Runtime composition

`Program.cs` builds the .NET worker host and registers:

- `HoneypotOptions` for the existing Telnet service.
- `MqttOptions` for the MQTT service, including independent validation and
  limits.
- `TelnetServer` and `MqttServer` as `IProtocolServer` implementations.
- `BrokerHub` as the singleton in-memory MQTT routing component.
- `ISessionStore`, using the local CSV store in Development/Local environments
  and Azure Blob Storage elsewhere.
- `Worker`, which supervises every protocol server independently.

`Worker` runs each registered protocol server in its own restart loop. A
recoverable failure in one listener does not stop the other listener. Shutdown
propagates the host cancellation token to all listeners and their active
sessions.

## MQTT component boundaries

```text
Worker
  └── MqttServer
        ├── global connection limiter
        ├── per-source-IP limiter
        └── MqttSession (one per TCP connection)
              ├── MqttPacketReader
              ├── MqttPacketWriter
              ├── session event recorder
              └── BrokerHub
                    ├── topic-filter matching
                    ├── retained message store
                    ├── `$SYS` telemetry counters
                    └── bounded per-session outbound channels
```

### `MqttServer`

`MqttServer` listens on plaintext MQTT port 1883 by default. It accepts TCP
connections, applies both global and per-source-IP limits, and creates one
`MqttSession` per accepted client. Rejected clients are closed before a session
is created. Connection slots are released when the session task finishes.

The server is disabled by `Honeypot:Mqtt:Enabled`. TLS, MQTT-over-WebSocket,
MQTT-SN, bridging, and outbound connections are intentionally not implemented.

### `MqttSession`

An MQTT session owns the protocol state for one TCP connection:

1. Read packets incrementally from the network stream.
2. Require `CONNECT` before connected-state packets.
3. Accept MQTT 3.1.1 (protocol level 4), with strict CONNECT field validation.
   Refuse other versions and requests for persistent sessions.
4. Dispatch `PUBLISH`, `SUBSCRIBE`, `UNSUBSCRIBE`, `PINGREQ`, and
   `DISCONNECT`.
5. Enqueue responses on a bounded outbound channel.
6. Persist one `HoneypotSession` when the connection closes.

The session has an absolute lifetime cap and enforces the negotiated keepalive
at 1.5 times its interval (zero disables keepalive). Only complete control packets
refresh that deadline. Unexpected disconnects publish a validated QoS 0/1 Last Will;
a valid DISCONNECT suppresses it.

QoS 1 deliveries allocate nonzero per-client packet IDs and retain them until PUBACK.
`MaxInflightMessages` bounds that state; exhaustion closes the slow connection.
Clean sessions discard pending state on disconnect; retransmission across reconnects
is not supported. QoS 2 publishes and Wills are rejected; subscriptions requesting
QoS 2 may be granted QoS 1.

The session has an absolute lifetime cap. The outbound writer is a single
long-running task, which prevents concurrent writes and ensures responses
queued after CONNECT (for example SUBACK or PINGRESP) are still delivered.
The CONNECT handshake must complete within `ConnectTimeoutSeconds`; configured
username/password credentials are checked when anonymous access is disabled.

Attacker payloads are never interpreted as commands. Publish events record
bounded metadata such as topic, QoS, payload length, SHA-256, and configured
payload samples (Base64, capped by `PayloadCaptureBytes`) rather than executing
or forwarding the payload. Attacker-retained messages are routed to active
subscribers but are not retained across subscriptions unless
`GlobalAttackerRetainedEnabled` is enabled.

## Packet codec

`MqttPacketReader` is an incremental, allocation-bounded reader:

- It buffers incomplete packets until the complete remaining length is
  available.
- It validates packet types and fixed-header flags before dispatch.
- It limits the MQTT remaining-length field before allocating the packet body.
- It rejects malformed remaining lengths and packets over
  `Honeypot:Mqtt:MaxPacketSize`.
- It returns `NeedMoreData`, `Packet`, or `ProtocolError` rather than throwing
  protocol errors into the listener.

`MqttPacketWriter` emits the response packets used by the simulator, including
MQTT 3.1.1 CONNACK, PUBLISH, PUBACK, SUBACK, UNSUBACK, and PINGRESP.

## Broker state and routing

`BrokerHub` is process-local and singleton-scoped. It maintains:

- Active sessions and their subscriptions.
- Separate trusted-profile and attacker-retained maps, each capped by
  `MaxRetainedMessages`; new topics are not retained once the respective map is full.
- Matching and delivery to subscribed sessions.

`TopicFilter` implements exact topic levels, `+`, and terminal `#`. Leading
wildcards do not match `$`-prefixed topics. A publish is delivered only to
matching active sessions; no external broker or network is contacted. Overlapping
filters produce one live delivery at the highest matching granted QoS. SUBACK
contains one result per requested filter; resubscription replaces its settings.
Live deliveries clear RETAIN, while subscription replay sets it and preserves the
minimum of stored and granted QoS. Empty retained publications remove the entry.

`SysTopicPublisher` publishes retained broker telemetry at
`Honeypot:Mqtt:SysIntervalSeconds`. It exposes version, monotonic uptime,
connected and total clients, message counts, and byte counts under
`$SYS/broker/...`. Explicit `$SYS/#` subscriptions receive these messages;
ordinary `#` subscriptions do not. At startup and on each interval it also
publishes simulated SmartHome readings under `home/livingroom/...` and gateway
metadata under `home/gateway/...`. The fictional HG-200 model and firmware identity
agree with Telnet’s `/etc/device-release`. Trusted profile state is independent of
attacker retention and cannot be overwritten by remote retained publications.

Each session receives through a bounded channel. A slow or non-reading client
cannot make the broker allocate unbounded outbound memory or block unrelated
clients. A full queue closes the connection with `outbound_queue_full` instead of
silently dropping protocol packets. Attacker-retained data is not persisted across process restarts.

## Session capture and persistence

MQTT uses the existing `HoneypotSession` schema without adding MQTT-specific
columns. Each TCP connection produces one record with:

- `Protocol = "mqtt"`.
- `DeviceProfile` set to the configured broker profile.
- `LoginAttempts`/authentication fields where applicable.
- `Events` containing protocol and attacker actions.

Event names include `connection.opened`, `connection.closed`,
`protocol.error`, `mqtt.connect`, `mqtt.subscribe`, `mqtt.publish`,
`mqtt.unsubscribe`, `mqtt.pingreq`, and `mqtt.disconnect`. Event details are
compact strings containing sanitized identifiers and bounded metadata.

Telnet also records `payload.download_attempt`. Passive lexical inspection in
the emulator recognizes downloader URL operands without executing shell syntax.
The optional `SessionEvent.DownloadAttempt` object contains `Tool`, `Url`,
`Executed=false`, and `Downloaded=false`. Existing event fields, schema version 2,
and CSV columns remain unchanged. Raw commands and responses are still captured.
This evidence describes attempted retrieval only; it contains no downloaded binary.

The local CSV store preserves its existing header and neutralizes
spreadsheet-formula cells. The Azure Blob store uses the configured blob service
URI with `DefaultAzureCredential` and stores each session as JSON under
`yyyy/MM/dd/{SessionId}.json`. The target container is provisioned by
infrastructure; the application only writes blobs and does not create
containers at runtime.

## Configuration and safety controls

MQTT limits are under `Honeypot:Mqtt` and validated at startup. Important
controls include:

- `MaxConnections` and `MaxConnectionsPerIp`.
- `MaxPacketSize`.
- `MaxSubscriptionsPerSession`.
- `MaxEventsPerSession`.
- `OutboundQueueLength`.
- `MaxSessionLifetimeSeconds`.
- `PayloadCaptureBytes`.

Deployments should deny egress, avoid host filesystem mounts, and apply CPU,
memory, and process limits. Secrets must be supplied through environment
variables, user secrets, or an external secret provider.

## Verification architecture

`TelemoqNet.IntegrationTests` starts the real MQTT TCP listener on an ephemeral
loopback port and uses an in-memory `ISessionStore`. Tests send raw MQTT wire
packets and assert both network responses and persisted events. This keeps the
core suite deterministic and Docker-independent.

The suite covers:

- CONNECT, PINGREQ, and DISCONNECT.
- SUBSCRIBE and publish routing isolation.
- Malformed fixed headers.
- Session event preservation for CONNECT, SUBSCRIBE, PUBLISH, PINGREQ, and
  DISCONNECT.
- MQTT listener startup logging.
- Coalesced MQTT packets, configured credentials, bounded payload samples, and
  component behavior such as packet decoding, topic matching, filesystem
  resolution, validation, and session serialization.
- Existing Telnet regression behavior.

The MQTT implementation is intentionally a bounded protocol subset rather than
a complete broker. It supports MQTT 3.1.1 clean sessions, QoS 0/1, subscriptions,
retained/system topics, simulated device readings, and bounded malformed input
handling. MQTT 3.1, MQTT 5, QoS 2, persistent MQTT sessions, and authentication
mechanisms beyond username/password are out of scope.

Mosquitto/Testcontainers compatibility tests can be added as an optional
golden-transcript layer, but a real broker is not required for the normal test
suite.
