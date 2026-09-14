# TrueParserAbpNats — Independent ABP Compatibility Smoke Harness

Repository:

`TrueParser/TrueParserAbpNats`

Status: **complete for the independent ABP scope**

JWT/Seed authentication is covered by a disposable local fixture that creates
an operator, account, user JWT, and NKey seed at test runtime. No credentials
are committed to the repository.

## Objective

Add a **standalone ABP-based compatibility smoke harness** that proves `TrueParser.Abp.EventBus.Nats` works correctly in a real ABP application using:

```text
ABP 10.6
real ABP UoW
real ABP Outbox
real ABP Inbox
real ABP background processors
EF Core persistence
real NATS JetStream
TrueParser.Abp.EventBus.Nats
```

This test harness must remain **100% independent of TrueParser Control Plane**.

Do not reference or import:

```text
TrueParserControlPlane
TrueParserDbContext
Control Plane ETOs
Control Plane handlers
Control Plane business services
Control Plane configuration
Control Plane migrations
```

The package must be testable as a public, reusable ABP infrastructure package.

---

# 1. Architecture

Add an independent smoke application/test fixture.

Preferred structure:

```text
test/
├── TrueParser.Abp.EventBus.Nats.Tests/
│
├── TrueParser.Abp.EventBus.Nats.Smoke.Contracts/
│   └── generic smoke ETOs
│
├── TrueParser.Abp.EventBus.Nats.Smoke.Publisher/
│   └── independent ABP publisher application
│
├── TrueParser.Abp.EventBus.Nats.Smoke.Consumer/
│   └── independent ABP consumer application
│
└── TrueParser.Abp.EventBus.Nats.Smoke.Tests/
    └── orchestration/integration tests
```

If a smaller architecture can provide the same process-level guarantees, use it.

Do not create unnecessary abstraction layers.

---

# 2. Generic test domain

Create only generic smoke-test contracts.

Example:

```csharp
[EventName("Smoke.OrderCreated")]
public sealed class OrderCreatedEto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}
```

Create a generic tenant event:

```csharp
[EventName("Smoke.TenantOrderCreated")]
public sealed class TenantOrderCreatedEto : IMultiTenant
{
    public Guid Id { get; set; }

    public Guid? TenantId { get; set; }

    public string Name { get; set; } = string.Empty;
}
```

Create a generic test persistence entity such as:

```text
ProcessedSmokeEvent
```

that allows tests to prove handler business execution.

Do not use production-specific terminology.

---

# 3. Real ABP Inbox/Outbox infrastructure

Create a test-only EF Core DbContext implementing:

```csharp
IHasEventOutbox
IHasEventInbox
```

with:

```csharp
DbSet<OutgoingEventRecord>
DbSet<IncomingEventRecord>
DbSet<ProcessedSmokeEvent>
```

Configure:

```csharp
modelBuilder.ConfigureEventOutbox();
modelBuilder.ConfigureEventInbox();
```

Configure ABP:

```csharp
options.Outboxes.Configure(config =>
{
    config.UseDbContext<SmokeDbContext>();
});

options.Inboxes.Configure(config =>
{
    config.UseDbContext<SmokeDbContext>();
});
```

Use SQLite or another transactional test database.

Do not use EF InMemory for tests that claim transaction correctness.

---

# 4. Real background workers

The smoke harness must exercise actual ABP:

```text
OutboxSender
OutboxSenderManager
InboxProcessor
InboxProcessManager
```

Do not manually call:

```text
PublishFromOutboxAsync
ProcessFromInboxAsync
```

for the primary smoke tests.

Existing focused tests may continue doing that.

The smoke harness exists specifically to validate the complete ABP lifecycle.

---

# 5. Test — complete ABP flow

Add:

```text
Committed_UoW_Should_Flow_Through_Outbox_NATS_Inbox_And_Handler
```

Flow:

