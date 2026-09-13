# TrueParserAbpNats — ABP 10.6 Compatibility and Reliability Hardening

Repository:

`https://github.com/TrueParser/TrueParserAbpNats`

## Compatibility:
We dont need comaptibility. its a bran new package.

## Objective

Harden `TrueParser.Abp.EventBus.Nats` into a reliable, reusable NATS JetStream transport for ABP Framework 10.6 that can replace `Volo.Abp.EventBus.RabbitMQ` with the **smallest possible application-code change**.

The target consumer migration must remain approximately:

```csharp
// Before
[DependsOn(typeof(AbpEventBusRabbitMqModule))]

// After
[DependsOn(typeof(TrueParserAbpEventBusNatsModule))]
```

Existing application code must continue using:

```csharp
IDistributedEventBus
IDistributedEventHandler<T>
AbpDistributedEventBusOptions
IHasEventOutbox
IHasEventInbox
```

Do not redesign ABP's event model.

Do not move inbox/outbox responsibilities into JetStream.

JetStream is the durable broker transport.

ABP Outbox remains responsible for the database -> broker transactional boundary.

ABP Inbox remains responsible for broker redelivery -> application-side idempotency.

The implementation should remain structurally close to ABP 10.6's `RabbitMqDistributedEventBus` wherever behavior is not inherently broker-specific.

---

# 1. Align Package Baseline With ABP 10.6

## Problem statement

The repository currently pins ABP packages at `10.3.0`, while the consuming TrueParser Control Plane is now on ABP `10.6.0`.

Hardening transport behavior against an older ABP base class risks implementing semantics that no longer exactly match the intended consumer environment.

## Proposed fix

Upgrade only ABP dependencies from:

```text
10.3.0
```

to:

```text
10.6.0
```

including all directly referenced ABP packages used by the solution.

Keep:

```text
NATS.Net 2.5.3
```

unchanged during this work.

Do NOT combine the ABP upgrade with a NATS.Net major-version upgrade.

## Verification

* restore succeeds;
* solution builds with zero errors;
* existing tests compile;
* existing live NATS tests pass against NATS Server with JetStream;
* no transport semantics are intentionally changed in this item.

---

# 2. Fix Durable Consumer / Service Identity

## Problem statement

Current durable consumer identity is generated from:

```csharp
$"{NatsOptions.StreamName}_{eventName}"
```

and therefore does not contain an application/service identity.

This is incorrect for ABP pub/sub fan-out.

Example:

```text
BillingService
NotificationService
```

both subscribe to:

```text
OrderCreated
```

If both use the same stream, both can resolve to the same durable JetStream consumer.

That turns two independent subscribers into competing consumers.

RabbitMQ's ABP transport instead uses `ClientName` as the durable queue identity, meaning:

* replicas of the same service share a queue;
* different services receive independent copies.

`AbpNatsOptions.ClientName` currently only sets the NATS connection name and does not participate in JetStream durable identity.

## Proposed fix

Add an explicit event-bus consumer identity:

```csharp
public string? ClientName { get; set; }
```

to `NatsDistributedEventBusOptions`.

`NatsDistributedEventBusOptions.ClientName` is required. Do not fall back to
`AbpNatsOptions.ClientName`: the former is the logical ABP event-bus consumer
identity, while the latter belongs exclusively to the NATS connection layer.

Fail startup with an actionable configuration exception when the explicit
event-bus `ClientName` is null, empty, or whitespace-only. Existing legacy
durables named `{StreamName}_{EventName}` do not require transport-level
migration or compatibility handling.

Generate durable consumer names from:

```text
{StreamName}_{ClientName}_{EventName}
```

after sanitization.

Required semantics:

```text
same ClientName + multiple process replicas
    -> same durable consumer
    -> load balanced delivery

different ClientName
    -> different durable consumer
    -> independent fan-out
```

Fail startup with a clear configuration exception if a durable service consumer cannot obtain a valid stable client identity.

Do not silently use a random process identity.

## Required tests

1. Same event + two different ClientNames:

   * both services receive the event.

2. Same ClientName + two service replicas:

   * one logical service delivery occurs.

