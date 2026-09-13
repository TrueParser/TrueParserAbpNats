# Phase 1 - ABP 10.6 Compatibility and NATS JetStream Reliability Hardening

Status: `[ ]` Pending.

Problem statement:

`TrueParser.Abp.EventBus.Nats` is intended to be a reusable NATS JetStream
transport for ABP Framework that can replace `Volo.Abp.EventBus.RabbitMQ` with
minimal application-code changes. The proposal
[`guides/proposals/bug-fixing-hardening.md`](guides/proposals/bug-fixing-hardening.md)
identifies compatibility and reliability gaps around package alignment,
durable consumer identity, stable message identity, dynamic events,
redelivery, stream configuration, live verification, and documentation.

The transport must remain structurally close to ABP's distributed event-bus
behavior wherever the difference is not inherently NATS-specific.

Objective:

Harden `TrueParser.Abp.EventBus.Nats` for ABP Framework 10.6 while preserving
the existing ABP event model and the smallest practical migration surface:

```text
NuGet package
module dependency
broker configuration
```

Existing application code must continue to use:

```text
IDistributedEventBus
IDistributedEventHandler<T>
AbpDistributedEventBusOptions
IHasEventOutbox
IHasEventInbox
```

JetStream remains the durable broker transport. ABP Outbox remains responsible
for the database-to-broker transactional boundary, and ABP Inbox remains
responsible for broker redelivery and application-side idempotency.

Scope and contract:

- Each slice below contains the implementation and its focused verification; do not split one proposal item into separate audit, implementation, test, and documentation slices.
- Every slice and every checklist item remains pending until the work is actually performed and verified.
- Use Graft for repository and subsystem context and Roslyn MCP as the primary semantic C# navigation and compiler-analysis tool.
- Preserve public ABP event contracts, application event DTOs, handler contracts, tenant propagation, correlation propagation, at-least-once delivery, and existing non-NATS developer workflows.
- Keep `NATS.Net 2.5.3` unchanged during the ABP baseline slice. The separately isolated 1.1.1 slice upgrades it to the approved `3.2.0` baseline only after 1.1 is complete and verified.

Explicit non-goals and safety boundaries:

- Do not redesign `IDistributedEventBus` or ABP's event model.
- Do not move Inbox or Outbox responsibilities into JetStream.
- Do not replace ABP Inbox/Outbox with NATS KV, Object Store, or a custom distributed transaction protocol.
- Do not change application event DTOs, application handlers, business logic, tenant isolation, or public API contracts.
- Do not claim exactly-once delivery. The target guarantee is at-least-once transport plus stable message identity, ABP Inbox idempotency, and transactional ABP Outbox behavior.
- Do not add a custom dead-letter queue framework in this phase.
- Do not upgrade NATS.Net in this phase.
- Do not generate or apply database migrations.
- Do not make external deployment or external-environment changes as part of implementation.
- Do not weaken, delete, skip, or reinterpret existing tests to make a slice pass.
- Stop and ask the maintainer if an equivalent behavior cannot be implemented without changing a listed contract.

Required implementation order:

```text
1.1  ABP 10.6 package alignment
1.2  Durable ClientName / consumer identity
1.3  Stable message/event identity
1.4  Actual dynamic event-name resolution
1.5  Dynamic events through ABP Inbox
1.6  DistributedEventSent parity
1.7  Initial DeliverPolicy behavior
1.8  Retention and fan-out configuration validation
1.9  Redelivery and poison-message controls
1.10 Existing stream configuration validation
1.11 Live JetStream release-gated tests
1.12 ABP RabbitMQ parity audit
1.13 NATS.Net version freeze verification
1.14 Documentation and public package contract
```

### 1.1 ABP 10.6 Package Baseline Alignment Slice

Status: `[x]` Completed.

Problem statement:

The proposal identifies the ABP package baseline as `10.3.0`, while the target
consumer environment is ABP `10.6.0`. Hardening against the wrong ABP base
classes can produce transport behavior that does not match the consuming
framework.

