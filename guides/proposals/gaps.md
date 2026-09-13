# TrueParserAbpNats — Remaining Hardening Fixes

Repository:

`TrueParser/TrueParserAbpNats`

These are follow-up fixes after the completed Phase 1 hardening work.

## Execution rule

Execute **one numbered item at a time**.

For every item:

```text
reproduce / characterize current behavior
↓
implement only that fix
↓
focused regression test
↓
full existing test suite
↓
live JetStream suite where applicable
↓
build
↓
report result
↓
STOP
```

Do not start the next numbered item automatically.

Do not combine unrelated cleanup.

Do not modify TrueParser Control Plane.


---

# 2. Make Durable Consumer Names Collision-Safe

## Current problem

Current durable consumer names are derived from:

```text
{StreamName}_{ClientName}_{EventName}
```

and then invalid characters are replaced with `_`.

Examples:

```text
Order.Created
Order_Created
```

can both become:

```text
Order_Created
```

Likewise:

```text
billing.eu
billing_eu
```

can sanitize to the same logical identifier.

This creates a possible durable consumer collision.

An existing consumer is currently retrieved by generated durable name without
proving that its stored `FilterSubject` belongs to the event that generated
that name.

A collision can therefore produce silent incorrect routing.

## Required fix

Keep consumer names human-readable but append a deterministic hash derived from
the **unsanitized logical identity**.

For example:

```text
{sanitizedStream}_{sanitizedClient}_{sanitizedEvent}_{shortHash}
```

Hash input should include enough raw identity to make collisions impractical:

```text
StreamName
ClientName
EventName
```

Example:

```text
TrueParserEvents_Billing_Order_Created_7d18bc42
```

Use a deterministic non-random hash.

Do not use:

```text
GetHashCode()
```

because it is not suitable as a stable persisted identifier.

Use an established stable hash such as SHA-256 and truncate to a reasonable
suffix, e.g. 8-12 hexadecimal characters.

Also, when retrieving an existing durable consumer, validate that its critical
identity configuration matches what the event bus expects.

At minimum verify:

```text
FilterSubject == expected subject
AckPolicy == Explicit
```

If an existing durable with that name has incompatible identity configuration,
fail clearly instead of consuming it.

Do not silently modify it.

## Required tests

Add collision regression coverage using event/client names such as:

```text
Order.Created
Order_Created
```

and:

```text
Billing.EU
Billing_EU
```

Assert:

```text
consumer names differ
filters remain correct
both subscriptions receive only their intended events
```

Also test an intentionally incompatible pre-existing durable:

```text
same durable name
wrong FilterSubject
```

Expected:

```text
deterministic initialization failure
```

## Completion criteria

* sanitization cannot collapse distinct logical identities into the same durable;
* generated names remain deterministic across restart;
* existing durable identity is validated;
* existing service-fan-out tests remain green;
* existing same-ClientName replica tests remain green.

STOP after completing and verifying item 2.

---

# 3. Complete `OnAddToOutboxAsync` ABP RabbitMQ Parity

## Current problem

ABP 10.6 RabbitMQ overrides:

```csharp
OnAddToOutboxAsync(
    string eventName,
    Type eventType,
    object eventData)
```

and records non-dynamic CLR event types into its event-type map before delegating
to the base implementation.

The current NATS implementation does not have an equivalent override.

Normal subscriptions may populate `EventTypes`, so this may not break the
common path, but the package's RabbitMQ parity audit explicitly includes
Outbox behavior and should not leave this difference unexplained.

## Required fix

First determine why ABP RabbitMQ performs this registration and verify its
effect against ABP 10.6 `DistributedEventBusBase`.

If applicable to NATS, mirror the RabbitMQ behavior:

```csharp
protected override Task OnAddToOutboxAsync(
    string eventName,
    Type eventType,
    object eventData)
{
    if (eventType != typeof(DynamicEventData))
    {
        EventTypes.GetOrAdd(eventName, eventType);
    }

    return base.OnAddToOutboxAsync(
        eventName,
        eventType,
        eventData);
}
```

Do not copy it blindly if semantic analysis proves it is unnecessary or wrong
for this transport.

If no code change is required, document the exact reason and add a regression
that proves the NATS path has equivalent behavior.

## Required test

Exercise a typed event through:

```text
UoW
→ Outbox
→ transport
→ consumer / Inbox
```

without relying on an unrelated subscription-side registration to establish
the CLR event type.

Verify typed deserialization remains correct.

## Completion criteria

Either:

```text
NATS mirrors ABP's OnAddToOutboxAsync behavior
```

or:

```text
an equivalent NATS behavior is proven and documented
```

No unexplained parity gap may remain.

STOP after completing and verifying item 3.

---

# 5. Finish Public Documentation and Correct Migration Examples