3. Restart using same ClientName:

   * same durable is resumed.

---

# 3. Preserve Stable Event IDs Across Outbox, JetStream and Inbox

## Problem statement

Current NATS receive processing calls:

```csharp
AddToInboxAsync(
    null,
    eventName,
    eventType,
    eventData,
    correlationId)
```

so ABP receives no stable `messageId`.

ABP's inbox duplicate detection is based on the supplied message ID. When a message ID exists, `DistributedEventBusBase` checks whether it has already been persisted before enqueueing it again.

RabbitMQ preserves this identity by publishing `OutgoingEventInfo.Id` as `MessageId` and feeding that same ID into the receiver/inbox.

Without this, the current claim of RabbitMQ-equivalent inbox deduplication is incomplete.

## Proposed fix

Define one stable transport event ID.

For outbox publishing:

```text
OutgoingEventInfo.Id
```

must become the message ID.

For direct publishing:

```text
GuidGenerator.Create()
```

must generate the message ID.

Publish the ID in NATS headers using:

```text
Nats-Msg-Id
```

and, if useful for abstraction clarity, also a library-specific/ABP message-id header.

On consumption, extract the stable ID and pass it to:

```csharp
AddToInboxAsync(
    messageId,
    eventName,
    eventType,
    eventData,
    correlationId)
```

Required identity chain:

```text
ABP OutgoingEventInfo.Id
        ↓
Nats-Msg-Id
        ↓
JetStream message
        ↓
ABP AddToInboxAsync(messageId)
        ↓
IncomingEventInfo.MessageId
```

This gives:

```text
publisher retry
    -> JetStream duplicate suppression

consumer redelivery
    -> ABP inbox duplicate suppression
```

Do not replace ABP Inbox with JetStream deduplication.

## Required tests

1. Outbox ID survives publish and consume unchanged.

2. Same outbox event published twice:

   * JetStream receives the same `Nats-Msg-Id`.

3. Same JetStream message redelivered:

   * inbox prevents duplicate business-handler execution.

4. Correlation ID remains unchanged across the same path.

---

# 4. Correct Dynamic Event and Wildcard Event Identity

## Problem statement

The consumer loop currently receives the subscribed event name/pattern and passes it into message processing.

For a wildcard subscription such as:

```text
Identity.User.*
```

the actual NATS message may have been published as:

```text
Identity.User.Created
```

The handler must receive:

```text
Identity.User.Created
```

not the wildcard subscription pattern.

The existing wildcard integration test proves delivery but does not freeze the actual resulting dynamic event name.

## Proposed fix

Derive the actual event name from:

```csharp
msg.Subject
```

by removing:

```text
{SubjectPrefix}.
```

The subscription pattern must be used only for consumer filtering.

It must never replace the actual published event name.

Example:

```text
consumer filter:
TrueParser.Events.Identity.User.*

message subject:
TrueParser.Events.Identity.User.Created

resolved event name:
Identity.User.Created
```

## Required tests

Wildcard subscription receives:

```text
Identity.User.Created
Identity.User.Deleted
```

and the corresponding `DynamicEventData.EventName` values must be exactly:

```text
Identity.User.Created
Identity.User.Deleted
```

---

# 5. Fix Dynamic Events Through ABP Inbox

## Problem statement

Current `ProcessFromInboxAsync` does:

```csharp
var eventType = EventTypes.GetOrDefault(incomingEvent.EventName);

if (eventType == null)
{
    return;
}
```

A dynamic/string-keyed event does not necessarily have a CLR event type in `EventTypes`, so inbox processing can return without invoking the dynamic handler.

ABP RabbitMQ already supports both paths:

```text
known typed event
OR
registered dynamic handler
```

## Proposed fix

Match ABP RabbitMQ semantics.

For inbox processing:

```text
if EventTypes contains eventName:
    deserialize using CLR type
    invoke typed handlers

else if DynamicHandlerFactories has a matching event name:
    deserialize raw data
    wrap in DynamicEventData(actualEventName, data)
    invoke dynamic handlers

else:
    no registered handler -> return
```

Wildcard dynamic handler matching must use existing event-name matching logic.

