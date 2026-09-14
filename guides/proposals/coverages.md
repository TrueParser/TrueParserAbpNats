# TrueParserAbpNats — Production Integration Coverage Expansion

## Objective

Expand the current live/integration suite beyond the existing 41 tests and cover the remaining production-critical behavior.

This work is **primarily test coverage**.

Do not redesign production code merely to make testing easier.

If a new test exposes a genuine production defect:

```text
RED test
↓
characterize defect
↓
make smallest production fix
↓
prove test GREEN
↓
run full existing suite
```

Do not weaken expectations to accommodate current implementation.

## Boundaries

Do NOT:

```text
modify GitHub release workflow
add live NATS requirements to GitHub Actions
add custom tenant-Inbox persistence
change public event contracts
change NATS delivery semantics
change ABP Inbox/Outbox semantics
refactor NatsDistributedEventBus unrelated to a discovered defect
use Docker
```

All live NATS tests remain controlled/local tests using:

```text
RUN_NATS_TESTS=true
```

Use unique:

```text
StreamName
SubjectPrefix
ClientName
ports
database names
```

for every test fixture so tests cannot contaminate one another.

Avoid arbitrary long `Task.Delay()` calls.

Prefer:

```text
TaskCompletionSource
bounded polling
WaitAsync(timeout)
connection-state/event signals
```

Every asynchronous test must have a hard timeout.

Execute the numbered items one at a time.

## Current implementation status

Status recorded on 2026-09-13. This proposal is **complete**. The
following thirty-three tests have been implemented and passed against the local
JetStream-enabled NATS servers and the disposable MySQL test database:

| Proposal test | Status | Implemented test |
|---|---|---|
| 1.1 committed Outbox flow | `[x]` Verified | `Publishing_Inside_Committed_UoW_Should_Flow_Through_ABP_Outbox_To_JetStream` |
| 1.2 rolled-back UoW | `[x]` Verified | `Rolled_Back_UoW_Should_Not_Publish_Outbox_Event` |
| 1.3 Outbox publish failure and recovery | `[x]` Verified | `Outbox_Worker_Should_Delete_Record_Only_After_Successful_NATS_Publish` |
| 2.1 Inbox background processing | `[x]` Verified | `Incoming_NATS_Event_Should_Be_Processed_By_ABP_Inbox_Background_Processor` |
| 2.2 failed Inbox handler | `[x]` Verified | `Failing_Inbox_Handler_Should_Not_Be_Marked_Processed` |
| 2.3 Inbox duplicate protection | `[x]` Verified | `Successful_Inbox_Handler_Should_Be_Executed_Exactly_Once` |
| 3.1 valid username/password authentication | `[x]` Verified | `UsernamePassword_Authentication_With_Valid_Credentials_Should_Connect` |
| 3.2 invalid username/password authentication | `[x]` Verified | `UsernamePassword_Authentication_With_Invalid_Credentials_Should_Fail` |
| 3.3 valid JWT/Seed authentication | `[x]` Verified | `JwtSeed_Authentication_With_Valid_Test_Credentials_Should_Connect` |
| 3.4 invalid JWT/Seed authentication | `[x]` Verified | `JwtSeed_Authentication_With_Invalid_Seed_Should_Fail` |
| 4.1 default connection resolution | `[x]` Verified | `Default_Connection_Should_Resolve_Default_Server` |
| 4.2 named connection resolution | `[x]` Verified | `Named_Connection_Should_Resolve_Configured_Server` |
| 4.3 connection reuse | `[x]` Verified | `Multiple_Requests_For_Same_ConnectionName_Should_Reuse_Connection` |
| 5.1 consumer recovery after NATS restart | `[x]` Verified | `Consumer_Should_Recover_After_NATS_Server_Restart` |
| 5.2 publish during broker outage | `[x]` Verified | `Publish_During_Broker_Outage_Should_Fail_Or_Wait_According_To_NATS_Client_Semantics_Without_False_Success` |
| 5.3 durable backlog recovery after restart | `[x]` Verified | `Durable_Consumer_Should_Resume_Backlog_After_Server_Restart` |
| 6.1 bounded application shutdown | `[x]` Verified | `Application_Shutdown_Should_Stop_Consumers_Without_Hanging` |
| 6.2 disposed event bus stops consuming | `[x]` Verified | `Disposed_EventBus_Should_No_Longer_Consume_Messages` |
| 6.3 fresh event bus after shutdown | `[x]` Verified | `Fresh_EventBus_Should_Start_After_Previous_Bus_Was_Shut_Down` |
| 7.1 PublishMany publishes all events with original IDs | `[x]` Verified | `PublishManyFromOutbox_Should_Publish_All_Events_With_Original_MessageIds` |
| 7.2 PublishMany emits one notification per event | `[x]` Verified | `PublishManyFromOutbox_Should_Emit_One_Outbox_Sent_Notification_Per_Event` |
| 7.3 PublishMany stops on partial failure | `[x]` Verified | `PublishManyFromOutbox_Should_Stop_On_Partial_Failure` |
| 7.4 retry after partial failure avoids duplicate delivery | `[x]` Verified | `Retry_After_Partial_Batch_Failure_Should_Not_Create_Duplicate_Business_Delivery` |
| 8.1 healthy NATS and JetStream health check | `[x]` Verified | `HealthCheck_Should_Be_Healthy_When_NATS_And_JetStream_Are_Available` |
| 8.2 unreachable NATS health check | `[x]` Verified | `HealthCheck_Should_Be_Unhealthy_When_Server_Is_Unreachable` |
| 8.3 NATS without JetStream health check | `[x]` Verified | `HealthCheck_Should_Be_Unhealthy_When_NATS_Is_Running_Without_JetStream` |
| 9.1 typed direct DistributedEventReceived notification | `[x]` Verified | `Typed_Direct_Event_Should_Emit_One_DistributedEventReceived_From_Direct` |
| 9.2 typed Inbox DistributedEventReceived notification | `[x]` Verified | `Typed_Inbox_Event_Should_Emit_One_DistributedEventReceived_From_Inbox` |
| 9.3 dynamic Inbox notification identity and payload | `[x]` Verified | `Dynamic_Inbox_Event_Should_Emit_Actual_Event_Name_And_Raw_Event_Data` |
| 10.1 same-client multi-process replica sharing | `[x]` Verified | `Two_Processes_With_Same_ClientName_Should_Act_As_One_Logical_Service` |
| 10.2 different-client multi-process fan-out | `[x]` Verified | `Two_Processes_With_Different_ClientNames_Should_Each_Receive_Full_Event_Set` |
| 11 coverage measurement and uncovered-branch review | `[x]` Verified | Coverlet XPlat Code Coverage report and production-branch classification |

No proposal tests remain pending. Item 11 records measurement and uncovered
branch review below; it is not a percentage target.

```text
pending: none
```

The test fixture uses one disposable MySQL database, as authorized for local
verification, rather than the SQLite fixture suggested by the original test
infrastructure section. It still uses ABP's real EF-backed Inbox and Outbox
infrastructure; it does not use EF InMemory or manually invoke the transport
methods for the six end-to-end tests.

Verification snapshot:

```text
Live suite:        77 passed, 0 failed, 0 skipped
Broker-free suite: 2 passed, 0 failed, 75 skipped
```

The live tests remain gated by `NatsFact` and `RUN_NATS_TESTS=true`; the
authentication and named-connection tests use temporary local WSL JetStream
servers on isolated ports, with generated JWT/Seed material kept outside the
repository. Recovery and shutdown tests start and stop their own temporary WSL
JetStream server processes with isolated persisted storage. The tagging and
package-publication workflow is not part of this proposal update.

---

# 1. True ABP Outbox End-to-End Integration

## Current gap

Current Outbox tests mainly call:

```csharp
PublishFromOutboxAsync(...)
```

directly.

