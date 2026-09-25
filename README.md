# TrueParser.Abp.Nats

[![NuGet](https://img.shields.io/nuget/v/TrueParser.Abp.EventBus.Nats.svg?style=flat-square&label=TrueParser.Abp.EventBus.Nats)](https://www.nuget.org/packages/TrueParser.Abp.EventBus.Nats)
[![NuGet](https://img.shields.io/nuget/v/TrueParser.Abp.Nats.svg?style=flat-square&label=TrueParser.Abp.Nats)](https://www.nuget.org/packages/TrueParser.Abp.Nats)
[![Build](https://github.com/TrueParser/TrueParserAbpNats/actions/workflows/publish-package.yml/badge.svg)](https://github.com/TrueParser/TrueParserAbpNats/actions/workflows/publish-package.yml)
[![License: LGPL v3](https://img.shields.io/badge/License-LGPL%20v3-blue.svg?style=flat-square)](./LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10-purple?style=flat-square)](https://dotnet.microsoft.com)

**NATS JetStream** distributed event bus for the **ABP Framework** — an ABP-compatible alternative to `Volo.Abp.EventBus.RabbitMQ` with minimal application-code migration.

> This project is an independent community library and is not affiliated with, endorsed by, or officially connected to ABP or Volosoft.

---

## Why NATS?

| | RabbitMQ | NATS JetStream |
|---|---|---|
| Latency | Workload-dependent | Workload-dependent |
| Throughput | Workload-dependent | Workload-dependent |
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
      "ClientName": "my-service-connection"
    },
    "EventBus": {
      "Nats": {
        "StreamName": "MyAppEvents",
        "SubjectPrefix": "MyApp.Events",
        "ClientName": "my-service",
        "InitialDeliveryPolicy": "New",
        "AckWait": null,
        "MaxDeliver": null,
        "BackOff": null
      }
    }
  }
}
```

`TrueParser:Nats:ClientName` names the NATS connection for monitoring. The
required `TrueParser:EventBus:Nats:ClientName` is the logical subscriber
identity used for durable consumers. Keep these values independent when a
service has more than one event-bus role. The event-bus identity has no fallback
to the connection name; startup fails when it is missing or blank.

Optional TLS settings can be supplied under `TrueParser:Nats:Tls`. They use
NATS.Net's `NatsTlsOpts` fields and apply to every pooled connection:

```json
{
  "TrueParser": {
    "Nats": {
      "Tls": {
        "CaFile": "/etc/certs/nats-ca.pem",
        "CertFile": "/etc/certs/client.pem",
        "KeyFile": "/etc/certs/client-key.pem",
        "InsecureSkipVerify": false
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

### Delivery and reliability

- New durable consumers use `InitialDeliveryPolicy.New` by default; use `All`
  only when a new service intentionally needs retained history.
- `Interest` retention is the default and preserves fan-out. `Workqueue` is
  rejected because it would break independent subscribers; `Limits` is
  available when bounded stream retention is intentional.
- Handler failures are negatively acknowledged for redelivery. `AckWait` and
  `BackOff` govern acknowledgement-timeout redelivery, while `MaxDeliver`
  bounds attempts when configured.
- Outgoing ABP Outbox IDs are published as `Nats-Msg-Id` and are passed to the
  ABP Inbox for deduplication. Delivery remains at-least-once; JetStream does
  not replace the ABP Outbox or Inbox.
- Durable names include a deterministic hash of the raw stream, subscriber,
  and event identity, preventing punctuation-based name collisions. Existing
  consumers must retain the expected filter subject and explicit ACK policy.
- For tenant-aware Inbox processing, use ETOs implementing `IMultiTenant` so
  ABP restores `TenantId` from the deserialized event. The NATS tenant header
  is transport metadata and is not persisted as a separate Inbox field.

---

## Migrating from RabbitMQ

Only 3 things change - your event handlers and publishers are untouched:

1. Replace `Volo.Abp.EventBus.RabbitMQ` with `TrueParser.Abp.EventBus.Nats`.
2. Swap the module dependency.
3. Move configuration to `TrueParser:Nats` and `TrueParser:EventBus:Nats`.

```csharp
[DependsOn(typeof(TrueParserAbpEventBusNatsModule))]
public class MyModule : AbpModule { }
```

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
      "ClientName": "my-service-connection"
    },
    "EventBus": {
      "Nats": {
        "StreamName": "MyAppEvents",
        "SubjectPrefix": "MyApp.Events",
        "ClientName": "my-service",
        "InitialDeliveryPolicy": "New",
        "AckWait": null,
        "MaxDeliver": null,
        "BackOff": null
      }
    }
  }
}
```

---

## Requirements

- NATS Server **2.10+** with JetStream enabled (`nats-server -js`)
- ABP Framework **10.6.0**
- NATS.Net **3.2.0**
- .NET **10**

---

## Running Tests

```bash
# Start NATS with JetStream
nats-server -js

# Run live integration tests explicitly
RUN_NATS_TESTS=true dotnet test test/TrueParser.Abp.EventBus.Nats.Tests
```

Without a local broker, the ordinary test run remains available; live tests
are gated by `RUN_NATS_TESTS` and are skipped unless it is set to `true`.

---

## Documentation

Full configuration reference, advanced patterns, and architecture details are in the [Wiki](./docs/wiki.md).

---

## Contributing

Thanks for your interest in contributing!

At this stage, this project is primarily maintained for internal use.
We welcome issues, bug reports, and feature suggestions.

Pull requests may not be accepted unless discussed in an issue first.
This helps us keep the architecture aligned with internal requirements.

---

## License

LGPL-3.0 — see [LICENSE](./LICENSE).

<p align="center">Built by the TrueParser team</p>