Scope:

- [x] Inventory every directly referenced ABP package in the solution and record its current version source.
- [x] Upgrade directly referenced ABP packages to `10.6.0`.
- [x] Keep `NATS.Net 2.5.3` unchanged.
- [x] Use Roslyn/compiler analysis to identify source compatibility errors caused by the baseline change.
- [x] Restore the solution and run the focused test project without changing transport semantics in this slice.

Completion criteria:

- [x] All direct ABP dependencies use the approved `10.6.0` baseline.
- [x] `NATS.Net` remains at `2.5.3`.
- [x] Restore succeeds and the solution builds with zero new compiler errors.
- [x] Existing tests compile and the focused test run result is recorded.
- [x] No transport behavior is intentionally changed beyond required ABP compatibility adjustments.

Verification record:

- `dotnet restore TrueParser.Abp.Nats.slnx` — succeeded.
- `dotnet build TrueParser.Abp.Nats.slnx --no-restore` — succeeded with 0 warnings and 0 errors.
- Roslyn `BuildSolution` for `TrueParser.Abp.EventBus.Nats.Tests.csproj` — succeeded with 0 warnings and 0 errors.
- `dotnet test test\TrueParser.Abp.EventBus.Nats.Tests --no-build` with `RUN_NATS_TESTS=true` — 8 passed, 0 failed.
- Resolved package inspection — direct ABP packages at `10.6.0`; `NATS.Net` and its NATS component packages at `2.5.3`.

### 1.1.1 NATS.Net 3.2.0 Major Upgrade Slice

Status: `[x]` Completed.

Problem statement:

The current `NATS.Net 2.5.3` dependency is an older 2.x baseline. `NATS.Net
3.2.0` is a stable major release, but the v3 line includes API and behavior
changes and the 3.2 release adds members to JetStream interfaces. Mixing that
upgrade with the ABP 10.6 package migration or the transport hardening fixes
would make compatibility failures difficult to attribute.

Starting and target version record:

- Old requested version: `2.5.3` from the central `Directory.Packages.props` pin.
- Old resolved version: `2.5.3` for `NATS.Net` and the resolved NATS component family.
- New requested version: `3.2.0` from the central `Directory.Packages.props` pin.
- New resolved version: `3.2.0` for `NATS.Net`, `NATS.Client.Abstractions`, `NATS.Client.Core`, `NATS.Client.Hosting`, `NATS.Client.JetStream`, `NATS.Client.KeyValueStore`, `NATS.Client.ObjectStore`, `NATS.Client.Serializers.Json`, `NATS.Client.Services`, `NATS.Client.Simplified`, and `NATS.Extensions.Microsoft.DependencyInjection`.

Official upstream compatibility audit:

The official NATS.Net release notes and stable tag inventory were reviewed from
`v2.5.3` through `v3.2.0`, including the published v2.5.x, v2.6.x, v2.7.x,
v2.8.x, v3.0.x, v3.1.x, and v3.2.0 releases. Tags without a published GitHub
release body were also checked and had no additional release notes to classify.