That verifies the transport method but does not prove:

```text
application UoW
→ ABP Outbox persistence
→ transaction commit
→ ABP OutboxSender/background worker
→ NATS JetStream
→ subscriber
```

## Test infrastructure

Create a test-only EF Core persistence layer using SQLite.

Do not use EF InMemory for the transactional tests.

The test DbContext must implement:

```csharp
IHasEventOutbox
IHasEventInbox
```

with:

```csharp
DbSet<OutgoingEventRecord>
DbSet<IncomingEventRecord>
```

and:

```csharp
modelBuilder.ConfigureEventOutbox();
modelBuilder.ConfigureEventInbox();
```

Configure the standard ABP event boxes with:

```csharp
outbox.UseDbContext<TestEventBoxDbContext>();
inbox.UseDbContext<TestEventBoxDbContext>();
```

Use ABP's actual Outbox/Inbox infrastructure.

Do not manually call:

```text
PublishFromOutboxAsync
ProcessFromInboxAsync
```

in the end-to-end tests.

Configure the ABP event-box polling period to a short deterministic test value where necessary.

---

## Test 1.1

```text
Publishing_Inside_Committed_UoW_Should_Flow_Through_ABP_Outbox_To_JetStream
```

### Arrange

Subscribe to a typed event.

Begin an actual transactional ABP UoW.

Call:

```csharp
IDistributedEventBus.PublishAsync(...)
```

with Outbox enabled.

### Expected before UoW commit

Before:

```csharp
uow.CompleteAsync()
```

assert:

```text
handler invocation count = 0
no observable NATS delivery
```

The event must not escape the application transaction before commit.

### Expected after UoW commit

After UoW commit and ABP OutboxSender processing:

```text
JetStream receives event
subscriber receives event
handler invocation count = 1
```

Also verify:

```text
OutgoingEventInfo.Id
    ==
Nats-Msg-Id
```

where observable.

The Outbox record must no longer remain waiting after successful sending.

### Failure meaning

If the handler receives before commit:

```text
FAIL — transactional Outbox boundary is broken
```

---

## Test 1.2

```text
Rolled_Back_UoW_Should_Not_Publish_Outbox_Event
```

### Arrange

Begin transactional UoW.

Publish event using normal:

```csharp
IDistributedEventBus.PublishAsync(...)
```

Do not complete the UoW / force rollback.

### Expected

After waiting longer than the configured Outbox polling interval:

```text
handler invocation count = 0
no JetStream message
no committed Outbox record eligible for publication
```

This must prove:

```text
DB rollback
→ no broker side effect
```

---

## Test 1.3

```text
Outbox_Worker_Should_Delete_Record_Only_After_Successful_NATS_Publish
```

### Arrange

Persist an Outbox event through a committed UoW.

Temporarily make NATS unavailable before OutboxSender can successfully publish.

### Expected while broker unavailable

```text
event remains in Outbox
handler invocation count = 0
OutboxSender does not mark/delete event as successfully sent
```

Restore NATS.

### Expected after recovery

```text
same Outbox event is retried
event reaches JetStream
handler invocation count = 1
Outbox record is removed from waiting set only after successful publish
```

Do not manually republish the application event.

ABP must perform the retry.

---

# 2. True ABP Inbox End-to-End Integration

## Current gap

Current Inbox tests invoke:

```csharp
ProcessFromInboxAsync(...)
```

or test `AddToInboxAsync` through helpers.

That does not prove:

```text
JetStream
→ NATS consumer
→ ABP Inbox persistence
→ ABP InboxProcessor
→ transactional handler execution
```

---

## Test 2.1

```text
Incoming_NATS_Event_Should_Be_Processed_By_ABP_Inbox_Background_Processor
```

### Arrange

Enable a real EF-backed ABP Inbox.

Subscribe a handler.

Publish a real event through JetStream/NATS.

### Expected

The NATS transport must persist the event into ABP Inbox rather than directly invoking the application handler.

Eventually the real InboxProcessor must:

```text
read waiting event
→ begin transactional UoW
→ invoke NatsDistributedEventBus.ProcessFromInboxAsync
→ invoke handler
→ mark Inbox event processed
→ commit
```

Assert:

```text
handler invocation count = 1
DistributedEventReceived.Source = Inbox
Inbox status = Processed
```

The handler must not be invoked twice.

---

## Test 2.2

```text
Failing_Inbox_Handler_Should_Not_Be_Marked_Processed
```

### Arrange

Handler throws deliberately.

### Expected

On the first processing attempt:

```text
handler throws
transaction fails/rolls back according to ABP policy
Inbox event is NOT marked successfully processed
```

Assert the event remains eligible according to configured ABP Inbox failure policy.

Do not assert NATS-level retry here.

This test is specifically the:

```text
ABP Inbox
→ handler transaction
```

boundary.

---

## Test 2.3

```text
Successful_Inbox_Handler_Should_Be_Executed_Exactly_Once
```

Use a stable message identity.

After successful Inbox processing, attempt to introduce the same logical message ID again through the supported transport path.

Expected:

```text
business handler execution count = 1
```

Do not merely assert:

```text
Inbox.EnqueueCount == 1
```

The acceptance condition is **business execution exactly once under duplicate delivery**, because that is what Inbox idempotency is intended to protect.

If reproducing true ACK-loss redelivery requires a dedicated fault-injection seam, create a test-only seam rather than changing production semantics.

---

# 3. Connection Authentication Coverage

Use isolated local NATS server processes with explicit test configurations.

Do not reuse developer credentials.

All credentials must be static/random test-only values.

---

## Test 3.1

```text
UsernamePassword_Authentication_With_Valid_Credentials_Should_Connect
```

Start local NATS requiring:

```text
username
password
```

Configure:

```text
AbpNatsOptions.UserName
AbpNatsOptions.Password
```

### Expected

```text
connection reaches Open
JetStream GetAccountInfoAsync succeeds
publish succeeds
consume succeeds
```

Do not consider merely constructing `NatsOpts` a pass.

---

## Test 3.2

```text
UsernamePassword_Authentication_With_Invalid_Credentials_Should_Fail
```

Use wrong password.

### Expected

```text
connection cannot become usable
JetStream operation fails
no successful publish
no false healthy state
```

The test must have a bounded timeout.

---

## Test 3.3

```text
JwtSeed_Authentication_With_Valid_Test_Credentials_Should_Connect
```

Use a proper local NATS JWT/Seed fixture.

Test credentials may be checked into the test fixture only if they are clearly:

```text
LOCAL TEST ONLY
NOT PRODUCTION CREDENTIALS
```

Configure:

```text
AbpNatsOptions.Jwt
AbpNatsOptions.Seed
```

### Expected

```text
authenticated connection opens
JetStream account call succeeds
publish/consume succeeds
```

Do not replace this with a test that merely inspects `NatsAuthOpts`.

If a valid local JWT server fixture cannot currently be constructed without adding unrelated infrastructure, report that exact blocker instead of creating a fake unit test and claiming authentication is covered.

---

## Test 3.4

```text
JwtSeed_Authentication_With_Invalid_Seed_Should_Fail
```

### Expected

```text
authentication rejected
no successful JetStream call
no successful publish
```

---

# 4. Named Connection Resolution

## Test infrastructure

Run two isolated local NATS servers, for example:

```text
Server A → port A
Server B → port B
```

Both with JetStream.

Configure:

```text
Connections       → Server A

NamedConnections:
    Secondary     → Server B
```

---

## Test 4.1

```text
Default_Connection_Should_Resolve_Default_Server
```

### Expected

Calling:

```csharp
GetAsync()
```

or event-bus operation without `ConnectionName` must operate against Server A.

Prove it using an actual JetStream operation specific to Server A.

---

## Test 4.2

```text
Named_Connection_Should_Resolve_Configured_Server
```

Configure:

```text
ConnectionName = Secondary
```