Do not create a separate NATS-specific dynamic-event contract.

## Required tests

* exact dynamic event through inbox;
* wildcard dynamic event through inbox;
* typed event through inbox;
* unknown event does not invoke unrelated handlers.

---

# 6. Remove Duplicate DistributedEventSent Notification

## Problem statement

Current NATS `PublishToEventBusAsync` publishes to JetStream and explicitly calls:

```csharp
TriggerDistributedEventSentAsync(...)
```

ABP's `DistributedEventBusBase.PublishAsync` already calls `TriggerDistributedEventSentAsync` after `PublishToEventBusAsync`.

This can cause direct sends to produce duplicate `DistributedEventSent` notifications.

ABP RabbitMQ does not fire the notification from its transport method.

## Proposed fix

Make:

```csharp
PublishToEventBusAsync(...)
```

transport-only.

It should:

```text
serialize
publish JetStream message
return
```

Do not call `TriggerDistributedEventSentAsync` there.

Retain explicit:

```text
Source = DistributedEventSource.Outbox
```

notification in `PublishFromOutboxAsync`, matching ABP RabbitMQ behavior.

## Required tests

* direct publish produces exactly one DistributedEventSent;
* outbox publish produces exactly one Outbox-sourced DistributedEventSent.

---

# 7. Make First-Consumer Delivery Policy Explicit and RabbitMQ-Compatible

## Problem statement

Current new durable consumers use:

```csharp
DeliverPolicy = ConsumerConfigDeliverPolicy.All
```

That means a newly deployed service can receive old messages still retained in the stream.

This is useful for replay, but it differs from normal RabbitMQ behavior where a queue created today does not receive messages published before that queue existed.

For a package advertised as a low-change RabbitMQ replacement, this difference must not be implicit.

## Proposed fix

Add an explicit option representing initial consumer delivery policy.

Default:

```text
New
```

RabbitMQ-compatible semantics:

```text
brand-new consumer
    -> new messages only

existing durable restarting
    -> resume unacked/backlogged messages
```

Allow an explicit configuration value:

```text
All
```

for users who intentionally want historical retained-message replay.

Do not change the resume semantics of an existing durable consumer.

## Required tests

1. Publish old message.
2. Create a completely new ClientName consumer.
3. Default configuration:

   * old message is not received.

Separate test:

```text
DeliverPolicy = All
```

* retained historical message is received.

---

# 8. Keep Interest Retention but Validate Invalid Fan-Out Configurations

## Problem statement

Current default is:

```csharp
Retention = StreamConfigRetention.Interest
```

which is appropriate for pub/sub fan-out.

`WorkQueuePolicy`, however, is not equivalent to ABP distributed pub/sub because a message is removed after one worker group processes it.

Allowing it casually in a reusable ABP event-bus package can silently break multi-service fan-out.

## Proposed fix

Keep:

```text
Interest
```

as the default.

Support `Limits` only as an intentional advanced configuration.

For `WorkQueue`:

either:

```text
reject it for TrueParser.Abp.EventBus.Nats
```

or require an explicit opt-in accompanied by a clear startup warning that standard ABP pub/sub fan-out semantics are disabled.

Prefer rejection for the first hardened version unless a real ABP use case requires it.

## Required tests

* default is Interest;
* multiple independent ClientNames both receive the same event;
* unsupported WorkQueue configuration fails clearly if rejection is chosen.

---

# 9. Add Redelivery / Poison Message Controls

## Problem statement

Current handler failure behavior is approximately:

```csharp
catch
{
    await msg.NakAsync();
}
```

The package currently exposes `MaxAckPending` through `PrefetchCount`, but not the important durable failure controls.

A permanently failing handler may therefore be repeatedly redelivered without a clear package-level policy.

## Proposed fix

Expose JetStream consumer options for:

```text
AckWait
MaxDeliver
BackOff
```

Keep defaults conservative and aligned with native NATS behavior unless there is a strong reason to override them.

Do NOT build a custom dead-letter queue framework in this workstream.

Document that applications can build dead-letter/advisory handling separately if required.

## Required tests

