# TrueParser.Abp.Nats

[![NuGet version](https://img.shields.io/nuget/v/TrueParser.Abp.Nats.svg?style=flat-square)](https://www.nuget.org/packages/TrueParser.Abp.Nats)
[![License](https://img.shields.io/badge/license-LGPL--3.0-blue.svg?style=flat-square)](https://github.com/trueparser/trueparser-abp-nats/blob/main/LICENSE)

A high-performance **NATS JetStream** provider for the **ABP Framework** Distributed Event Bus. Built with the modern `NATS.Net` (v2) client for superior speed, reliability, and low-latency messaging.

---

## 🚀 Features

- **Blazing Fast**: Leverages the high-performance `NATS.Net` (v2) client with full `async/await` support.
- **Native JetStream**: Uses JetStream for persistent, durable, and reliable event delivery.
- **Durable Consumers**: Automatically creates and manages durable pull-consumers for all ABP event handlers.
- **Ordered Delivery**: Native support for message ordering and exactly-once delivery semantics via JetStream.
- **Seamless Integration**: Drop-in replacement for RabbitMQ or Kafka in any ABP application.
- **Multi-Tenant Ready**: Automatically handles ABP tenant headers across subjects.
- **Centralized Management**: Configure through ABP's options pattern or `appsettings.json`.

---

## 📦 Installation

Install the NuGet packages:

```bash
dotnet add package TrueParser.Abp.Nats
dotnet add package TrueParser.Abp.EventBus.Nats
```

---

## 🛠️ Configuration

### 1. Register the Module

Add the `TrueParserAbpEventBusNatsModule` to your project dependencies:

```csharp
[DependsOn(typeof(TrueParserAbpEventBusNatsModule))]
public class MyModule : AbpModule
{
    // ...
}
```

### 2. Update `appsettings.json`

Add the NATS configuration section:

```json
{
  "TrueParser": {
    "Nats": {
      "Connections": "nats://localhost:4222",
      "ClientName": "MyProductService"
    },
    "EventBus": {
      "Nats": {
        "StreamName": "TrueParserEvents",
        "SubjectPrefix": "TrueParser.Events"
      }
    }
  }
}
```

---

## 📂 Project Structure

- **`TrueParser.Abp.Nats`**: Base infrastructure for NATS connection management and JetStream accessors.
- **`TrueParser.Abp.EventBus.Nats`**: The ABP Distributed Event Bus implementation.

---

## 🧪 Running Tests

Tests require a local NATS server running with JetStream enabled:

```bash
nats-server -js
```

Then run the integration tests:

```bash
dotnet test test/TrueParser.Abp.EventBus.Nats.Tests
```

---

## ⚖️ License

The `TrueParser.Abp.Nats` library is licensed under the **GNU Lesser General Public License v3 (LGPLv3)**. See the [LICENSE](./LICENSE) file for the full text.

---

<p align="center">
  Built with ❤️ by the TrueParser Team
</p>