```text
ABP application service
↓
transactional UoW
↓
IDistributedEventBus.PublishAsync<OrderCreatedEto>()
↓
EF Outbox
↓
UoW commit
↓
ABP OutboxSender
↓
NATS JetStream
↓
consumer
↓
EF Inbox
↓
ABP InboxProcessor
↓
IDistributedEventHandler<OrderCreatedEto>
↓
ProcessedSmokeEvent DB row
```

Expected:

```text
before UoW commit:
    processed row count = 0

after UoW commit:
    exactly one processed row exists
    event ID matches publisher event
    payload matches publisher payload
```

Also verify:

```text
OutgoingEventInfo.Id
==
Nats-Msg-Id
```

where the transport exposes it.

---

# 6. Test — rollback

Add:

```text
Rolled_Back_UoW_Should_Not_Reach_NATS_Or_Handler
```

Flow:

```text
begin transactional UoW
publish distributed event
rollback / do not CompleteAsync
```

Expected:

```text
no consumer execution
no ProcessedSmokeEvent row
no committed Outbox event eligible for sending
```

Required invariant:

```text
database rollback
→ no broker side effect
```

---

# 7. Test — broker unavailable after Outbox commit

Add:

```text
Committed_Outbox_Event_Should_Survive_NATS_Outage_And_Send_After_Recovery
```

Scenario:

```text
NATS unavailable
↓
application UoW commits
↓
Outbox event committed
```

Expected while NATS unavailable:

```text
Outbox event remains waiting
handler execution count = 0
no false publish success
```

Restart NATS.

Expected:

```text
ABP OutboxSender retries automatically
event reaches JetStream
Inbox receives it
handler executes once
Outbox event is removed/marked sent afterward
```

Do not manually republish.

---

# 8. Test — real Inbox duplicate protection

Add:

```text
Duplicate_Message_Should_Not_Execute_Business_Handler_Twice
```

Use one stable message ID.

Introduce the same logical message more than once using the closest possible real transport/redelivery path.

Expected:

```text
business DB mutation count = 1
```

Do not accept only:

```text
Inbox row count = 1
```

The acceptance criterion is business execution.

---

# 9. Test — same ClientName across two processes

Use two independent ABP consumer processes/applications:

```text
Consumer A
ClientName = SmokeService

Consumer B
ClientName = SmokeService
```

Publish N uniquely identified events.

Expected:

```text
combined unique handled IDs = N
each event handled exactly once
duplicate handled IDs = 0
```

Do not require equal load distribution.

Required semantic:

```text
same ClientName
→ same durable
→ one logical service
→ load-balanced delivery
```

---

# 10. Test — different ClientNames

Run:

```text
Consumer A
ClientName = SmokeServiceA

Consumer B
ClientName = SmokeServiceB
```

Wait until both consumers are ready.

Publish N events.

Expected:

```text
A receives N unique events
B receives N unique events
```

Required semantic:

```text
different ClientName
→ independent durable consumers
→ fan-out
```

---

# 11. Test — restart and backlog recovery

Add:

```text
Consumer_Restart_Should_Resume_Same_Durable_Backlog
```

Scenario:

```text
consumer running
↓
durable created
↓
message retained/pending
↓
consumer process stops
↓
same consumer application restarts
with same ClientName
```

Expected:

```text
same durable identity reused
pending event eventually processed
no unrelated replacement durable created
```

Use persistent JetStream storage for this test.

Do not restart with an empty JetStream data directory.

---

# 12. Test — connection interruption and recovery

Add:

```text
Consumer_Should_Recover_After_NATS_Server_Restart
```

Flow:

```text
start NATS
start ABP consumer
publish event A
verify A handled
↓
stop nats-server
↓
restart nats-server
↓
publish event B
```

Expected:

```text
existing ABP application recovers
event B is handled
no second InitializeAsync required
no application restart required
```

If NATS.Net 3.2.0 has documented semantics requiring a different lifecycle, follow those semantics and document the exact behavior.

---

# 13. Test — clean shutdown

Add:

```text
ABP_Application_Shutdown_Should_Stop_NATS_Consumers_Cleanly
```

Exercise the normal ABP application shutdown lifecycle.

Expected within a bounded timeout:

```text
event bus shutdown completes
consumer loops stop
connection pool disposes
Inbox/Outbox workers stop
process exits
```

No:

```text
deadlock
hang
ObjectDisposedException
unobserved background exception
```

---

# 14. Test — disposed consumer cannot continue consuming

Add:

```text
Disposed_Consumer_Should_Not_Receive_New_Messages
```

Scenario:

```text
start consumer A
verify it consumes
stop/dispose consumer A

start consumer B with same durable identity
publish event
```

Expected:

```text
consumer A invocation count does not increase
consumer B handles event once
```

This proves no leaked `ConsumeAsync` task remains alive.

---

# 15. Test — typed event

Add complete application smoke:

```text
Typed_Event_Should_Round_Trip_Through_Real_ABP_Inbox_Outbox
```

Expected:

```text
correct EventName
correct CLR type
correct payload
correct handler
exactly one business execution
```

---

# 16. Test — dynamic exact event

Create a dynamic event such as:

```text
Smoke.Dynamic.Created
```

Expected:

```text
actual event name preserved
dynamic handler receives correct raw payload
Inbox processing preserves actual event name
```

---

# 17. Test — dynamic wildcard event

Subscribe:

```text
Smoke.Dynamic.*
```

Publish:

```text
Smoke.Dynamic.Created
Smoke.Dynamic.Updated
```

Expected:

```text
both delivered
actual event names preserved
unrelated Smoke.Other.Created not delivered
```

---

# 18. Test — correlation ID

Add:

```text
CorrelationId_Should_Round_Trip_Through_Outbox_NATS_Inbox
```

Set a known correlation ID before publishing.

Expected in final handler:

```text
handler correlation ID
==
publisher correlation ID
```

This must pass through:

```text
UoW
Outbox
NATS
Inbox
background worker
handler
```

---

# 19. Test — ABP IMultiTenant behavior

Do NOT implement custom tenant persistence for this test.

Use:

```csharp
TenantOrderCreatedEto : IMultiTenant
```

Publish:

```text
TenantId = TenantA
```

Expected in handler:

```text
CurrentTenant.Id == TenantA
```

Also test:

```text
TenantA
TenantB
host/null tenant
```

Expected:

```text
A executes under A
B executes under B
host event executes with null tenant
```

This validates standard ABP behavior independently of Control Plane.

---

# 20. Test — username/password authentication

Start a real local NATS server requiring credentials.

Valid credentials expected:

```text
connection opens
JetStream account call succeeds
publish/consume succeeds
```

Invalid credentials expected:

```text
no usable connection
no successful JetStream operation
no false Healthy status
```

---

# 21. Test — JWT/Seed authentication

The test project now creates a disposable local NATS JWT/Seed fixture when
`RUN_NATS_TESTS=true`:

```text
operator JWT
application account JWT with JetStream limits
user JWT + generated user NKey seed
temporary WSL nats-server with JetStream and MEMORY resolver
```

Valid:

```text
authenticated connection
JetStream succeeds
publish/consume succeeds
```

Invalid seed:

```text
authentication fails
no successful JetStream operation
```

Do not replace this with a unit test that only inspects options. The fixture
is local, disposable, and uses runtime-generated credentials; it is not used
by normal tests unless `RUN_NATS_TESTS=true` is explicitly set.

---

# 22. Test — named connection routing

Run two independent NATS JetStream servers:

```text
Server A
Server B
```

Configure:

```text
Default → Server A
Secondary → Server B
```

Expected:

```text
default event bus → stream appears only on Server A

ConnectionName = Secondary
→ stream appears only on Server B
```

Also verify repeated access to the same named connection reuses the cached connection instance.

---

# 23. Test — health check

Add:

```text
HealthCheck_Should_Be_Healthy_With_JetStream
```