* transient failure followed by success redelivers;
* configured MaxDeliver is honored;
* configured backoff is applied;
* successful processing ACKs and stops redelivery.

---

# 10. Validate Existing Stream Configuration

## Problem statement

Current stream initialization creates the stream and treats "already exists" as success.

It does not verify that the existing stream actually matches required settings.

This can silently accept:

```text
wrong SubjectPrefix
wrong retention
wrong replica count
wrong MaxAge
```

## Proposed fix

Initialization should:

```text
Get stream
    ↓
not found
    -> create configured stream

found
    -> validate critical configuration
```

Validate at minimum:

```text
stream name
subjects / SubjectPrefix
retention
```

Also report differences in:

```text
replica count
MaxAge
```

where applicable.

For the first hardened version, do NOT automatically mutate an existing stream unless explicitly configured.

If incompatible:

```text
fail startup with an actionable configuration error
```

Later an option such as:

```text
ManageStreamConfiguration = true
```

may be considered separately.

## Required tests

* missing stream is created;
* matching existing stream succeeds;
* incompatible subject fails;
* incompatible retention fails;
* restart with valid stream succeeds.

---

# 11. Make Live JetStream Tests Release-Blocking

## Problem statement

The repository already contains real NATS integration and thread-safety tests.

However `NatsFactAttribute` skips them unless:

```text
RUN_NATS_TESTS=true
```

The package publishing workflow explicitly sets:

```text
RUN_NATS_TESTS=false
```

meaning a release can currently be published without exercising the actual NATS transport.

That is unacceptable for a hardened transport package.

## Proposed fix

Add a CI/release job that starts a real local NATS server with JetStream enabled.

Before package publication:

```text
start nats-server -js
RUN_NATS_TESTS=true
dotnet test
```

Publishing must depend on successful live transport tests.

Keep fast non-NATS tests usable without requiring a broker for ordinary developer workflows.

## Required live reliability matrix

At minimum:

1. typed publish/consume;
2. dynamic publish/consume;
3. wildcard actual event-name preservation;
4. service fan-out across different ClientNames;
5. horizontal replica sharing with same ClientName;
6. durable restart/backlog recovery;
7. stable outbox/message/inbox ID propagation;
8. duplicate redelivery + inbox dedup;
9. consumer handler failure + redelivery;
10. broker temporary outage + recovery;
11. stream restart;
12. concurrent subscribe/unsubscribe;
13. tenant header propagation;
14. correlation ID propagation.

---

# 12. Harden Outbox/Inbox Parity Against ABP RabbitMQ

## Problem statement

The library documentation currently states:

```text
Outbox / Inbox works identically to RabbitMQ
```

That claim should only remain after transport-level parity is actually demonstrated.

## Proposed fix

Use ABP 10.6 `RabbitMqDistributedEventBus` as the behavioral reference implementation.

Audit these methods side-by-side:

```text
PublishAsync
PublishToEventBusAsync
PublishFromOutboxAsync
PublishManyFromOutboxAsync
ProcessFromInboxAsync
OnAddToOutboxAsync
AddToUnitOfWork
Subscribe
Unsubscribe
dynamic subscription handling
tenant/correlation propagation
DistributedEventSent
DistributedEventReceived
```

For each difference classify it as:

```text
BROKER-SPECIFIC
INTENTIONAL NATS ENHANCEMENT
BUG / PARITY GAP
```

Do not introduce different behavior unless JetStream requires it.

Record this matrix in repository documentation or TASK.md.

## Acceptance

Every behavior difference from RabbitMQ must have an explicit reason.

---

# 13. Do Not Upgrade NATS.Net During This Hardening

## Problem statement

The repository currently uses:

```text
NATS.Net 2.5.3
```

A NATS.Net major-version upgrade would introduce another large variable while delivery semantics are being corrected.

## Proposed fix

Freeze:

```text
NATS.Net 2.5.3
```

through all preceding fixes.

Once:

```text
ABP 10.6 compatibility
RabbitMQ parity
outbox/inbox IDs
consumer identity
dynamic events
redelivery
stream validation
live CI
```

are green, tag or commit that baseline.

