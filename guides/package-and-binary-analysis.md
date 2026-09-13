# Package and Binary Analysis Notes

This guide records the repeatable workflow for future ABP, NATS.Net, and
RabbitMQ-parity work in this repository. It is an investigation and
verification guide; it does not authorize production changes by itself.

## Before changing code

1. Read `AGENTS.md`, the applicable proposal, and the current `TASK.md`.
2. Use Graft before searching or opening source files:

   ```text
   graft map
   graft ask "the behavior or symbol being investigated" --source
   graft grep "an exact identifier"       # exhaustive occurrence search
   graft callers "SymbolName"             # call-flow and blast-radius check
   graft skeleton path/to/file.cs         # API surface overview
   ```

3. Check the working tree and preserve unrelated user changes.
4. Write down the current requested and resolved versions before editing.

For an upgrade or parity gap, keep the investigation sequence explicit:

```text
characterize current behavior
→ add a focused RED regression
→ make the smallest compatible fix
→ prove GREEN
→ run the required broader suites
→ document the result
```

## Resolve the actual package graph

This repository uses Central Package Management. Package versions belong in
`Directory.Packages.props`; do not add a second version to an individual
project file.

Use the project or solution commands below rather than relying on an assumed
version:

```powershell
dotnet restore TrueParser.Abp.Nats.slnx
dotnet list TrueParser.Abp.Nats.slnx package --include-transitive
dotnet build TrueParser.Abp.Nats.slnx --configuration Release --no-restore
```

Record all relevant direct and resolved packages, including the NATS client
family. A successful restore is not enough if different projects resolve
incompatible client versions.

For a package migration, verify:

```text
old requested version
old resolved version
new requested version
new resolved version
mixed transitive versions
```

Do not run `dotnet add package` when it would duplicate a centrally managed
reference. Do not silently downgrade after a failed migration.

## Inspect ABP packages and binaries

Use ABP's tagged source as the behavioral authority whenever it is available.
For ABP parity work, inspect the exact target tag and compare at least:

```text
DistributedEventBusBase
EventBusBase.TriggerHandlerAsync
RabbitMqDistributedEventBus
OutboxSender
Inbox processing/background-worker registration
```

The NuGet package is still useful for confirming what the compiled consumer
actually receives. Find the local package cache without copying it into the
repository:

```powershell
dotnet nuget locals global-packages --list
dotnet nuget locals http-cache --list
```

Package assets normally live below the reported global-packages directory.
Inspect the `.nuspec`, dependency groups, and the `lib`/`ref` assemblies for
the target framework. Prefer the target framework used by this solution.

When source and binary behavior appear different, record both:

```text
source URL and tag
package ID and version
assembly path
type/member inspected
observed difference
```

### Temporary decompilation

For members whose implementation is not available in source, a temporary
ILSpy command-line installation can inspect the assembly. Keep the tool
outside the repository and remove it when it is no longer needed:

```powershell
$toolPath = Join-Path $env:TEMP "ilspycmd-abp"
dotnet tool install ilspycmd --tool-path $toolPath --version 11.0.0.9375
& (Join-Path $toolPath "ilspycmd.exe") --help
& (Join-Path $toolPath "ilspycmd.exe") <path-to-assembly.dll>
```

The version above is the temporary tool used for the ABP 10.6 investigation;
future work should first check the currently approved tool version. Never
commit the tool, generated decompilation, NuGet cache contents, or temporary
credentials.

Decompilation is evidence about a compiled artifact, not a replacement for
the tagged upstream source. Confirm important behavior with a regression test
against the repository's real integration boundary.

## Inspect NATS.Net APIs and behavior

For a NATS.Net upgrade, review the complete release range from the actual
resolved baseline to the target version. Do not inspect only the final release.
Cross-check repository usage of:

```text
NatsOpts and authentication
NatsConnection and connection state
INatsJSContext and INatsJSConsumer
GetConsumerAsync
CreateOrUpdateConsumerAsync
ConsumeAsync
AckAsync / NakAsync
JetStream publish acknowledgements
serialization interfaces
subscription channel options
drain and cancellation behavior
subject validation
```

Classify each relevant upstream change as one of:

```text
not used
source compatibility
runtime behavior
backpressure/performance
shutdown/lifecycle
serialization
JetStream
optional capability
```

Make no compatibility setting merely because it exists upstream. Configure a
new option only when repository usage proves it is needed to preserve current
behavior, and add a focused regression for that decision.

## Source and test verification

Keep live transport tests local and explicitly gated. The normal test run must
remain usable without a broker:

```powershell
dotnet test TrueParser.Abp.Nats.slnx --no-restore
```

For an intentional local live run, provide environment values in the current
shell only; do not place credentials in source, scripts, or committed config:

```powershell
$env:RUN_NATS_TESTS = "true"
$env:NATS_TEST_URL = "nats://localhost:4222"
dotnet test TrueParser.Abp.Nats.slnx --configuration Release --no-restore
```

Use `NatsFact` (or the repository's equivalent gate) for broker-dependent
tests. Start a JetStream-enabled server separately, and clean up streams,
consumers, child processes, and temporary databases with `try/finally` or
fixture disposal.

For EF-backed ABP Inbox/Outbox coverage, use ABP's real event-box services.
Do not replace transactional tests with EF InMemory or manually invoke the
transport method when the requirement is an end-to-end worker path. Keep the
local database disposable and pass its connection string through the process
environment only.

Run in proportion to the change:

```text
focused regression
broker-free suite
live JetStream suite
Release build
pack affected libraries
inspect final package dependency metadata
```

Do not change tagging, release, or publication workflows to make local NATS
tests pass. Do not add Docker if the applicable proposal forbids it.

## Evidence to record

Every completed investigation should leave a compact record containing:

```text
proposal/slice
old and new package versions
source and binary references inspected
API incompatibilities found
runtime/default changes reviewed
production files changed and why
regression name and RED/GREEN result
broker-free result
live broker result and server version
package/build result
deferred behavior
proposed Conventional Commit message
```

Keep generated test results, coverage XML/HTML, decompilation output, local
NuGet caches, passwords, JWT seeds, and temporary server configuration out of
the repository unless a proposal explicitly requires a sanitized fixture.