### Expected

The stream and messages must appear on Server B.

They must not accidentally appear on Server A.

Acceptance:

```text
Server A does not contain test stream/message
Server B contains and processes test stream/message
```

---

## Test 4.3

```text
Multiple_Requests_For_Same_ConnectionName_Should_Reuse_Connection
```

### Expected

Within one `NatsConnectionPool`:

```text
GetAsync("Secondary")
GetAsync("Secondary")
```

return the same cached logical connection instance while it remains valid.

Different names must not alias to the same configured endpoint accidentally.

---

# 5. Broker Disconnect / Restart / Reconnect Recovery

Use an actual local `nats-server` process controlled by the test fixture.

Do not simulate this with mocks.

---

## Test 5.1

```text
Consumer_Should_Recover_After_NATS_Server_Restart
```

### Arrange

```text
start NATS
initialize event bus
subscribe handler
publish event A
```

Verify event A arrives.

Then:

```text
stop nats-server process
wait until disconnect is observed
restart nats-server on same port/data configuration
```

### Expected

Without recreating the application event bus:

```text
NATS connection reconnects
consumer resumes
publish event B succeeds
event B handler invocation = 1
```

No manual call to:

```text
InitializeAsync again
```

should be necessary unless NATS.Net's documented lifecycle requires it.

---

## Test 5.2

```text
Publish_During_Broker_Outage_Should_Fail_Or_Wait_According_To_NATS_Client_Semantics_Without_False_Success
```

Do not prescribe a result inconsistent with NATS.Net behavior.

Determine the documented 3.2.0 behavior first.

The invariant is:

```text
application must never report successful JetStream publish
if no publish ACK was obtained
```

Expected test assertion should be based on that invariant.

---

## Test 5.3

```text
Durable_Consumer_Should_Resume_Backlog_After_Server_Restart
```

Use persisted JetStream storage, not a completely fresh server data directory.

Scenario:

```text
consumer exists
message retained/unacked
broker restarts
same durable identity reconnects
```

Expected:

```text
same durable resumed
pending message eventually delivered
no creation of an unrelated consumer identity
```

---

# 6. Full Shutdown Lifecycle Coverage

The test must exercise the actual shutdown order.

---

## Test 6.1

```text
Application_Shutdown_Should_Stop_Consumers_Without_Hanging
```

Create active consumers.

Begin a message flow.

Invoke the normal shutdown lifecycle including:

```text
NatsDistributedEventBus.OnApplicationShutdownAsync
event-bus disposal
NatsConnectionPool.DisposeAsync
```

### Expected

All shutdown operations complete within a strict timeout, e.g.:

```text
<= 5 seconds
```

or another justified bounded value.

Assert:

```text
no ObjectDisposedException escapes
no deadlock
no hung ConsumeAsync loop
no unhandled background exception
```

---

## Test 6.2

```text
Disposed_EventBus_Should_No_Longer_Consume_Messages
```

### Arrange

Handler A belongs to old bus.

Dispose old bus completely.

Create new bus with same durable identity and Handler B.

Publish event.

### Expected

```text
Handler A invocation does not increase
Handler B receives event exactly once
```

This is stronger evidence against a leaked old consumer task than merely asserting that `Dispose()` returned.

---

## Test 6.3

```text
Fresh_EventBus_Should_Start_After_Previous_Bus_Was_Shut_Down
```

Expected:

```text
old bus shuts down
connection pool shuts down
fresh service provider/bus starts
same NATS server usable
new consumer initializes
new publish/consume succeeds
```

No process restart should be needed.

---

# 7. `PublishManyFromOutboxAsync` Coverage

Current implementation publishes the supplied Outbox events sequentially.

Cover both successful batch behavior and partial failure.

---

## Test 7.1

```text
PublishManyFromOutbox_Should_Publish_All_Events_With_Original_MessageIds
```

Create three distinct:

```csharp
OutgoingEventInfo
```

records with known IDs.

Call:

```csharp
PublishManyFromOutboxAsync(...)
```

### Expected

All three reach JetStream.

For every event:

```text
Nats-Msg-Id == OutgoingEventInfo.Id.ToString()
```

All three events contain their correct:

```text
event name
payload
correlation ID
```

where supplied.

---

## Test 7.2

```text
PublishManyFromOutbox_Should_Emit_One_Outbox_Sent_Notification_Per_Event
```

Capture:

```csharp
DistributedEventSent
```

### Expected

For N input events:

```text
notification count = N
```

Every notification:

```text
Source = Outbox
correct EventName
correct EventData
```

No duplicate notifications.

---

## Test 7.3

```text
PublishManyFromOutbox_Should_Stop_On_Partial_Failure
```

Use deterministic test-only fault injection.

Example:

```text
event 1 → base publish succeeds
event 2 → injected exception
event 3 → must not be attempted
```

### Expected

```text
event 1 publish attempted/succeeded
event 2 throws
event 3 not published
PublishManyFromOutboxAsync propagates failure
```

It must not falsely return success.

Do not swallow the exception.

---

## Test 7.4

```text
Retry_After_Partial_Batch_Failure_Should_Not_Create_Duplicate_Business_Delivery
```

This test should reflect how ABP OutboxSender behaves when batch deletion did not occur.

Retry the original Outbox batch with the same stable IDs.

Expected:

```text
previously successful event retains same Nats-Msg-Id
JetStream deduplication applies within its duplicate window
eventually all events are delivered
no duplicate business execution for the already-published logical event
```

If exact behavior depends on configured JetStream duplicate window, configure the test explicitly rather than relying on an undocumented server default.

---

# 8. Health Check Coverage

Test the actual:

```csharp
NatsHealthCheck
```

rather than mocking it.

---

## Test 8.1

```text
HealthCheck_Should_Be_Healthy_When_NATS_And_JetStream_Are_Available
```

### Expected

Against running JetStream-enabled NATS:

```text
HealthCheckResult.Status == Healthy
```

This test is important because the implementation currently checks connection state before calling JetStream.

If a fresh but valid connection reports unhealthy because it has not yet established a connection, treat that as a real health-check bug and fix the health check rather than weakening the test.

---

## Test 8.2

```text
HealthCheck_Should_Be_Unhealthy_When_Server_Is_Unreachable
```

Point to a known-unused local port.

### Expected

```text
Status == Unhealthy
```

within a bounded timeout.

It must not hang indefinitely.

---

## Test 8.3

```text
HealthCheck_Should_Be_Unhealthy_When_NATS_Is_Running_Without_JetStream
```

Start NATS without:

```text
-js
```

### Expected

Core NATS may be reachable, but:

```text
HealthCheckResult.Status == Unhealthy
```

because this package's event bus requires JetStream.

This distinguishes:

```text
NATS reachable
```

from:

```text
NATS + JetStream operational
```

---

# 9. `DistributedEventReceived` Exact Coverage

Do not merely assert that a handler ran.

Capture the actual local:

```csharp
DistributedEventReceived
```

event.

---

## Test 9.1

```text
Typed_Direct_Event_Should_Emit_One_DistributedEventReceived_From_Direct
```

Publish a typed event without Inbox.

Expected exactly one notification:

```text
Source = Direct
EventName = typed event's ABP event name
EventData = typed payload
```

Handler invocation:

```text
1
```

Notification count:

```text
1
```

---

## Test 9.2

```text
Typed_Inbox_Event_Should_Emit_One_DistributedEventReceived_From_Inbox
```

Use the real Inbox flow from item 2.

Expected:

```text
Source = Inbox
EventName = typed event name
EventData = typed payload
notification count = 1
handler count = 1
```

No Direct-sourced notification should be emitted for the final Inbox handler execution.

---

## Test 9.3

```text
Dynamic_Inbox_Event_Should_Emit_Actual_Event_Name_And_Raw_Event_Data
```

Publish:

```text
Identity.User.Created
```

