# Control Plane — RabbitMQ → NATS Migration Acceptance Suite

Repository:

`TrueParser/TrueParserControlPlane`

## Objective

Before RabbitMQ is removed from Control Plane, prove that every production distributed-event path that currently depends on ABP RabbitMQ behaves correctly through:

```text
TrueParser.Abp.EventBus.Nats
+
NATS JetStream
+
existing ABP Inbox
+
existing ABP Outbox
```

This is a **migration acceptance gate**, not a generic NATS test suite.

`TrueParserAbpNats` already owns transport-level tests.

These tests must prove that the **actual Control Plane application** continues to behave correctly after swapping transports.

---

# 1. Hard rule

Do not consider the RabbitMQ → NATS migration complete because:

```text
NATS connects
one event publishes
one handler runs
```

That is insufficient.

Acceptance requires:

```text
every distributed event contract currently used by Control Plane
+
every distributed handler
+
every domain-event → distributed-event bridge
+
ABP Inbox/Outbox
+
cache invalidation events
+
important chained workflows
```

to be verified against NATS.

Do not remove RabbitMQ packages/configuration until this suite is GREEN.

---

# 2. First build the authoritative event graph

Before writing tests, use Roslyn MCP as the primary code-navigation mechanism.

Do not derive the event inventory from filenames alone.

Discover all implementations/references of:

```csharp
IDistributedEventHandler<T>
IDistributedEventBus
PublishAsync(...)
PublishManyAsync(...)
```

Also locate:

```text
[EventName(...)]
domain event handlers that publish distributed ETOs
background workers that publish events
host-level distributed handlers
ABP framework ETO handlers used for cache coherence
```

Generate a migration matrix:

```text
Producer
Domain/source event
Distributed ETO
EventName
Uses Outbox?
Distributed handler(s)
Uses Inbox?
Expected business side effect
Tenant-sensitive?
External side effect?
Smoke test
```

The repository ADR currently identifies the event-driven surface across:

```text
Application/EventHandlers
Application.Contracts/Events
Domain/Events
```

but the source code is authoritative.

Do not rely solely on ADR documentation because new handlers may have been added after the ADR.

---

# 3. Known Control Plane distributed-event surface

At minimum verify all currently active contracts discovered under:

```text
TrueParser.ControlPlane.Application.Contracts/Events
```

including the known families:

```text
CustomTenantRegisteredEto
Customer*
DodoWebhookReceivedEto
HostApplicationCreatedEto
InvitationAcceptedEto
InvitationSentEto
Payment*
PlanChangedEto
PlanRetiredEto
PlanSync*
RotateTenantKeyEto
Subscription*
TenantApplicationLifecycleEto
TenantStatusChangedEto
```

Do not assume one class per source file.

Some files contain multiple ETO types.

Every concrete ETO with a production publisher or distributed handler must appear in the matrix.

---

# 4. Include host-level distributed events

Do not restrict the audit to the ControlPlane module.

The HTTP host also contains distributed handlers used for cache/security coherence.

At minimum inspect and test:

```text
UserStatusChangedEto
TenantStatusChangedEto
TenantSigningKeyChangedEto
```

and any additional distributed handler found under:

```text
src/TrueParser.HttpApi.Host/EventHandlers
```

Current known handlers include cache invalidation for:

```text
user status
tenant status
tenant signing keys
```

These are production-critical because stale distributed caches can change authentication or authorization behavior.

---

# 5. Test the real application path

For important workflows, do NOT write only:

```csharp
await eventBus.PublishAsync(testEto);
```

followed by a synthetic test handler.

That proves serialization and routing but not the actual migration.

The strongest smoke path is:

```text
real application/domain operation
        ↓
domain event
        ↓
existing domain-event handler
        ↓
existing distributed ETO
        ↓
ABP Outbox / TrueParserDbContext
        ↓
ABP OutboxSender
        ↓
NATS JetStream
        ↓
NATS durable consumer
        ↓
ABP Inbox / TrueParserDbContext
        ↓
ABP InboxProcessor
        ↓
real production distributed handler
        ↓
observable business side effect
```

Where practical, test this complete chain.

---

