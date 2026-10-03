# TelemoqNet

TelemoqNet is a deliberately bounded IoT honeypot that simulates Telnet and
plaintext MQTT services. It records attacker interactions locally or in Azure
Blob Storage, never executes received commands, and never makes outbound
connections on behalf of an attacker.

See [docs/architecture.md](docs/architecture.md) for the runtime architecture,
MQTT component boundaries, packet flow, safety controls, persistence model, and
test strategy.

## Local operation

The worker targets .NET 10. Restore and build the solution, then run:

```bash
dotnet restore TelemoqNet.slnx
dotnet build TelemoqNet.slnx
DOTNET_ENVIRONMENT=Development \
  dotnet run --project TelemoqNet.Api/TelemoqNet.Api.csproj
```

Telnet listens on port `2323` and MQTT listens on port `1883` by default.
MQTT is a bounded, plaintext simulator accepting protocol levels 3, 4, and 5.
Set `Honeypot:Mqtt:Enabled` to `false` to disable it.

Deploy exposed instances with egress denied, no host filesystem mounts, and
container CPU, memory, and process limits. Supply Azure credentials through
environment variables, user secrets, or an external secret provider; never
place connection strings in tracked JSON.

## Testing

Run the existing Telnet and MQTT integration tests with:

```bash
dotnet test TelemoqNet.IntegrationTests/TelemoqNet.IntegrationTests.csproj
```

The integration tests start the real TCP listeners on ephemeral loopback ports,
send MQTT wire packets directly, and use an in-memory session store. This keeps
the tests deterministic and verifies the honeypot itself without requiring
Docker or a real broker. TestContainers is intentionally not required for the
core suite: a Mosquitto container is useful for optional golden-transcript or
compatibility tests, but it must not be part of the normal offline test run.

For a manual smoke test, start the worker and use any MQTT client:

```bash
mosquitto_sub -h 127.0.0.1 -p 1883 -t 'home/test' -v
mosquitto_pub -h 127.0.0.1 -p 1883 -t 'home/test' -m 'hello'
```

The broker accepts anonymous MQTT 3.x/4/5 connections by default. The
`Honeypot:Mqtt:Enabled`, port, connection limits, and packet limit can be
overridden with environment variables such as
`Honeypot__Mqtt__Port=1884`.