through dynamic Inbox flow.

Expected:

```text
Source = Inbox
EventName = Identity.User.Created
```

not:

```text
Identity.User.*
```

and:

```text
EventData = underlying dynamic payload
```

not a nested `DynamicEventData` wrapper unless ABP's reference semantics explicitly require otherwise.

---

# 10. Real Multi-Process Fan-Out / Replica Verification

This is lower priority than items 1-9 but should be added before calling the package heavily battle-tested.

Do not simulate separate services merely by creating two bus instances in the same DI container.

Create a small test-host executable or child-process mode.

Each child must run an independent:

```text
ServiceProvider
NatsConnection
NatsDistributedEventBus
process
```

Parent test coordinates readiness and results.

---

## Test 10.1

```text
Two_Processes_With_Same_ClientName_Should_Act_As_One_Logical_Service
```

Start:

```text
Process A → ClientName = Billing
Process B → ClientName = Billing
```

Wait until both signal READY.

Publish e.g. 50 uniquely identified events.

### Expected

Across A+B combined:

```text
each logical event handled exactly once
total handled IDs = 50
duplicate IDs = 0
```

Do NOT require a 25/25 distribution.

NATS may distribute unevenly.

The required semantic is:

```text
same ClientName
→ shared durable
→ one logical service delivery
```

---

## Test 10.2

```text
Two_Processes_With_Different_ClientNames_Should_Each_Receive_Full_Event_Set
```

Start:

```text
Process A → ClientName = Billing
Process B → ClientName = Notifications
```

Wait for both READY before publishing because:

```text
Interest retention
+
InitialDeliveryPolicy.New
```

make consumer readiness significant.

Publish 50 unique events.

### Expected

Process A:

```text
50 unique IDs
```

Process B:

```text
50 unique IDs
```

Across each process:

```text
no duplicates
```

Required semantic:

```text
different ClientName
→ independent durable consumers
→ full fan-out
```

---

# 11. Code Coverage Measurement

The test project already references:

```text
coverlet.collector
```

Use the existing collector.

Run after all coverage additions:

```powershell
dotnet test TrueParser.Abp.Nats.slnx `
  --configuration Release `
  --collect:"XPlat Code Coverage"
```

Locate the generated Cobertura XML.

Report at minimum:

```text
line coverage
branch coverage
```

for:

```text
TrueParser.Abp.Nats
TrueParser.Abp.EventBus.Nats
```

separately if the report permits.

Do not set an arbitrary percentage target merely to achieve a number.

Do not add meaningless tests just to raise coverage.

The purpose is to identify untested production branches.

After the first measurement, inspect uncovered production branches in:

```text
NatsConnectionPool
JetStreamContextAccessor
NatsHealthCheck
NatsDistributedEventBus
module/options validation
```

Classify uncovered code as:

```text
production-critical → add meaningful test
error-only/unreachable platform branch → document
trivial property/module registration → low priority
```

Record the resulting coverage baseline in the verification report.

Coverage measurement completed on 2026-09-13 using the existing
`coverlet.collector` package and the full live suite:

| Package | Line coverage | Branch coverage |
|---|---:|---:|
| `TrueParser.Abp.Nats` | 95.83% | 96.15% |
| `TrueParser.Abp.EventBus.Nats` | 85.79% | 69.15% |
| Combined report | 87.03% | 72.08% |

Uncovered production behavior was reviewed as follows:

| Area | Classification | Review result |
|---|---|---|
| `NatsConnectionPool` disposal exception catch | error-only defensive branch | Documented; disposal failures are intentionally swallowed during cleanup. |
| `NatsHealthCheck` non-open state after a successful JetStream probe | lifecycle-defensive branch | Documented; unreachable after a successful probe in the tested client lifecycle. |
| `NatsDistributedEventBus` subscribe/unsubscribe duplicate and no-op paths | low-priority registration branches | Existing thread-safety and lifecycle coverage exercises the meaningful behavior; no artificial tests added. |
| `NatsDistributedEventBus` retry, startup-timeout, and cancellation catches | error-only/lifecycle-defensive branches | Documented; live recovery and shutdown tests cover successful recovery and bounded termination. |
| `ParseMaxAge` and `ParsePrefetchCount` invalid-input branches | configuration-error/default branches | Documented; valid configured behavior is covered, and invalid values safely fall back to unset. |
| subject-prefix, matcher, and consumer-validation failure branches | error-only validation paths | Existing validation tests cover the externally relevant failures; remaining branches are defensive detail. |
| ABP module initialization registration | trivial module registration | Low priority; production module startup is covered by all live event-bus tests. |

The public string-based publish overload was identified as meaningful and
covered with real JetStream tests for both registered typed events and unknown
dynamic events. No uncovered production branch required another production
change after this review. The generated Cobertura file remains under
`TestResults/` and is intentionally not committed.

Do not commit:

```text
TestResults/
coverage XML
generated HTML reports
```

unless explicitly required.

---

# Required execution order

Execute exactly:

```text
1. ABP Outbox end-to-end
2. ABP Inbox end-to-end
3. authentication
4. named connections
5. disconnect/restart recovery
6. shutdown lifecycle
7. PublishManyFromOutbox
8. health checks
9. DistributedEventReceived
10. multi-process topology
11. coverage measurement
```

For each numbered item:

```text
add focused RED test
run focused test
implement test fixture / smallest required production fix
run focused test GREEN
run all broker-free tests
run all RUN_NATS_TESTS=true tests
STOP
```

Do not implement the next numbered item until the current one is verified.

---

# General acceptance rules

Every new test must prove an externally meaningful invariant.

Do not accept tests whose only assertion is:

```text
object != null
option was assigned
method did not throw
consumer config contains value
```

when the production requirement is behavioral.

Prefer assertions such as:

```text
message delivered exactly once
message not delivered before commit
rollback prevents publication
event remains in Outbox after publish failure
Inbox record becomes Processed only after handler success
same ClientName shares delivery
different ClientName fans out
connection recovers after broker restart
shutdown prevents old consumer from receiving
wrong credentials cannot use JetStream
health check accurately reports unavailable JetStream
```

All live tests must clean up:

```text
child processes
NATS server processes
temporary configuration files
temporary SQLite databases
streams
consumers
temporary JetStream storage directories
```

even on test failure.

Use `try/finally`, `IAsyncLifetime`, or appropriate fixture disposal.

---

# Final verification report

At the end report:

```text
previous test count: 24
new test count: 33 total
broker-free passed: 2
live JetStream passed: 77
failed: 0
skipped: 75 broker-free; 0 live

Outbox E2E: PASS/FAIL
Inbox E2E: PASS/FAIL
username/password auth: PASS/FAIL
JWT/Seed auth: PASS/FAIL/BLOCKED with exact reason
named connections: PASS/FAIL
broker restart recovery: PASS/FAIL
shutdown lifecycle: PASS/FAIL
PublishManyFromOutbox: PASS/FAIL
health checks: PASS/FAIL
DistributedEventReceived: PASS/FAIL
multi-process same ClientName: PASS/FAIL
multi-process different ClientName: PASS/FAIL

line coverage: 95.83% (`TrueParser.Abp.Nats`), 85.79% (`TrueParser.Abp.EventBus.Nats`), 87.03% combined
branch coverage: 96.15% (`TrueParser.Abp.Nats`), 69.15% (`TrueParser.Abp.EventBus.Nats`), 72.08% combined

production defects discovered: typed direct events did not emit `DistributedEventReceived`; fixed and covered
production files changed: `NatsDistributedEventBus.cs`
test files added/changed: health checks, notification parity, multi-process replica host, string publish overload coverage

remaining known untested production behavior: defensive exception and invalid-input branches classified in the item 11 review; no pending proposal test
```

Do not declare production coverage complete if any test was replaced by a mock that avoids the transport boundary it was intended to validate.
