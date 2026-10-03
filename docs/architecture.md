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
3. Accept MQTT protocol levels 3, 4, and 5.
4. Dispatch `PUBLISH`, `SUBSCRIBE`, `UNSUBSCRIBE`, `PINGREQ`, and
   `DISCONNECT`.
5. Enqueue responses on a bounded outbound channel.
6. Persist one `HoneypotSession` when the connection closes.

The session has an absolute lifetime cap. The outbound writer is a single
long-running task, which prevents concurrent writes and ensures responses
queued after CONNECT (for example SUBACK or PINGRESP) are still delivered.

Attacker payloads are never interpreted as commands. Publish events record
bounded metadata such as topic, QoS, payload length, SHA-256, and configured
payload samples rather than executing or forwarding the payload.

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
CONNACK, PUBLISH, PUBACK/PUBREC/PUBREL/PUBCOMP, SUBACK, UNSUBACK, PINGRESP,
and MQTT 5 DISCONNECT.

## Broker state and routing

`BrokerHub` is process-local and singleton-scoped. It maintains:

- Active sessions and their subscriptions.
- A bounded-by-policy retained-message map.
- Matching and delivery to subscribed sessions.

`TopicFilter` implements exact topic levels, `+`, and terminal `#`. Leading
wildcards do not match `$`-prefixed topics. A publish is delivered only to
matching active sessions; no external broker or network is contacted.

Each session receives through a bounded channel. A slow or non-reading client
cannot make the broker allocate unbounded outbound memory or block unrelated
clients. Attacker-retained data is not persisted across process restarts.

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

The local CSV store preserves its existing header and neutralizes
spreadsheet-formula cells. The Azure Blob store initializes its container once
and stores each session as JSON under `yyyy/MM/dd/{SessionId}.json`.

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
- Existing Telnet regression behavior.

Mosquitto/Testcontainers compatibility tests can be added as an optional
golden-transcript layer, but a real broker is not required for the normal test
suite.