Only then create an independent work item for the latest supported NATS.Net major version.

Do not mix that client migration into this hardening branch.

---

# 14. Documentation and Public Package Contract

## Problem statement

The package is intended to be usable outside TrueParser, so behavior must not depend on hidden TrueParser-specific assumptions.

README currently markets the package as a drop-in RabbitMQ replacement with minimal migration.

That should be retained only with precise configuration semantics.

## Proposed fix

Update documentation after implementation to describe:

```text
ClientName
durable consumer identity
same-service replicas
cross-service fan-out
Interest retention
initial DeliverPolicy
outbox responsibilities
inbox responsibilities
Nats-Msg-Id
redelivery semantics
MaxDeliver/AckWait/backoff
stream ownership/validation
HA ReplicaCount recommendations
```

Explicitly explain:

```text
JetStream does not replace ABP Inbox/Outbox.
```

Keep migration documentation focused on the minimal application changes.

---

# Required Implementation Order

Execute in this order:

```text
1. ABP 10.6 package alignment
        ↓
2. Durable ClientName / consumer identity
        ↓
3. Stable message/event identity
        ↓
4. Actual dynamic event-name resolution
        ↓
5. Dynamic inbox correctness
        ↓
6. DistributedEventSent parity fix
        ↓
7. Initial DeliverPolicy behavior
        ↓
8. Retention validation
        ↓
9. Redelivery controls
        ↓
10. Existing stream validation
        ↓
11. Live JetStream release-gated tests
        ↓
12. RabbitMQ parity audit
        ↓
13. Freeze NATS.Net version
        ↓
14. Documentation / release handoff
```

Do not combine several behavioral changes before validating the preceding one.

Prefer small commits that isolate each semantic change.

---

# Hard Boundaries

Do NOT:

* modify TrueParser Control Plane in this workstream;
* use Control Plane phase numbering;
* remove ABP Inbox;
* remove ABP Outbox;
* replace ABP DB inbox/outbox with NATS KV/Object Store;
* redesign `IDistributedEventBus`;
* require TrueParser-specific services;
* change application event DTO contracts;
* change application event handlers;
* upgrade NATS.Net in the same hardening work;
* invent custom exactly-once claims;
* claim exactly-once delivery;
* build a custom distributed transaction protocol;
* add a custom DLQ until actual requirements justify it.

Target semantic guarantee:

```text
at-least-once transport
+
stable message identity
+
ABP inbox idempotency
+
transactional ABP outbox
```

---

# Final Acceptance Matrix

```text
ABP Framework baseline                          10.6
NATS.Net semantic-hardening baseline            unchanged

Different ClientNames receive independent copy  PASS
Same ClientName replicas share durable          PASS
Durable restart resumes backlog                 PASS

Direct event has stable MessageId               PASS
Outbox event preserves OutgoingEventInfo.Id     PASS
Nats-Msg-Id populated                           PASS
Inbox receives same stable MessageId            PASS
Redelivery does not duplicate inbox execution   PASS

Typed events                                    PASS
Dynamic events                                  PASS
Wildcard events preserve actual event name      PASS
Dynamic events through inbox                    PASS

Tenant context                                  PASS
Correlation ID                                  PASS

DistributedEventSent direct notification        exactly once
DistributedEventSent outbox notification        exactly once

Default first-consumer behavior                 documented + tested
Interest fan-out                                PASS

Ack/redelivery controls                         PASS
Poison-event bounded-delivery behavior          PASS

Existing valid stream                           PASS
Existing incompatible stream                    deterministic failure

Live JetStream integration suite                PASS
Live JetStream release gate                     ENABLED

ABP RabbitMQ parity matrix                      COMPLETE
Application-specific dependencies               ABSENT
Control Plane changes                           ZERO
```

## Desired End State

A third-party ABP application should be able to migrate from RabbitMQ by changing only:

```text
NuGet package
module dependency
broker configuration
```

while leaving:

```text
publishers
handlers
ETOs
unit-of-work behavior
outbox configuration
inbox configuration
DbContext event-box implementation
```

unchanged.

That is the compatibility contract for `TrueParser.Abp.EventBus.Nats`.