# 6. Infrastructure for the acceptance suite

Use:

```text
real Control Plane host/test application
real MySQL test database
real Redis
real local NATS JetStream
real TrueParser.Abp.EventBus.Nats
real ABP Inbox
real ABP Outbox
```

Do not mock:

```text
IDistributedEventBus
NatsDistributedEventBus
IEventInbox
IEventOutbox
NATS connection
JetStream
Redis cache
```

when the behavior under test crosses those boundaries.

External third-party systems may be replaced with controlled test doubles:

```text
SMTP/email delivery
Dodo remote APIs
Infisical/external secret service where necessary
```

We are testing transport/application integration, not sending real external traffic.

---

# 7. Per-event transport smoke test

For **every concrete distributed ETO** in the authoritative matrix, add at least one transport smoke.

Pattern:

```text
publish real ETO
↓
ABP Outbox if production path uses Outbox
↓
NATS
↓
ABP Inbox if configured
↓
real matching handler
```

## Expected assertions

For every ETO:

```text
correct EventName used
serialization succeeds
deserialization returns correct CLR type
all important payload fields survive
correct handler is invoked
unrelated handler is not invoked
handler invocation count is exactly expected
no exception logged by consumer
message is ACKed after successful processing
```

Do not accept a test whose only assertion is:

```text
PublishAsync did not throw
```

---

# 8. Business-side-effect smoke tests

For each real handler, verify its meaningful outcome.

Examples follow; Codex must derive the exact assertion from current code.

## 8.1 Invitation events

For:

```text
InvitationSentEto
InvitationAcceptedEto
```

Expected:

```text
correct production handler invoked
correct tenant/user/invitation identifiers received
expected email/onboarding side effect requested
no duplicate side effect
```

Do not send real email.

Capture the configured email sender/test double and assert the production handler attempted exactly the expected message.

---

## 8.2 Plan changes

For:

```text
PlanChangedEto
PlanRetiredEto
PlanSync*
```

Expected depending on actual handler:

```text
correct plan retrieved/updated
correct dependent application state changed
correct notification requested
correct external-sync operation requested
no unrelated plan affected
```

If one domain event publishes another distributed ETO, test the complete chain.

---

## 8.3 Customer/subscription/payment pipeline

Test all production ETOs discovered for:

```text
Customer
Subscription
Payment
```

Expected:

```text
correct state transition
correct identifiers preserved
idempotent processing
no duplicate customer/subscription/payment mutation
```

For Dodo-facing effects, mock only the external Dodo HTTP boundary, not the distributed bus.

---

## 8.4 Dodo webhook

For:

```text
DodoWebhookReceivedEto
```

Use the real webhook consumer.

Expected:

```text
NATS delivery occurs
ABP Inbox receives event
DodoWebhookConsumer invoked
real application-level webhook processing invoked
same logical webhook is not processed twice
```

This path is particularly important because the current handler explicitly relies on transactional processing/idempotency.

---

## 8.5 Host application

For:

```text
HostApplicationCreatedEto
```

and any related lifecycle ETO:

Expected:

```text
correct handler invoked
correct application ID/name/metadata preserved
expected downstream notification/side effect invoked once
```

---

## 8.6 Tenant application lifecycle

For:

```text
TenantApplicationLifecycleEto
```

Expected:

```text
correct tenant application selected
correct lifecycle operation applied
correct related notification generated where applicable
another tenant/application remains untouched
```

---

## 8.7 Key rotation

Test:

```text
KeyRotationPoller
    ↓
RotateTenantKeyEto
    ↓
RotateTenantKeyEventHandler
```

Expected:

```text
event published through NATS
correct tenant/key identity reaches handler
SigningKeyManager called exactly once
resulting signing-key lifecycle event is published if production code does so
cache-coherence event reaches host handler
```

This should be tested as an event chain where possible.

---

# 9. Cache-coherence smoke tests

These are mandatory.

## 9.1 User status

Trigger the production path resulting in:

```text
UserStatusChangedEto
```

Prime Redis with the old user-status cache entry.

Expected:

```text
event crosses NATS
UserCacheInvalidationHandler runs
target cache entry invalidated
unrelated user cache entry remains
```

