# TrueParser.Abp.Nats

[![NuGet](https://img.shields.io/nuget/v/TrueParser.Abp.EventBus.Nats.svg?style=flat-square&label=TrueParser.Abp.EventBus.Nats)](https://www.nuget.org/packages/TrueParser.Abp.EventBus.Nats)
[![NuGet](https://img.shields.io/nuget/v/TrueParser.Abp.Nats.svg?style=flat-square&label=TrueParser.Abp.Nats)](https://www.nuget.org/packages/TrueParser.Abp.Nats)
[![Build](https://img.shields.io/github/actions/workflow/status/trueparser/trueparser-abp-nats/ci.yml?style=flat-square)](https://github.com/trueparser/trueparser-abp-nats/actions)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg?style=flat-square)](./LICENSE)
[![ABP](https://img.shields.io/badge/ABP-10.x-brightgreen?style=flat-square)](https://abp.io)
[![.NET](https://img.shields.io/badge/.NET-10-purple?style=flat-square)](https://dotnet.microsoft.com)

**NATS JetStream** distributed event bus for the **ABP Framework** — a drop-in replacement for `Volo.Abp.EventBus.RabbitMQ` with minimal migration effort.

---

## Why NATS?

| | RabbitMQ | NATS JetStream |
|---|---|---|
| Latency | ~1ms | ~100µs |
| Throughput | ~50K msg/s | ~10M msg/s |
| Operations overhead | High (exchanges, queues, bindings) | Low (subjects, streams) |
| At-least-once delivery | Yes | Yes |
| Fan-out / wildcard | Via exchanges | Native subject wildcards |
| Cloud-native | Requires plugins | Built-in |

---

## Packages

| Package | Purpose |
|---|---|
| `TrueParser.Abp.Nats` | Connection pool, JetStream context, health checks |
| `TrueParser.Abp.EventBus.Nats` | Distributed event bus implementation |

Most applications only need `TrueParser.Abp.EventBus.Nats` — it pulls in `TrueParser.Abp.Nats` automatically.

---

## Quick Start

### 1. Install

```bash
dotnet add package TrueParser.Abp.EventBus.Nats
```

### 2. Register the module

```csharp
[DependsOn(typeof(TrueParserAbpEventBusNatsModule))]
public class MyModule : AbpModule { }
```

### 3. Configure `appsettings.json`

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

### 4. Use — identical to any ABP event bus

```csharp
// Publish
await _distributedEventBus.PublishAsync(new OrderPlacedEto { OrderId = id });

// Handle
public class OrderPlacedHandler : IDistributedEventHandler<OrderPlacedEto>
{
    public async Task HandleEventAsync(OrderPlacedEto eventData)
    {
        // process...
    }
}
```

---

## Migrating from RabbitMQ

Only 3 things change — your event handlers and publishers are untouched:

```diff
- dotnet add package Volo.Abp.EventBus.RabbitMQ
+ dotnet add package TrueParser.Abp.EventBus.Nats
```

```diff
- [DependsOn(typeof(AbpEventBusRabbitMqModule))]
+ [DependsOn(typeof(TrueParserAbpEventBusNatsModule))]
```

```diff
- "RabbitMQ": { "EventBus": { "ExchangeName": "MyExchange" } }
+ "TrueParser": {
+   "Nats": { "Connections": "nats://localhost:4222" },
+   "EventBus": { "Nats": { "StreamName": "MyAppEvents" } }
+ }
```

---

## Requirements

- NATS Server **2.10+** with JetStream enabled (`nats-server -js`)
- ABP Framework **10.x**
- .NET **10**

---

## Running Tests

```bash
# Start NATS with JetStream
nats-server -js

# Run integration tests
dotnet test test/TrueParser.Abp.EventBus.Nats.Tests
```

---

## Documentation

Full configuration reference, advanced patterns, and architecture details are in the [Wiki](./docs/wiki.md).

---

## License

MIT — see [LICENSE](./LICENSE).

<p align="center">Built by the TrueParser team</p>