Expected:

```text
Healthy
```

Add:

```text
HealthCheck_Should_Be_Unhealthy_When_Server_Unreachable
```

Expected:

```text
Unhealthy
```

Add:

```text
HealthCheck_Should_Be_Unhealthy_When_NATS_Has_No_JetStream
```

Expected:

```text
NATS reachable
JetStream unavailable
→ Unhealthy
```

Use bounded timeouts.

---

# 24. Test — PublishManyFromOutboxAsync

Add coverage using generic OutgoingEventInfo objects.

Test:

```text
PublishManyFromOutbox_Should_Preserve_All_MessageIds
```

Expected for every event:

```text
Nats-Msg-Id == OutgoingEventInfo.Id
```

Test:

```text
PublishManyFromOutbox_Should_Emit_One_Outbox_Notification_Per_Event
```

Expected:

```text
notification count == input event count
Source == Outbox
```

Test partial failure:

```text
event 1 succeeds
event 2 fails
event 3 not attempted
```

Expected:

```text
failure propagates
no fake batch success
```

---

# 25. Test — DistributedEventReceived

Add explicit notification assertions.

Typed direct event:

```text
Source = Direct
EventName correct
EventData correct
notification count = 1
```

Typed Inbox event:

```text
Source = Inbox
EventName correct
EventData correct
notification count = 1
```

Dynamic Inbox event:

```text
Source = Inbox
actual event name preserved
raw underlying dynamic payload exposed correctly
```

---

# 26. Test — wrong-handler isolation

Use deliberately similar generic event names.

Example:

```text
Smoke.Order.Created
Smoke.Order_Created
```

Expected:

```text
each event reaches only its intended consumer
durable identities differ
no cross-delivery
```

Also verify wildcard isolation.

---

# 27. Test — durable compatibility validation

Pre-create an incompatible consumer using the generated durable name.

Wrong:

```text
FilterSubject
```

Expected:

```text
ABP application initialization fails
clear AbpException
existing durable is not modified
```

Wrong:

```text
AckPolicy
```

Expected same behavior.

---

# 28. Test — stream compatibility validation

Pre-create incompatible stream configurations.

Test mismatches in:

```text
subjects
retention
replica count
max age
```

Expected:

```text
startup fails
clear difference reported
stream is not silently modified
```

---

# 29. Test execution rules

Every live smoke test must:

```text
use a unique StreamName
use a unique SubjectPrefix
use deterministic ClientName where identity matters
clean up consumers
clean up streams
clean up temp DB files
clean up NATS storage directories
kill child processes
```

Cleanup must occur even after failure.

Use:

```text
try/finally
IAsyncLifetime
IAsyncDisposable
```

as appropriate.

---

# 30. No arbitrary sleeps

Do not use long arbitrary:

```csharp
Task.Delay(5000)
```

as synchronization.

Prefer:

```text
TaskCompletionSource
bounded polling
WaitAsync(timeout)
process readiness signal
connection-state observation
```

Every async wait needs a hard timeout.

---

# 31. Existing focused suite remains

Do not replace the current tests.

The final model is:

```text
focused unit/integration tests
+
independent ABP full smoke harness
```

Focused tests should remain fast and diagnostic.

Smoke tests prove complete framework behavior.

---

# 32. No Control Plane coupling

Add a repository-wide guard.

The smoke projects must contain zero references to:

```text
TrueParserControlPlane
TrueParser.ControlPlane
TrueParserDbContext
Dodo
PlanChangedEto
TenantApplicationLifecycleEto
Control Plane namespaces
```

If any appear, fail review.

This repository must remain independently reusable.

---

# 33. Coverage measurement

After smoke coverage is complete, run:

```powershell
dotnet test --configuration Release --collect:"XPlat Code Coverage"
```

Report:

```text
line coverage
branch coverage
```

for:

```text
TrueParser.Abp.Nats
TrueParser.Abp.EventBus.Nats
```