---

## 9.2 Tenant status

Prime:

```text
TenantStatusCacheItem
```

Trigger:

```text
TenantStatusChangedEto
```

Expected:

```text
target tenant cache entry invalidated
unrelated tenant cache entry remains intact
```

---

## 9.3 Tenant signing key

Prime the signing-key coherence cache.

Trigger:

```text
TenantSigningKeyChangedEto
```

Expected:

```text
NATS delivery
TenantSigningKeyCacheInvalidationHandler invoked
correct cache item removed/refreshed
unrelated tenant key cache remains untouched
```

This test must be GREEN before RabbitMQ removal because this path affects signing-key coherence.

---

# 10. Domain → distributed bridge verification

Every domain-event handler that calls:

```csharp
IDistributedEventBus.PublishAsync(...)
```

must get a focused smoke test.

Known areas to inspect include:

```text
HostApplicationDomainEventHandler
InvitationDomainEventHandler
PlanChangedDomainEventHandler
PlanRetiredDomainEventHandler
PlanSyncDomainEventHandler
TenantApplicationLifecycleDomainEventHandler
SigningKeyLifecycleChangedDomainEventHandler
DodoEventHandlers
KeyRotationPoller
Identity status event bridge
```

For every bridge:

```text
raise/trigger original domain event
↓
observe expected distributed ETO enter Outbox
↓
allow OutboxSender to publish
↓
assert expected downstream handler behavior
```

Do not directly publish the ETO in these bridge tests.

The point is to prove that the original business workflow still produces the same distributed event after migration.

---

# 11. Event-name compatibility gate

RabbitMQ and NATS must expose the **same ABP logical event names**.

Before migration, freeze the authoritative set of:

```text
CLR event type
→ EventNameAttribute / ABP resolved name
```

After migration assert every name remains identical.

Example:

```text
PlanChangedEto
→ TrueParser.ControlPlane.PlanChanged
```

The migration must not accidentally turn event routing into CLR full names where an explicit `[EventName]` exists.

Fail the migration if any logical event name changes.

---

# 12. Serialization fidelity gate

For each ETO family, include representative payloads containing:

```text
Guid
nullable Guid
DateTime / DateTimeOffset
enum
nullable enum
decimal
bool
empty/null strings where valid
collections where present
nested DTOs where present
```

Expected:

```text
publisher object
→ serialized NATS payload
→ deserialized handler object
```

preserves the values used by business logic.

This is particularly important because RabbitMQ and the NATS package may not use identical serializer implementation details even though they expose the same ABP abstraction.

---

# 13. Outbox transactional smoke

Use the real `TrueParserDbContext`.

## 13.1 Commit

```text
business DB transaction
+
distributed event
```

Expected before UoW commit:

```text
no handler side effect
```

Expected after commit:

```text
Outbox record committed
OutboxSender publishes to NATS
handler eventually executes
Outbox record cleared/marked appropriately
```

---

## 13.2 Rollback

Create business mutation + event inside a transactional UoW.

Rollback.

Expected:

```text
business mutation absent
event not delivered
no downstream handler execution
```

This is a hard migration requirement.

---

# 14. Inbox idempotency smoke

Use a production-representative ETO and the real ABP Inbox.

Deliver the same logical message ID more than once.

Expected:

```text
transport may redeliver
Inbox identifies same message
business handler side effect occurs once
```

Assert the business side effect, not merely Inbox row count.

---

# 15. NATS outage during Outbox publishing

Scenario:

```text
commit business transaction
↓
Outbox row exists
↓
NATS unavailable
```

Expected:

```text
business transaction remains committed
Outbox event remains waiting
no fake successful send
```

Restart NATS.

Expected:

```text
ABP OutboxSender retries
event is published
real handler executes
Outbox item eventually removed
```

No manual re-publish.

---

# 16. Control Plane restart with backlog

Scenario:

```text
event published
durable exists
handler/service stopped before completion or with pending backlog
Control Plane shuts down
Control Plane starts again
```

Expected:

```text
same NATS ClientName
→ same durable identity
→ backlog resumes
→ event eventually handled
```

No new unrelated durable should be created for the same logical service.

---