| Upstream change | Affected repository API/symbol | Classification | Applicability and action | Regression test |
| --- | --- | --- | --- | --- |
| v2.5.3 URL authentication support | `NatsConnectionPool.CreateConnection` | OPTIONAL NEW CAPABILITY | Username/password and JWT/Seed options are used; URL credentials are not configured. No change. | Existing live connection tests |
| v2.5.5-v2.5.6 JetStream publish/serializer fixes | `PublishToNatsAsync`, `INatsEventSerializer` boundary | JETSTREAM IMPACT; SERIALIZATION IMPACT | The repository uses byte payloads and the ABP serializer abstraction; no NATS serializer implementation is present. No source change. | Typed/dynamic live publish tests |
| v2.5.7 DI serializer and pending-channel defaults | Direct `NatsConnection` in `NatsConnectionPool` | PERFORMANCE/BACKPRESSURE IMPACT; SERIALIZATION IMPACT | NATS DI builders are not used. No `SubscribeAsync` channel is created directly. No option change. | Live concurrency tests |
| v2.5.9 ordered-consumer creation fix; v2.5.10 reconnect fix | `GetConsumerAsync`, `CreateOrUpdateConsumerAsync`, connection pool | JETSTREAM IMPACT; SHUTDOWN/LIFECYCLE IMPACT | The package uses a normal durable pull consumer and native reconnect behavior. No source change. | Live consumer and publish tests |
| v2.5.12-v2.5.16 consumer pause, inbox leak, socket factory, and opt-in Direct request/reply | `NatsConnectionPool`, `NatsDistributedEventBus` | NOT USED BY THIS REPOSITORY; SHUTDOWN/LIFECYCLE IMPACT | No request/reply, custom socket, pause, or inbox subscription API is used. No source change. | Live lifecycle and concurrency tests |
| v2.6.x consumer disposal/reconnect/503 fixes and JetStream retry corrections | `ConsumeAsync<byte[]>`, `AckAsync`, `NakAsync`, `PublishAsync` | JETSTREAM IMPACT; SHUTDOWN/LIFECYCLE IMPACT | Existing native APIs are retained; no retry or ACK policy was redesigned. | Full live JetStream suite |
| v2.7.0 serialization move, timeout exception change, and `INatsJSConsumer` interface return | `ProcessMessageAsync`, `GetTenantId`, consume loop | SOURCE-COMPATIBILITY IMPACT; SERIALIZATION IMPACT | NATS.Net 3.2 compilation required both message parameters to accept `INatsJSMsg<byte[]>`. No timeout handling or serializer rewrite was needed. | Roslyn build and live suite |
| v2.7.1 type forwarding for serializer interfaces | NATS serializer references | SOURCE-COMPATIBILITY IMPACT | No direct custom NATS serializer or old serializer assembly reference exists. No source change. | Solution build |
| v2.7.2 slow-consumer handling and UTF-8 subject encoding | `ConsumeAsync<byte[]>`, `GetSubjectName` | PERFORMANCE/BACKPRESSURE IMPACT; JETSTREAM IMPACT | The event bus uses JetStream consume and valid generated subjects; no explicit channel override is required. | Live concurrency and wildcard tests |
| v2.7.3 immediate consumer cancellation and interface optional-parameter changes | `ConsumeAsync<byte[]>(cancellationToken: ...)`, ACK/NAK calls | SHUTDOWN/LIFECYCLE IMPACT; SOURCE-COMPATIBILITY IMPACT | Existing immediate cancellation is preserved. Recompilation resolves the interface API; no drain option was enabled. | `Consumer_Should_Stop_And_Start_Again_After_Unsubscribe` |
| v2.8.0 subject validation, NATS.NKeys split, consumer-dispose drain, and reset APIs | `GetSubjectName`, `NatsAuthOpts`, consumer lifecycle | SOURCE-COMPATIBILITY IMPACT; JETSTREAM IMPACT; SHUTDOWN/LIFECYCLE IMPACT | Generated subjects contain no whitespace; JWT/Seed authentication remains supported through the package; drain/reset features are not enabled or used. | Live publish, consumer, and lifecycle tests |
| v2.8.1-v2.8.2 durable-create and ordered-push teardown fixes | `CreateOrUpdateConsumerAsync`, `ConsumeAsync` | JETSTREAM IMPACT; SHUTDOWN/LIFECYCLE IMPACT | The existing durable configuration supplies a name and is not an ordered push consumer. No source change. | Live retained-message and lifecycle tests |
| v3.0.0 .NET 10 support, context-aware serializer opt-ins, socket abstraction move, Direct request/reply default, channel defaults, explicit drain, and DI dependency changes | `NatsConnectionPool`, `JetStreamContextAccessor`, `NatsHealthCheck`, event bus consume/publish paths | SOURCE-COMPATIBILITY IMPACT; PERFORMANCE/BACKPRESSURE IMPACT; SHUTDOWN/LIFECYCLE IMPACT; OPTIONAL NEW CAPABILITY | `net10.0` is supported. No request/reply, direct subscription, custom serializer, socket implementation, DI builder, or drain option is used. Existing behavior is preserved. | Roslyn build and full live suite |
| v3.0.1 JetStream list cancellation fix and opt-in W3C baggage | No list enumeration or OTel registration | NOT USED BY THIS REPOSITORY; OPTIONAL NEW CAPABILITY | No applicable API is used. No source or package addition. | Package/build verification |
| v3.1.0 `OnSubscribed` callback and dependency audit | `ConsumeAsync<byte[]>` consumer loop | NOT USED BY THIS REPOSITORY; OPTIONAL NEW CAPABILITY | The event bus relies on JetStream consumer startup, not Core `SubscribeAsync` handoff. No source change. | Live consumer startup tests |
| v3.1.1 OTel receive fixes and object-store/empty-payload fixes | No OTel, object-store, or direct-get usage | NOT USED BY THIS REPOSITORY; OPTIONAL NEW CAPABILITY | No applicable API is used. No source change. | Full live suite |
| v3.2.0 JetStream reset/sourcing fields and new members on `INatsJSStream`/`INatsJSConsumer` | Interfaces consumed by `NatsDistributedEventBus`; no direct implementers | JETSTREAM IMPACT; SOURCE-COMPATIBILITY IMPACT | Callers are unaffected and the repository supplies no direct test double or implementation. No source change. | Roslyn Release build and full live suite |

