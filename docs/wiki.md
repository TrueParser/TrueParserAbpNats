# TrueParser.Abp.Nats — Wiki

## Table of Contents

1. [Architecture](#architecture)
2. [Configuration Reference](#configuration-reference)
3. [Advanced Usage](#advanced-usage)
4. [Migrating from RabbitMQ](#migrating-from-rabbitmq)
5. [Outbox / Inbox Pattern](#outbox--inbox-pattern)
6. [Health Checks](#health-checks)
7. [Multi-Tenancy](#multi-tenancy)
8. [Wildcard Subscriptions](#wildcard-subscriptions)
9. [Troubleshooting](#troubleshooting)
10. [Architecture Decisions](#architecture-decisions)

---

## Architecture

### Package layout

```
TrueParser.Abp.Nats               ← core infrastructure
│
├── AbpNatsOptions                 connection URLs, auth, client name
├── NatsConnectionPool             singleton, lazy, named connections
├── JetStreamContextAccessor       thin wrapper → INatsJSContext
└── NatsHealthCheck                IHealthCheck implementation

TrueParser.Abp.EventBus.Nats      ← event bus
│
├── NatsDistributedEventBus        implements DistributedEventBusBase
├── NatsDistributedEventBusOptions stream name, subject prefix, client identity,
│                                  initial delivery policy, retention
├── INatsEventSerializer           pluggable serialization contract
└── DefaultNatsEventSerializer     System.Text.Json implementation
```

### Message flow

```
PublishAsync(OrderPlacedEto)
  └─► DistributedEventBusBase (UoW check, outbox check)
        └─► PublishToEventBusAsync
              └─► js.PublishAsync("MyApp.Events.OrderPlacedEto", bytes)
                    └─► JetStream Stream "MyAppEvents"
                          └─► Durable pull consumer per handler
                                └─► HandleEventAsync(OrderPlacedEto)
```

### Subject naming

Event type `Ordering.OrderPlacedEto` with prefix `MyApp.Events` becomes:

```
MyApp.Events.Ordering.OrderPlacedEto
```

Stream covers all events with a single wildcard subject:

```
MyApp.Events.>
```

### Consumer naming

Consumer names are derived as `{StreamName}_{ClientName}_{EventName}` with all
non-alphanumeric characters replaced by `_`:

```
MyAppEvents_my-service_Ordering_OrderPlacedEto
```

Names are sanitized to satisfy NATS consumer name constraints (alphanumeric, `-`, `_` only).
`ClientName` is the required logical event-bus service identity: replicas with
the same value share durable consumers, while different values receive
independent fan-out copies.

---

## Configuration Reference

### `TrueParser:Nats` — connection options

```json
{
  "TrueParser": {
    "Nats": {
      "Connections": "nats://localhost:4222",
      "ClientName": "my-service",
      "UserName": "",
      "Password": "",
      "Jwt": "",
      "Seed": "",
      "NamedConnections": {
        "analytics": "nats://analytics-nats:4222"
      }
    }
  }
}
```

| Property | Default | Description |
|---|---|---|
| `Connections` | `nats://localhost:4222` | Primary server URL. Comma-separate for clustering. |
| `ClientName` | _(empty)_ | Shown in NATS server monitoring UI |
| `UserName` / `Password` | _(empty)_ | Basic authentication |
| `Jwt` / `Seed` | _(empty)_ | NKey / JWT authentication |
| `NamedConnections` | _(empty)_ | Additional named connections for multi-cluster setups |

### `TrueParser:EventBus:Nats` — event bus options

```json
{
  "TrueParser": {
    "EventBus": {
      "Nats": {
        "StreamName": "MyAppEvents",
        "SubjectPrefix": "MyApp.Events",
        "ClientName": "my-service",
        "ConnectionName": null,
        "InitialDeliveryPolicy": "New",
        "AckWait": null,
        "MaxDeliver": null,
        "BackOff": null,
        "Retention": "Interest",
        "ReplicaCount": 1,
        "MaxAge": null
      }
    }
  }
}
```

| Property | Default | Description |
|---|---|---|
| `StreamName` | `TrueParserEvents` | JetStream stream name |
| `SubjectPrefix` | `TrueParser.Events` | Prefix for all event subjects |
| `ClientName` | _(required)_ | Stable logical service identity used in durable consumer names |
| `ConnectionName` | `null` (uses default) | Which named NATS connection to use |
| `InitialDeliveryPolicy` | `New` | Starting policy for a brand-new durable consumer: `New` or `All` |
| `AckWait` | `null` | Maximum unacknowledged duration before redelivery; null preserves the NATS default |
| `MaxDeliver` | `null` | Maximum delivery attempts; null preserves the native unlimited-redelivery default |
| `BackOff` | `null` | Optional acknowledgment-timeout redelivery delays, in order |
| `Retention` | `Interest` | `Interest` (default) or `Limits`; `Workqueue` is rejected — see below |
| `ReplicaCount` | `1` | Number of stream replicas (use 3 for HA clusters) |
| `MaxAge` | `null` | Max message retention (e.g. `"24h"`) |

#### Retention policies

| Policy | When to use |
|---|---|
| `Interest` | **Default.** Keeps messages until all consumers have ack'd. Correct for fan-out pub/sub. |
| `Limits` | Keeps messages up to size/age/count limits. Use when you want bounded storage regardless of consumers. |
| `Workqueue` | Rejected for the standard event-bus path because it deletes after the first consumer ack and breaks fan-out. |

`Interest` is the supported default for ABP distributed-event fan-out. `Limits`
is supported as an intentional advanced configuration when bounded storage is
more important than retaining messages for currently interested consumers.
`Workqueue` is not supported by this package's standard event-bus path; startup
fails with an actionable configuration error instead of silently changing
fan-out semantics.

#### Initial delivery policy

`InitialDeliveryPolicy` applies only when a durable consumer is created for the
first time. The default `New` policy delivers messages published after that
consumer is created, matching normal RabbitMQ queue creation behavior. An
existing durable consumer is fetched without changing its configuration and
resumes its stored delivery position and backlog.

Set `InitialDeliveryPolicy` to `All` when a service intentionally needs to
replay retained historical messages while creating a new durable consumer.

#### Existing stream validation

At initialization, the event bus creates a missing stream using the configured
`StreamName`, `{SubjectPrefix}.>` subject, `Retention`, `ReplicaCount`, and
`MaxAge`. If the stream already exists, those settings are validated before
consumers start. A mismatch fails startup with the differences listed in the
configuration error; the package does not automatically update or migrate an
existing stream.

#### Redelivery and poison messages

`AckWait`, `MaxDeliver`, and `BackOff` are applied to newly created durable
consumers. Null values preserve the NATS server defaults. `BackOff` is a
sequence of durations and controls acknowledgment-timeout redelivery; the
first backoff value becomes the effective acknowledgment wait. Handler
failures are negatively acknowledged by the event bus and remain eligible for
redelivery, with `MaxDeliver` providing an optional bound.

After a message reaches `MaxDeliver`, JetStream keeps it in the stream. This
package does not create a custom dead-letter queue; applications may use their
own advisory or dead-letter handling when required.

---

## Advanced Usage

### Custom serializer

Implement `INatsEventSerializer` to swap JSON for MessagePack or Protobuf:

```csharp
public class MessagePackNatsSerializer : INatsEventSerializer
{
    public byte[] Serialize(object eventData)
        => MessagePackSerializer.Serialize(eventData);

    public object Deserialize(byte[] value, Type type)
        => MessagePackSerializer.Deserialize(type, value);

    public T Deserialize<T>(byte[] value)
        => MessagePackSerializer.Deserialize<T>(value);
}
```

Register it in your module:

```csharp
context.Services.AddSingleton<INatsEventSerializer, MessagePackNatsSerializer>();
```

### Named connections (multi-cluster)

```json
{
  "TrueParser": {
    "Nats": {
      "Connections": "nats://primary:4222",
      "NamedConnections": {
        "analytics": "nats://analytics-cluster:4222"
      }
    },
    "EventBus": {
      "Nats": {
        "ConnectionName": "analytics"
      }
    }
  }
}
```

### Using `TrueParser.Abp.Nats` standalone (without event bus)

If you only need a managed NATS connection inside an ABP module (e.g. for raw publish/subscribe or KV store access):

```csharp
[DependsOn(typeof(TrueParserAbpNatsModule))]
public class MyModule : AbpModule { }
```

```csharp
public class MyService
{
    private readonly INatsConnectionPool _pool;

    public MyService(INatsConnectionPool pool) => _pool = pool;

    public async Task PublishRawAsync()
    {
        var connection = await _pool.GetAsync();
        await connection.PublishAsync("my.subject", new byte[] { 1, 2, 3 });
    }
}
```

---

## Migrating from RabbitMQ

### Step-by-step

**Step 1 - swap packages**

```bash
dotnet remove package Volo.Abp.EventBus.RabbitMQ
dotnet add package TrueParser.Abp.EventBus.Nats
```

**Step 2 - swap module dependency**

**Before**

```csharp
[DependsOn(typeof(AbpEventBusRabbitMqModule))]
public class MyModule : AbpModule { }
```

**After**

```csharp
[DependsOn(typeof(TrueParserAbpEventBusNatsModule))]
public class MyModule : AbpModule { }
```

**Step 3 - update `appsettings.json`**

**Before**

```json
{
  "RabbitMQ": {
    "Connections": {
      "Default": { "HostName": "localhost" }
    },
    "EventBus": {
      "ClientName": "MyService",
      "ExchangeName": "MyExchange"
    }
  }
}
```

**After**

```json
{
  "TrueParser": {
    "Nats": {
      "Connections": "nats://localhost:4222",
      "ClientName": "my-service"
    },
    "EventBus": {
      "Nats": {
        "StreamName": "MyAppEvents",
        "SubjectPrefix": "MyApp.Events"
      }
    }
  }
}
```

**Step 4 — nothing else.** All `IDistributedEventBus` usages, event handler classes, ETO classes, and `[EventHandler]` attributes stay identical.

### What you get after migration

| Capability | RabbitMQ | NATS |
|---|---|---|
| At-least-once delivery | Yes | Yes |
| Fan-out to multiple services | Yes (exchange) | Yes (Interest retention) |
| Durable subscriptions | Yes (durable queues) | Yes (durable consumers) |
| Wildcard subjects | Via topic exchanges | Native (`*`, `>`) |
| Outbox/Inbox | Yes | Yes |
| UoW integration | Yes | Yes |
| Latency | ~1ms | ~100µs |
| Throughput | ~50K msg/s | ~10M msg/s |
| Ops complexity | High | Low |

---

## Outbox / Inbox Pattern

Works identically to the RabbitMQ implementation. Configure via ABP's standard outbox options:

```csharp
Configure<AbpDistributedEventBusOptions>(options =>
{
    options.Outboxes.Configure(config =>
    {
        config.UseDbContext<MyDbContext>();
    });

    options.Inboxes.Configure(config =>
    {
        config.UseDbContext<MyDbContext>();
    });
});
```

When an outbox is configured:
- `PublishAsync` writes the event to the outbox table inside the same DB transaction
- The outbox worker calls `PublishFromOutboxAsync` which sends to NATS and marks the record processed
- On the subscriber side, `ProcessFromInboxAsync` de-duplicates using the message ID before invoking handlers

No NATS-specific configuration is needed for outbox/inbox — it is entirely driven by ABP's standard options.

---

## Health Checks

`NatsHealthCheck` is registered automatically. Wire it into ASP.NET Core health endpoints:

```csharp
builder.Services.AddHealthChecks()
    .AddCheck<NatsHealthCheck>("nats");
```

The check verifies:
1. The NATS connection state is `Open`
2. JetStream is enabled on the server (calls `GetAccountInfoAsync`)

---

## Multi-Tenancy

Tenant context is propagated automatically via NATS message headers:

| Header | Value |
|---|---|
| `Abp-Tenant-Id` | `CurrentTenant.Id` (when set) |
| `Abp-Correlation-Id` | Current correlation ID |

No configuration needed — this is transparent.

---

## Wildcard Subscriptions

NATS subjects support two wildcard tokens:

| Token | Matches |
|---|---|
| `*` | Exactly one subject token |
| `>` | One or more subject tokens (must be at end) |

Subscribe to dynamic events using a string event name containing wildcards:

```csharp
// Receives UserCreatedEto and UserDeletedEto but not OrderPlacedEto
_distributedEventBus.Subscribe("Identity.User.*", new MyDynamicHandler());
```

The consumer filter subject becomes `MyApp.Events.Identity.User.*` which JetStream evaluates server-side.

---

## Troubleshooting

### `NatsNoRespondersException: No responders`

The published subject does not match any JetStream stream.

- Check that `SubjectPrefix` in options matches the stream's subject filter (`{SubjectPrefix}.>`)
- Check that the stream was successfully created — look for startup log errors containing "Could not ensure NATS JetStream stream exists"
- Verify JetStream is enabled: `nats-server -js` or `jetstream: enabled: true` in the server config

### Message published but handler never fires

- The durable consumer is created asynchronously on first `Subscribe`. For existing consumers (restarts), NATS resumes from the last acknowledged position automatically. For brand-new consumers, `InitialDeliveryPolicy.New` is used by default; set it to `All` to intentionally replay retained historical messages. Note: with `Interest` retention, a message published when **no consumers at all** exist on the stream is discarded immediately by NATS and cannot be recovered regardless of delivery policy.
- If startup reports that `Retention` cannot be `Workqueue`, use `Interest` for fan-out or intentionally choose `Limits`
- Verify the handler class is registered with ABP's DI (`[ExposeServices]` or module registration)

### `NatsJSApiException` with error code 10058 on startup

The stream already exists from a previous run. This is handled automatically — the code catches error 10058 ("stream name already in use") and continues. If you see this error propagating, ensure you are on the latest package version.

### Consumer name rejected by NATS server

Consumer names must be alphanumeric plus `-` and `_`. Event names containing `.` or wildcard characters (`*`, `>`) are automatically sanitized. If you use unusual event names, check the sanitized consumer name in the NATS monitoring dashboard (`http://localhost:8222`).

---

## Architecture Decisions

### Why pull consumers instead of push?

Pull consumers give the subscriber control over fetch rate, providing natural backpressure. Push consumers can overwhelm a slow subscriber. Pull with `ConsumeAsync` in NATS.Net provides a clean async-enumerable API that integrates well with ABP's handler invocation model.

### Why `Interest` retention by default?

ABP's distributed event bus is a pub/sub system where one event fans out to multiple independent services. `Interest` retention keeps a message until every registered consumer has ack'd it, which is the correct semantic. `Workqueue` retention deletes after the first ack, breaking fan-out.

### Why one stream for all events?

A single stream with subject `{prefix}.>` simplifies operations — one stream to monitor, one retention policy, one replica count. Individual event types are routed by subject filter on the consumer level. This mirrors the RabbitMQ approach of one exchange with per-queue bindings.

### Why not `DynamicEventData` in `GetHandlerFactories`?

ABP's `GetHandlerFactories(Type)` is keyed by .NET type. Dynamic events are keyed by string name, so they cannot be retrieved via the type-based lookup when `Type == typeof(DynamicEventData)`. Dynamic event handlers are therefore triggered directly from `DynamicHandlerFactories[eventName]` in the message processing path, bypassing `GetHandlerFactories` for that case only.