## Current problem

The implementation now requires an explicit:

```text
TrueParser:EventBus:Nats:ClientName
```

and correctly rejects configurations without it.

However at least one RabbitMQ migration example in the Wiki currently omits
this required event-bus `ClientName`.

A third-party developer following that example will receive a startup failure.

The Phase 1 task also still marks the public documentation slice as pending.

## Required fix

Perform a complete documentation audit against the current implementation.

Every configuration example must include the required event-bus identity:

```json
{
  "TrueParser": {
    "Nats": {
      "Connections": "nats://localhost:4222",
      "ClientName": "my-service-connection"
    },
    "EventBus": {
      "Nats": {
        "ClientName": "my-service",
        "StreamName": "MyAppEvents",
        "SubjectPrefix": "MyApp.Events"
      }
    }
  }
}
```

Clearly distinguish:

```text
TrueParser:Nats:ClientName
    = connection/client monitoring identity

TrueParser:EventBus:Nats:ClientName
    = logical distributed-event subscriber identity
```

Document verified behavior for:

```text
ClientName
durable consumer naming
same-service replicas
cross-service fan-out
InitialDeliveryPolicy
Interest retention
Limits retention
Workqueue rejection
Nats-Msg-Id
ABP Inbox
ABP Outbox
tenant propagation
correlation propagation
AckWait
MaxDeliver
BackOff
stream validation
ReplicaCount
MaxAge
```

Explicitly state:

```text
JetStream does not replace ABP Inbox or Outbox.
```

Do not claim exactly-once delivery.

Target guarantee:

```text
transactional ABP Outbox
+
JetStream at-least-once transport
+
stable message identity
+
ABP Inbox idempotency
```

Remove or qualify unsupported benchmark claims if they are not backed by
reproducible evidence.

## Required verification

Search all:

```text
README
docs/
guides/
NuGet-facing package documentation
```

for stale examples and old configuration assumptions.

Every copy-pastable configuration example must be valid against the current
startup validation.

## Completion criteria

* every migration example starts successfully when copied correctly;
* connection `ClientName` and event-bus `ClientName` are not conflated;
* all verified reliability semantics are accurately documented;
* no outdated 2.5.3/current-behavior claims remain in public-facing docs except historical material;
* Phase 1 documentation slice can be marked complete.

STOP after completing and verifying item 5.

---

# 6. Clarify `BackOff` Semantics and Test Contract

## Current problem

The package correctly places configured:

```text
BackOff
```

onto the JetStream consumer configuration.

However handler failures currently execute:

```csharp
await msg.NakAsync();
```

which requests immediate redelivery.

Therefore the configured JetStream `BackOff` controls acknowledgment-timeout
redelivery but should not be described or tested as though it necessarily
delays explicit handler-failure NAK retries.

The current configuration test proves that BackOff reaches JetStream, but it
does not prove delayed handler-failure retry behavior.

## Required fix

Do not change retry behavior unless intentionally approved.

For this task, first decide the intended public contract.

Recommended contract for the current package:

```text
handler failure
    -> immediate NAK/redelivery

Ack timeout
    -> JetStream BackOff schedule

MaxDeliver
    -> bounds total delivery attempts
```

If that is the intended behavior:

* keep `NakAsync()` unchanged;
* make documentation explicit;
* rename/reword tests so they claim only what is actually verified.

For example:

```text
Configured_BackOff_Should_Be_Persisted_To_JetStream_Consumer
```

rather than implying handler exceptions follow that delay sequence.

If delayed handler-failure retry is desired instead, stop and design that as a
separate behavioral enhancement rather than silently changing it here.

## Completion criteria

* public documentation precisely distinguishes NAK redelivery from AckWait/BackOff;
* tests do not overclaim delayed exception retry;
* MaxDeliver behavior remains verified;
* no accidental retry-semantic change.

STOP after completing and verifying item 6.

---

# Final verification after all six items

Only after items 1-6 are individually completed:

```text
dotnet restore
Release build
Roslyn build
broker-free tests
full RUN_NATS_TESTS=true suite
thread-safety suite
pack both NuGet packages
static package-version audit
documentation configuration audit
```

Then update `TASK.md` with actual results.

Do not mark the overall hardening complete before every remaining item is
verified.

Final target:

```text
ABP 10.6                         PASS
NATS.Net 3.2.0                  PASS
consumer identity               PASS
collision-safe durable identity PASS
Outbox identity                 PASS
Inbox dedup                     PASS
tenant through Inbox            PASS
correlation through Inbox       PASS
dynamic Inbox                   PASS
wildcards                       PASS
delivery policy                 PASS
retention/fan-out               PASS
poison-message limits           PASS
stream validation               PASS
ABP parity                      PASS
live release gate               PASS
public documentation            PASS
```