Scope:

- [x] Start only after slice 1.1 has completed its ABP 10.6 restore, build, and focused-test checks.
- [x] Inventory direct and resolved NATS package references and record the current `2.5.3` baseline.
- [x] Upgrade the centrally managed `NATS.Net` package to `3.2.0` and keep all resolved NATS package versions internally consistent.
- [x] Use the official NATS.Net v3 upgrade notes, Roslyn MCP, and Graft to inspect affected symbols, JetStream interfaces, connection setup, headers, publishing, consuming, and test doubles.
- [x] Resolve only source or test compatibility changes required by the NATS.Net 3.2.0 upgrade.
- [x] Verify that the repository's .NET 10 target remains supported by the selected package.
- [x] Restore, build the solution, and run the focused test project with no unrelated transport-hardening changes.
- [x] Run live NATS tests when the environment gate and JetStream server are available; otherwise record that they were gated or skipped.
- [x] Do not silently downgrade to `2.5.3` if the upgrade fails; record the exact incompatibility and required maintainer decision.

Completion criteria:

- [x] The resolved NATS client baseline is exactly `3.2.0`.
- [x] No unintended mixed NATS package versions remain.
- [x] Restore succeeds and the solution builds with zero new compiler errors.
- [x] Existing focused tests compile and their result is recorded.
- [x] Live NATS test execution or gating status is recorded explicitly.
- [x] No event-bus, JetStream, acknowledgement, identity, retention, or delivery semantics are intentionally changed in this slice.
- [x] The NATS.Net `3.2.0` baseline is frozen for the remainder of Phase 1 unless separately approved.

Verification record:

- `dotnet restore TrueParser.Abp.Nats.slnx` — succeeded.
- Immediate post-upgrade `dotnet build TrueParser.Abp.Nats.slnx --no-restore` — found one required concrete-to-interface incompatibility in `ProcessMessageAsync`.
- Compatibility fix: changed only `ProcessMessageAsync` and `GetTenantId` parameters from `NatsJSMsg<byte[]>` to `INatsJSMsg<byte[]>`.
- Final `dotnet build TrueParser.Abp.Nats.slnx --no-restore` — succeeded with 0 warnings and 0 errors.
- Roslyn `BuildSolution` for the test project in Debug and Release — succeeded with 0 warnings and 0 errors.
- Normal `dotnet test test\TrueParser.Abp.EventBus.Nats.Tests --no-build` — 1 passed, 8 explicitly skipped by the NATS gate.
- Live `RUN_NATS_TESTS=true dotnet test test\TrueParser.Abp.EventBus.Nats.Tests --no-build` — 9 passed, 0 failed.
- `dotnet pack` for both library projects — both packages created successfully.
- Package metadata — `TrueParser.Abp.Nats` depends on `NATS.Net 3.2.0`; the event-bus package depends on the core package and ABP `10.6.0`.
- Local NATS server was reachable on port `4222` during the live run; its version could not be read because the monitoring endpoint on port `8222` was unavailable.

### 1.2 Durable Consumer and Service Identity Slice

Status: `[ ]` Pending.

Problem statement:

Durable consumer names currently derive from stream and event name but do not
contain an application/service identity. Different services subscribing to the
same event can therefore resolve to one consumer and compete for delivery
instead of receiving independent fan-out copies.

Scope:

- [ ] Add an explicit event-bus consumer identity option, `ClientName`, without changing the existing NATS connection option contract.
- [ ] Define and document the fallback to `AbpNatsOptions.ClientName` where appropriate.
- [ ] Generate sanitized durable names from `{StreamName}_{ClientName}_{EventName}`.
- [ ] Fail startup with an actionable configuration exception when a stable valid consumer identity cannot be resolved; do not use a random process identity.
- [ ] Add focused live tests for different service identities, same-identity replicas, and restart/resume behavior.

Completion criteria:

- [ ] Different `ClientName` values receive independent copies of the same event.
- [ ] Replicas with the same `ClientName` share one logical durable consumer and do not duplicate service delivery.
- [ ] Restart with the same identity resumes the same durable consumer.
- [ ] Invalid or missing stable identity fails clearly.

### 1.3 Stable Message and Event Identity Slice

Status: `[ ]` Pending.

Problem statement:

The receive path does not consistently pass a stable message ID into ABP Inbox,
which leaves duplicate detection incomplete across outbox publishing,
JetStream delivery, and redelivery.

Scope:

- [ ] Use `OutgoingEventInfo.Id` as the transport message ID for Outbox publishing.
- [ ] Generate a stable direct-publish ID with the repository's `IGuidGenerator`.
- [ ] Publish the ID in the `Nats-Msg-Id` header and preserve any required library or ABP message-id header.
- [ ] Extract the ID on consumption and pass it to `AddToInboxAsync` unchanged.
- [ ] Preserve correlation ID propagation across the same path.
- [ ] Add focused tests for outbox identity, direct identity, duplicate publishing, redelivery, Inbox deduplication, and correlation propagation.

Completion criteria:

- [ ] The identity chain is preserved from ABP Outbox through NATS and into ABP Inbox.
- [ ] Repeated delivery of the same message does not execute the business handler twice when Inbox deduplication applies.
- [ ] The transport does not rely on JetStream deduplication as a replacement for ABP Inbox.
- [ ] Correlation IDs remain unchanged.

### 1.4 Actual Dynamic Event-Name Resolution Slice

Status: `[ ]` Pending.

Problem statement:

Wildcard subscriptions currently use the subscription pattern during message
processing. A handler subscribed to `Identity.User.*` must receive the actual
published event name, such as `Identity.User.Created`, not the wildcard pattern.

Scope:

- [ ] Derive the actual event name from `msg.Subject` by removing the configured subject prefix.
- [ ] Use the subscription pattern only for consumer filtering.
- [ ] Preserve exact event names for direct dynamic subscriptions and typed events.
- [ ] Extend the wildcard integration coverage to assert the resulting `DynamicEventData.EventName` values exactly.

Completion criteria:

- [ ] Wildcard handlers receive the actual published event name for each message.
- [ ] Subject-prefix removal is deterministic and does not alter event-name segments.
- [ ] Existing exact and typed event behavior remains unchanged.

