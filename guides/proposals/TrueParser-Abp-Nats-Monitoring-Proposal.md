# TrueParser.Abp.Nats Monitoring Surface Proposal

## Goal

Expose the minimum read-only NATS / JetStream monitoring surface required by upstream applications such as:

- Control Plane APIs
- Prometheus exporters
- Grafana integrations
- OpenTelemetry collectors
- Internal dashboards

The package must remain output-agnostic.

It should **not** bind to HTTP, Prometheus, Grafana, storage, polling, dashboards, or any exporter.

The package should only expose what NATS already exposes.

---

## Design

`TrueParser.Abp.Nats` should expose only two observability surfaces:

1. NATS.Net's existing OpenTelemetry signals unchanged.
2. A small read-only JetStream monitoring reader returning native NATS.Net models.

No custom monitoring model is required.

---

## 1. OpenTelemetry

Do not create custom telemetry such as:

```csharp
TrueParser.Nats.Meter
TrueParser.Nats.ActivitySource
```

NATS.Net already exposes:

```text
ActivitySource: NATS.Net
Meter:          NATS.Net
```

NATS.Net already emits telemetry for:

- published messages
- consumed messages
- operation duration
- active subscriptions
- reconnects
- sent bytes
- received bytes
- dropped messages
- publish / receive tracing
- trace context propagation

The upstream application should decide how to export these signals.

Examples:

```text
NATS.Net Meter
    -> Prometheus exporter
    -> Prometheus
    -> Grafana
```

```text
NATS.Net ActivitySource + Meter
    -> OpenTelemetry Collector
    -> Grafana / Tempo / other backend
```

The package should not own exporter configuration.

---

## 2. Read-Only JetStream Monitoring Surface

Add exactly one interface:

```csharp
public interface INatsMonitoringReader
{
    ValueTask<AccountInfoResponse> GetAccountInfoAsync(
        string? connectionName = null,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<StreamInfo> GetStreamsAsync(
        string? connectionName = null,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<ConsumerInfo> GetConsumersAsync(
        string streamName,
        string? connectionName = null,
        CancellationToken cancellationToken = default);
}
```

Use native types from:

```csharp
NATS.Client.JetStream.Models
```

Do not introduce package-specific monitoring DTOs.

---

## Implementation

Reuse the existing:

```csharp
IJetStreamContextAccessor
```

Implementation should only delegate to the NATS.Net JetStream APIs.

```csharp
public sealed class NatsMonitoringReader : INatsMonitoringReader
{
    private readonly IJetStreamContextAccessor _contextAccessor;

    public NatsMonitoringReader(IJetStreamContextAccessor contextAccessor)
    {
        _contextAccessor = contextAccessor;
    }

    public async ValueTask<AccountInfoResponse> GetAccountInfoAsync(
        string? connectionName = null,
        CancellationToken cancellationToken = default)
    {
        var js = await _contextAccessor.GetContextAsync(connectionName);
        return await js.GetAccountInfoAsync(cancellationToken);
    }

    public async IAsyncEnumerable<StreamInfo> GetStreamsAsync(
        string? connectionName = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var js = await _contextAccessor.GetContextAsync(connectionName);

        await foreach (
            var stream in js.ListStreamsAsync(
                cancellationToken: cancellationToken))
        {
            yield return stream.Info;
        }
    }

    public async IAsyncEnumerable<ConsumerInfo> GetConsumersAsync(
        string streamName,
        string? connectionName = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var js = await _contextAccessor.GetContextAsync(connectionName);

        await foreach (
            var consumer in js.ListConsumersAsync(
                streamName,
                cancellationToken))
        {
            yield return consumer.Info;
        }
    }
}
```

Register it with DI:

```csharp
context.Services.AddSingleton<INatsMonitoringReader, NatsMonitoringReader>();
```

---

## Exposed Stream State

`StreamInfo.State` already provides:

```text
Messages
Bytes
FirstSeq
LastSeq
NumSubjects
NumDeleted
ConsumerCount
```

This is sufficient for stream-level monitoring.

Example upstream view:

```text
Stream: TrueParserEvents

Messages:      1,293
Bytes:         84 MB
Consumers:     7
```

---

## Exposed Consumer State

`ConsumerInfo` already provides:

```text
StreamName
Name
Delivered
AckFloor
NumAckPending
NumRedelivered
NumWaiting
NumPending
IsPaused
PauseRemaining
Cluster
```

This is sufficient for queue / worker monitoring.

Example:

```text
PdfWorker
  Pending:      122
  AckPending:   4
  Redelivered:  2

GisWorker
  Pending:      0
  AckPending:   1
  Redelivered:  0
```

This allows the upstream application to understand:

- queue backlog
- in-flight work
- redelivery / retry activity
- consumer progress
- paused consumers
- stream storage
- number of consumers

---

## Upstream Usage

The package must not decide how monitoring is exposed.

Example Control Plane API:

```text
GET /api/nats/account
GET /api/nats/streams
GET /api/nats/streams/{stream}/consumers
```

The Control Plane owns:

- HTTP controllers
- authorization
- JSON serialization
- UI
- polling frequency
- caching, if ever required

The package only provides NATS state.

---

## Important Constraint

Do not use consumer-info calls inside the message processing hot path.

NATS.Net documents consumer-info retrieval as a server round trip.

This monitoring reader is intended for:

- on-demand API calls
- dashboard refreshes
- Prometheus-style scrape intervals
- administrative inspection

It must not become part of job execution or acknowledgement logic.

---

## Explicitly Out of Scope

Do not add:

- `NatsMonitoringSnapshot`
- `NatsStreamMetrics`
- `NatsConsumerMetrics`
- derived queue-health states
- backlog severity calculations
- throughput calculations
- polling background services
- persistent monitoring storage
- Prometheus dependencies
- OpenTelemetry exporters
- Grafana-specific code
- controllers
- REST APIs
- dashboards
- caching
- retry/control logic based on monitoring values

Monitoring must remain observational only.

---

## Final Package Shape

```text
TrueParser.Abp.Nats
        |
        +-- NATS.Net ActivitySource
        |       "NATS.Net"
        |
        +-- NATS.Net Meter
        |       "NATS.Net"
        |
        +-- INatsMonitoringReader
                |
                +-- AccountInfoResponse
                +-- StreamInfo
                +-- ConsumerInfo
```

Upstream applications are free to consume this through:

```text
Control Plane API
Prometheus
Grafana
OpenTelemetry
Internal dashboards
CLI tools
```

without `TrueParser.Abp.Nats` depending on any of them.

---

## Required Code Changes

Only:

```text
+ INatsMonitoringReader.cs
+ NatsMonitoringReader.cs
+ DI registration
+ focused unit/integration tests
+ README documentation for the monitoring reader
+ README note documenting NATS.Net Meter / ActivitySource names
```

No other architecture change is required.

## Conclusion

Keep `TrueParser.Abp.Nats` as a thin ABP-facing NATS integration.

Expose native NATS observability rather than building an observability platform.

The package should provide raw, read-only NATS / JetStream state and allow every upstream consumer to decide how that state is presented or exported.