Do not chase percentage with meaningless tests.

Inspect uncovered production branches and classify them:

```text
production-critical → add meaningful test
platform/error-only → document
trivial property/module code → low priority
```

---

# 34. Execution order

Do this in small slices:

```text
1. Create generic ABP smoke host infrastructure
2. Real Outbox → NATS → Inbox → Handler
3. Transaction rollback
4. Outbox broker outage/recovery
5. Inbox duplicate execution
6. same-ClientName multi-process
7. different-ClientName multi-process
8. restart/backlog
9. disconnect/reconnect
10. shutdown/leaked consumer verification
11. typed/dynamic/wildcard
12. correlation + IMultiTenant
13. auth
14. named connections
15. health
16. PublishManyFromOutbox
17. DistributedEventReceived
18. durable/stream incompatibility
19. coverage measurement
```

For every numbered slice:

```text
add focused test
↓
run focused test
↓
make smallest required fixture/production change
↓
focused GREEN
↓
full broker-free suite
↓
full RUN_NATS_TESTS=true suite
↓
STOP
```

Do not start the next slice automatically.

---

# 35. Production-code rule

This work is primarily test infrastructure.

Do not refactor production code just to simplify tests.

If a smoke test exposes a real defect:

```text
RED
↓
identify root cause
↓
smallest production fix
↓
focused GREEN
↓
full regression
```

No unrelated cleanup.

---

# 36. Final acceptance

The package can be considered independently hardened only when:

```text
focused test suite                         GREEN
real ABP Outbox → NATS → Inbox             GREEN
UoW rollback                               GREEN
Outbox outage/retry                         GREEN
Inbox duplicate protection                 GREEN
same-ClientName multi-process              GREEN
different-ClientName fan-out               GREEN
restart/backlog                             GREEN
broker reconnect                            GREEN
shutdown                                    GREEN
typed event                                 GREEN
dynamic exact                               GREEN
dynamic wildcard                            GREEN
correlation                                 GREEN
IMultiTenant                                GREEN
username/password auth                      GREEN
JWT/Seed auth                               GREEN
named connections                           GREEN
health checks                               GREEN
PublishManyFromOutbox                       GREEN
DistributedEventReceived                    GREEN
durable validation                          GREEN
stream validation                           GREEN

Control Plane references                    ZERO
```

---

# 37. Final report

Report:

```text
 baseline commit: current working tree baseline
 final commit: working tree (not committed)
 ABP version: 10.6
 NATS.Net version: 3.2.0

 previous test count: 77
 new focused test count: 6
 new smoke test count: 6

broker-free:
     passed: 2
     failed: 0
     skipped: 81

live JetStream:
     passed: 83
     failed: 0
     skipped: 0

 Outbox E2E: PASS
 Inbox E2E: PASS
 rollback: PASS
 broker outage/recovery: PASS
 duplicate delivery: PASS
 same ClientName multi-process: PASS
 different ClientName fan-out: PASS
 restart backlog: PASS
 reconnect: PASS
 shutdown: PASS
 typed: PASS
 dynamic: PASS
 wildcard: PASS
 correlation: PASS
 IMultiTenant: PASS
 username/password: PASS
 JWT/Seed: PASS (runtime-generated disposable fixture)
 named connection: PASS
 health: PASS
 PublishManyFromOutbox: PASS
 DistributedEventReceived: PASS
 durable validation: PASS
 stream validation: PASS

 line coverage: 87.37% overall (TrueParser.Abp.Nats 93.05%; TrueParser.Abp.EventBus.Nats 86.57%)
 branch coverage: 72.08% overall (TrueParser.Abp.Nats 92.30%; TrueParser.Abp.EventBus.Nats 69.62%)

 production defects discovered: none in this acceptance implementation
 production files changed: none

Control Plane references found:
     0

 remaining known gaps: none in the independent ABP smoke scope
```

Do not mark a test PASS if its production boundary was replaced with a mock.