### 1.5 Dynamic Events Through ABP Inbox Slice

Status: `[ ]` Pending.

Problem statement:

Inbox processing can return early when a dynamic event has no CLR event type in
the registered event-type map, preventing a matching dynamic handler from
running.

Scope:

- [ ] Match the ABP RabbitMQ behavior for typed and dynamic Inbox paths.
- [ ] Deserialize known typed events using their CLR type and invoke typed handlers.
- [ ] For an unknown CLR type with a matching dynamic handler, deserialize raw data, wrap it in `DynamicEventData` using the actual event name, and invoke the matching dynamic handlers.
- [ ] Preserve existing wildcard event-name matching logic.
- [ ] Add focused tests for exact dynamic Inbox events, wildcard dynamic Inbox events, typed Inbox events, and unknown events with no unrelated handler invocation.

Completion criteria:

- [ ] Exact dynamic events are processed through Inbox.
- [ ] Wildcard dynamic events are processed through Inbox with the actual event name.
- [ ] Typed events still use their typed deserialization and handlers.
- [ ] Unknown events do not invoke unrelated handlers.

### 1.6 DistributedEventSent Notification Parity Slice

Status: `[ ]` Pending.

Problem statement:

The transport method explicitly triggers `DistributedEventSent` even though
ABP's `DistributedEventBusBase.PublishAsync` already triggers the notification,
which can produce duplicate direct-publish notifications.

Scope:

- [ ] Make `PublishToEventBusAsync` transport-only after serialization and JetStream publication.
- [ ] Remove the duplicate transport-level notification.
- [ ] Preserve the explicit Outbox-sourced notification in `PublishFromOutboxAsync` where ABP parity requires it.
- [ ] Add focused tests that count direct and Outbox notifications and assert their sources.

Completion criteria:

- [ ] Direct publishing produces exactly one `DistributedEventSent` notification.
- [ ] Outbox publishing produces exactly one correctly sourced notification.
- [ ] No unrelated publish or handler behavior changes.

### 1.7 Explicit Initial Consumer Delivery Policy Slice

Status: `[ ]` Pending.

Problem statement:

New durable consumers currently use `DeliverPolicy.All`, which can replay old
retained messages to a newly deployed service and differs from normal RabbitMQ
queue creation semantics.

Scope:

- [ ] Add an explicit initial consumer delivery-policy option.
- [ ] Default new consumers to `New` for RabbitMQ-compatible behavior.
- [ ] Preserve resume/backlog behavior for an existing durable consumer.
- [ ] Support explicit `All` configuration for intentional retained-message replay.
- [ ] Add focused tests proving default new-only behavior and explicit historical replay.

Completion criteria:

- [ ] A brand-new consumer does not receive messages published before its creation under default configuration.
- [ ] An existing durable consumer resumes its stored delivery position.
- [ ] Explicit `All` configuration receives retained historical messages.
- [ ] The option and behavior are documented.

### 1.8 Retention and Fan-Out Configuration Validation Slice

Status: `[ ]` Pending.

Problem statement:

`Interest` retention is appropriate for ABP pub/sub fan-out, while
`WorkQueuePolicy` can silently change semantics by removing a message after one
worker group processes it.

Scope:

- [ ] Preserve `Interest` as the default retention policy.
- [ ] Support `Limits` only as an intentional advanced configuration.
- [ ] Reject `WorkQueuePolicy` for the standard event-bus path, or obtain explicit maintainer direction for a clearly warned opt-in.
- [ ] Add focused tests for the default, independent fan-out, and invalid WorkQueue configuration behavior.

Completion criteria:

- [ ] Default retention remains `Interest`.
- [ ] Independent service identities receive the same event under the supported fan-out configuration.
- [ ] Unsupported WorkQueue configuration fails clearly or has an explicitly approved opt-in warning.

### 1.9 Redelivery and Poison-Message Controls Slice

Status: `[ ]` Pending.

Problem statement:

Handler failures currently result in a negative acknowledgement but the package
does not expose durable controls for acknowledgement wait, maximum delivery,
or backoff. A permanently failing handler can therefore be redelivered without
a clear bounded-delivery policy.

Scope:

- [ ] Expose the required JetStream consumer options for `AckWait`, `MaxDeliver`, and `BackOff`.
- [ ] Keep defaults conservative and aligned with native NATS behavior unless an explicit requirement justifies an override.
- [ ] Preserve ACK on successful processing and NAK/redelivery on handler failure.
- [ ] Do not add a custom dead-letter queue framework.
- [ ] Add focused live tests for transient recovery, configured maximum delivery, backoff, and successful acknowledgement stopping redelivery.

Completion criteria:

- [ ] Transient handler failure is redelivered and can succeed.
- [ ] Configured `MaxDeliver` is honored.
- [ ] Configured backoff is applied by the consumer.
- [ ] Successful processing ACKs the message and stops redelivery.
- [ ] Poison-message behavior and the absence of a built-in custom DLQ are documented.

### 1.10 Existing Stream Configuration Validation Slice

Status: `[ ]` Pending.

Problem statement:

Stream initialization currently treats an existing stream as success without
verifying that its subjects and critical settings match the configured event
bus. This can silently accept incompatible stream configuration.

Scope:

- [ ] Fetch an existing stream before deciding whether to create it.
- [ ] Create a missing stream using the configured stream name, subject prefix, retention, replica count, and MaxAge where applicable.
- [ ] Validate at minimum stream name, subjects/subject prefix, and retention on an existing stream.
- [ ] Report replica-count and MaxAge differences where applicable.
- [ ] Fail startup with an actionable configuration error for incompatible settings.
- [ ] Do not mutate an existing stream automatically unless an explicit management option is separately approved.
- [ ] Add focused tests for creation, matching restart, incompatible subject, incompatible retention, and valid restart behavior.

Completion criteria:

- [ ] Missing streams are created correctly.
- [ ] Matching existing streams start successfully.
- [ ] Incompatible critical configuration fails deterministically with useful details.
- [ ] Existing streams are not silently mutated.

### 1.11 Live JetStream Release-Gated Test Slice

Status: `[ ]` Pending.

Problem statement:

The repository has real NATS integration and thread-safety tests, but the
existing environment gate can leave a package publication path without testing
the actual transport.

Scope:

- [ ] Add a CI/release job that starts a local NATS Server with JetStream enabled.
- [ ] Set `RUN_NATS_TESTS=true` for the release-gating test run.
- [ ] Make package publication depend on successful live transport tests.
- [ ] Keep fast non-NATS developer tests usable without a broker.
- [ ] Cover the required live matrix: typed and dynamic events, wildcard names, service fan-out, same-identity replicas, durable restart, stable IDs, Inbox deduplication, redelivery, outage recovery, stream restart, concurrency, tenant headers, and correlation IDs.
- [ ] Capture complete test output and retain a machine-readable result when the console output is insufficient.

Completion criteria:

- [ ] The live test job starts and stops its isolated JetStream server reliably.
- [ ] The release path cannot publish when required live transport tests fail.
- [ ] Ordinary developer test execution remains possible without a broker.
- [ ] The live reliability matrix is represented by executable tests or an explicitly documented approved exception.

### 1.12 ABP RabbitMQ Parity Audit Slice

Status: `[ ]` Pending.

Problem statement:

The package is marketed as a low-change RabbitMQ replacement, so behavior that
differs from ABP's `RabbitMqDistributedEventBus` must be explicitly understood
and justified rather than being accidental parity gaps.

Scope:

- [ ] Use ABP 10.6 `RabbitMqDistributedEventBus` as the behavioral reference.
- [ ] Compare publishing, Outbox, Inbox, unit-of-work, subscription, dynamic-event, tenant/correlation, and distributed-event notification behavior.
- [ ] Use Roslyn MCP for symbol and call-flow analysis and Graft for repository context.
- [ ] Classify each difference as broker-specific, intentional NATS enhancement, or bug/parity gap.
- [ ] Fix the gaps that belong in this phase or record an explicit maintainer-approved follow-up.
- [ ] Record the parity matrix in repository documentation or this `TASK.md` without claiming unverified equivalence.

Completion criteria:

- [ ] Every material behavior difference has an explicit reason.
- [ ] Required parity gaps from this phase are fixed and focused-tested.
- [ ] Any remaining difference has a documented owner, rationale, and follow-up scope.

### 1.13 NATS.Net Version Baseline Verification Slice

Status: `[ ]` Pending.

Problem statement:

After the isolated 1.1.1 client migration, later reliability work must not
silently introduce another NATS.Net version change or a mixed package graph.

Scope:

- [ ] Verify that all preceding implementation and verification work keeps `NATS.Net 3.2.0`.
- [ ] Inspect direct and resolved package references for an unintended NATS.Net downgrade, upgrade, or version split.
- [ ] Record the frozen `3.2.0` baseline and any package-resolution limitation.
- [ ] Create no client-migration code or compatibility layer in this phase.

Completion criteria:

- [ ] The resolved NATS.Net baseline remains unchanged.
- [ ] No NATS.Net major-version migration is mixed into this phase.
- [ ] A later client upgrade, if desired, is isolated as a separate work item.

### 1.14 Documentation and Public Package Contract Slice

Status: `[ ]` Pending.

Problem statement:

The library is intended for use outside TrueParser, so configuration and
delivery semantics must be explicit rather than dependent on hidden application
assumptions. RabbitMQ replacement claims must be supported by precise
documentation.

Scope:

- [ ] Document `ClientName` and durable consumer identity.
- [ ] Document same-service replica sharing and cross-service fan-out.
- [ ] Document Interest retention and initial delivery policy.
- [ ] Document ABP Outbox and Inbox responsibilities separately from JetStream.
- [ ] Document `Nats-Msg-Id`, correlation/tenant propagation, and redelivery behavior.
- [ ] Document `MaxDeliver`, `AckWait`, `BackOff`, stream ownership/validation, and `ReplicaCount` recommendations.
- [ ] Update README and relevant repository documentation only after behavior is implemented and verified.
- [ ] Keep migration instructions focused on package, module, and broker configuration changes.

Completion criteria:

- [ ] Documentation describes only verified behavior.
- [ ] The public configuration contract is complete and consistent with the implementation.
- [ ] Documentation does not claim that JetStream replaces ABP Inbox/Outbox or provides exactly-once delivery.
- [ ] Migration guidance remains accurate for a third-party ABP application.

### Phase-level completion criteria

- [ ] ABP 10.6 compatibility is verified.
- [ ] Different service identities receive independent copies and same-service replicas share a durable consumer.
- [ ] Durable restart, stable message identity, Inbox deduplication, dynamic events, and wildcard event names are verified.
- [ ] Direct and Outbox `DistributedEventSent` notifications are exactly once and correctly sourced.
- [ ] Initial delivery, retention, redelivery, and poison-message behavior are explicit and tested.
- [ ] Existing incompatible stream configuration fails deterministically.
- [ ] Live JetStream tests are release-gated while non-NATS tests remain usable locally.
- [ ] The RabbitMQ parity matrix is complete and all remaining differences are explained.
- [ ] `NATS.Net 3.2.0` remains frozen after the isolated 1.1.1 upgrade slice.
- [ ] Documentation and the public package contract reflect verified implementation behavior.
- [ ] No application event contracts, ABP Inbox/Outbox responsibilities, database migrations, or exactly-once claims were changed.

Handoff requirements:

- [ ] Report affected files and verified commands/results.
- [ ] State whether live NATS tests were gated or executed.
- [ ] Record remaining environment-dependent limitations and any approved follow-up work.
- [ ] Include one proposed Conventional Commit message without creating the commit.
