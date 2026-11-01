# Requirements Traceability

This matrix maps the engineering requirements to the implementation and
verification artifacts. It is intended to be used as the basis for the thesis
requirements and testing chapters.

| ID | Requirement | Implementation | Verification |
|---|---|---|---|
| FR-01 | Accept Telnet connections and provide a device-like login flow. | `TelnetServer`, `TelnetSession`, `GenericLinuxDevice` | `TelnetIntegrationTests.LoginAndCommands_AreCapturedAndLogged` |
| FR-02 | Emulate a bounded BusyBox/Linux command surface. | `GenericLinuxDevice`, `CommandStrategies` | `TelnetIntegrationTests.BusyBoxProbe_ReturnsTerseAppletNotFoundResponse`; `ComponentTests.GenericLinuxDevice_ExecutesOnlySimulatedCommands` |
| FR-03 | Accept bounded MQTT protocol traffic. | `MqttServer`, `MqttSession`, `MqttPacketReader` | `MqttIntegrationTests`; `ComponentTests.PacketReader_HandlesFragmentedAndCoalescedPackets` |
| FR-04 | Support MQTT 3.1.1 clean sessions with QoS 0/1 and explicitly reject unsupported versions and persistence. | `MqttSession`, `MqttPacketWriter` | MQTT integration tests for CONNECT, PUBLISH, SUBSCRIBE, PINGREQ, and DISCONNECT |
| FR-05 | Capture login attempts, commands, protocol events, and bounded payload metadata. | `HoneypotSession`, session recorders in Telnet/MQTT sessions | Telnet and MQTT session assertions; `ComponentTests.SessionSerialization_PreservesStructuredEvents` |
| FR-06 | Persist complete session records in structured form. | `LocalCsvSessionStore`, `AzureBlobSessionStore` using managed identity | `AzureBlobIntegrationTests.StoreAsync_PersistsCompleteSessionAsBlobJson` |
| FR-07 | Enforce connection, packet, line, event, subscription, and queue limits. | `HoneypotOptions`, `MqttOptions`, validators, bounded channels/readers | Oversized input and malformed packet tests; options validator tests |
| FR-08 | Prevent attacker input from executing commands or making outbound requests. | Strategy-based emulator, `VirtualFileSystem`, simulated `wget`/`curl`, no process/network execution APIs | `ComponentTests.GenericLinuxDevice_ExecutesOnlySimulatedCommands`; code review and egress-isolation deployment requirements |
| FR-09 | Keep sessions and virtual filesystem state isolated. | Per-connection session state and virtual filesystem instances | `TelnetIntegrationTests.VirtualFilesystemAndWorkingDirectory_AreSessionLocal`; concurrent-session test |
| FR-10 | Preserve clean cancellation and bounded session lifetime. | Cancellation tokens in servers/sessions; session timeout options | Integration fixture shutdown and timeout-focused test runs |
| NFR-01 | Do not log plaintext passwords. | Structured logging excludes password values | `TelnetIntegrationTests.FailedAuthentication_IsCapturedWithoutLoggingPassword` |
| NFR-02 | Keep the normal test suite deterministic and independent of cloud services. | In-memory session stores; Azure test opt-in; Azurite Testcontainer | Default `dotnet test` run and opt-in Azure test |
| NFR-03 | Keep protocol behavior observable for later analysis. | Stable session schema, event sequence numbers, timestamps, protocol labels | Session serialization and persistence assertions |
| NFR-04 | Fail safely on malformed or oversized input. | Bounded codecs and protocol-error events | `ComponentTests.PacketReader_RejectsOversizedPackets`; Telnet oversized-input test |

## Scope boundaries

The implementation is a low/medium-interaction simulator, not a real shell,
full MQTT broker, or firmware emulator. It deliberately does not execute
commands, retrieve payloads, persist attacker-retained state across restarts,
or support MQTT 3.1, MQTT 5, QoS 2, persistent MQTT sessions, or advanced authentication. These are
explicit safety and scope decisions, not unimplemented production features.