# 17. Duplicate/redelivery behavior

For at least one DB-mutating handler:

```text
deliver
handler completes business work
simulate/redeliver same logical message
```

Expected:

```text
business mutation occurs once
```

For side-effect handlers such as email, prove either:

```text
existing application idempotency prevents duplicate side effect
```

or document clearly if the application intentionally permits duplicate at-least-once side effects.

Do not claim exactly-once semantics from NATS.

---

# 18. Two-instance Control Plane smoke

Run two independent Control Plane instances/processes:

```text
Instance A
ClientName = TrueParser.ControlPlane

Instance B
ClientName = TrueParser.ControlPlane
```

Publish a set of uniquely identified events.

Expected:

```text
one shared logical service durable
each event handled by exactly one instance
no duplicated business side effects
```

Do not require equal distribution between A and B.

Then test distinct logical consumers if Control Plane has any intentionally different `ClientName`s.

---

# 19. Correlation ID

For a representative workflow:

```text
HTTP/application request
→ domain event
→ Outbox
→ NATS
→ Inbox
→ handler
```

Expected:

```text
original ABP correlation ID
==
handler correlation ID
```

This must remain true across asynchronous processing.

---

# 20. Multi-tenancy

Do not implement custom NATS tenant-Inbox metadata merely for this test.

Use production ETO behavior.

For tenant-scoped ETOs implementing `IMultiTenant`, verify:

```text
publish Tenant A event
→ handler CurrentTenant.Id == Tenant A
```

Also publish Tenant B.

Expected:

```text
A handler never operates on B's entities
B handler never operates on A's entities
```

For host events:

```text
CurrentTenant.Id == null
```

where that is the intended existing ABP behavior.

---

# 21. Wrong-handler isolation

Publish representative events with similar names.

Expected:

```text
PlanChanged
does not invoke PlanRetired handler

TenantStatusChanged
does not invoke UserStatusChanged handler

CustomerCreated
does not invoke SubscriptionCreated handler
```

This validates subject/event-name isolation after changing transports.

---

# 22. Startup validation

Start migrated Control Plane against:

## Valid NATS configuration

Expected:

```text
host starts
stream created/validated
all required durable consumers initialize
health/readiness succeeds
```

## Wrong stream configuration

Expected:

```text
startup fails clearly
existing stream is not silently mutated
```

## Missing EventBus ClientName

Expected:

```text
startup fails with actionable configuration error
```

No random/machine-generated fallback is permitted.

---

# 23. Shutdown smoke

With active subscriptions:

```text
start Control Plane
publish/consume successfully
initiate normal application shutdown
```

Expected:

```text
NATS consumer tasks stop
event bus disposes
connection pool disposes
host exits normally
no hung shutdown
no unobserved consumer exception
```

Then restart and verify normal event processing again.

---

# 24. Full localhost API smoke after migration

After all event-specific tests pass, run the existing full localhost smoke suite against the migrated host.

Do not weaken or skip existing tests.

Expected:

```text
same HTTP/API behavior as pre-migration baseline
authentication works
tenant isolation works
OpenIddict works
Redis-backed behavior works
MySQL behavior works
background workers start
no RabbitMQ dependency required
```

Use the previously frozen localhost baseline as the regression comparison.

---

# 25. RabbitMQ removal audit

Only after all migration tests are GREEN:

Search with Roslyn/codebase tools for:

```text
Volo.Abp.EventBus.RabbitMq
AbpEventBusRabbitMqModule
AbpRabbitMqEventBusOptions
RabbitMQ:
RabbitMQ__
RabbitMq
rabbitmq
```

Classify every result as:

```text
production reference
test reference
historical documentation
comment
migration document
```

Production target after final removal:

```text
RabbitMQ package reference             0
RabbitMQ module dependency             0
RabbitMQ runtime configuration         0
RabbitMQ connection requirement        0
RabbitMQ production code dependency    0
```

Historical ADRs may retain RabbitMQ references if explicitly describing history, but current architecture documentation must be updated.

---

# 26. Migration acceptance matrix

Codex must produce a table similar to:

| Event / workflow        | Producer          |   Outbox | NATS | Inbox | Handler                                  | Side effect          | Result |
| ----------------------- | ----------------- | -------: | ---: | ----: | ---------------------------------------- | -------------------- | ------ |
| InvitationSent          | domain bridge     |        ✓ |    ✓ |     ✓ | InvitationEmailHandler                   | email request        | PASS   |
| PlanChanged             | domain bridge     |        ✓ |    ✓ |     ✓ | PlanChangedEmailHandler + others         | plan side effects    | PASS   |
| DodoWebhookReceived     | HTTP webhook      | ✓/actual |    ✓ |     ✓ | DodoWebhookConsumer                      | webhook processing   | PASS   |
| RotateTenantKey         | KeyRotationPoller | ✓/actual |    ✓ |     ✓ | RotateTenantKeyEventHandler              | signing key rotation | PASS   |
| UserStatusChanged       | identity bridge   | ✓/actual |    ✓ |     ✓ | UserCacheInvalidationHandler             | Redis invalidation   | PASS   |
| TenantSigningKeyChanged | key lifecycle     | ✓/actual |    ✓ |     ✓ | TenantSigningKeyCacheInvalidationHandler | Redis invalidation   | PASS   |

The actual matrix must contain **every event and handler discovered by Roslyn**, not only these examples.

---

# 27. Hard acceptance criteria

Migration is accepted only when:

```text
TrueParserAbpNats full live test suite             GREEN

Control Plane unit tests                           GREEN
Control Plane application tests                    GREEN
Control Plane domain tests                         GREEN
Control Plane EF tests                             GREEN

every distributed ETO smoke                        GREEN
every production distributed handler               GREEN
every domain → distributed bridge                  GREEN

ABP Outbox real MySQL flow                         GREEN
ABP Inbox real MySQL flow                          GREEN
duplicate/redelivery idempotency                   GREEN
broker outage/recovery                             GREEN
restart/backlog                                    GREEN

user cache invalidation                            GREEN
tenant cache invalidation                          GREEN
signing-key cache invalidation                     GREEN

correlation propagation                            GREEN
tenant-scoped representative workflows             GREEN
same-ClientName two-instance semantics             GREEN

full localhost API smoke                           GREEN

RabbitMQ production references                     ZERO
```

No skipped migration test counts as PASS.

---

# 28. Execution strategy

Do this in small slices.

Recommended sequence:

```text
1. Build authoritative event graph
2. Freeze pre-migration RabbitMQ behavior matrix
3. Swap package/module/config to NATS
4. Verify startup only
5. Add per-ETO transport smoke
6. Add domain → ETO bridge smoke
7. Add business-side-effect smoke
8. Add cache-coherence smoke
9. Verify Outbox transaction behavior
10. Verify Inbox/idempotency behavior
11. Verify outage/restart behavior
12. Verify two-instance behavior
13. Run complete localhost/API regression
14. Remove RabbitMQ
15. Run everything again
```

After each slice:

```text
build
focused tests
full relevant suite
regression check
commit
```

Do not perform the entire migration as one large diff.

---

# 29. Critical non-goals

Do not change while migrating:

```text
ETO shapes
EventName values
domain behavior
business logic
database schema
existing ABP Inbox/Outbox architecture
tenant model
email semantics
Dodo business semantics
cache key shapes
API contracts
OpenIddict behavior
```

The migration should be:

```text
RabbitMQ transport
        ↓ replace only
NATS JetStream transport
```

while application semantics remain unchanged.

---

# 30. Final report

At completion provide:

```text
Control Plane commit:
TrueParserAbpNats version/commit:
NATS.Net version:

distributed ETOs discovered:
distributed handlers discovered:
domain→distributed bridges discovered:

ETO smoke:
    passed:
    failed:
    skipped:

Outbox real flow:
Inbox real flow:
duplicate delivery:
broker outage:
broker recovery:
restart backlog:
two-instance same ClientName:
correlation:
tenant context:
cache coherence:

Application tests:
Domain tests:
EF tests:
localhost smoke:

RabbitMQ production references remaining:

migration verdict:
    PASS / FAIL
```

A `PASS` requires zero unexplained failures and zero production RabbitMQ dependencies.
