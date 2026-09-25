# AI Agent Guidelines

This file is the working contract for agents contributing to TrueParser.Abp.Nats.
It applies to the repository root and its two library projects and test project.

## Start of every task

1. Read this file once at the beginning of the session.
2. Read the relevant parts of `README.md` and any task or issue scope supplied by the user.
3. Run `graft map` for orientation, then use `graft ask "..." --source` for the relevant subsystem. Use `graft skeleton` for a cheap API overview and `graft callers` for known symbols and call flow.
4. Inspect only files relevant to the request. Do not scan unrelated history, reports, generated output, or documentation.
5. Run `git status --short` and `git diff --stat` before editing. Treat every existing change as user work.

Graft is required for repository orientation before source exploration. If a returned span is truncated, open only the pointed-to source range. After a substantial code change, run `graft build` so the context graph reflects the new code.

Do not guess about behavior, compatibility, security, public contracts, or architecture. If an unresolved question would materially change the implementation, stop and ask the user. Keep communication factual and avoid praise or unsupported superlatives.

After context compaction, re-run `git status --short` and `git diff --stat`, re-read only the relevant task scope, inspect targeted diffs for files already being changed, and resume from the actual working tree.

## Repository shape

This is a .NET 10 / C# 14 library solution:

- `src/TrueParser.Abp.Nats` contains the reusable NATS connection-pool, JetStream context accessor, and health-check infrastructure.
- `src/TrueParser.Abp.EventBus.Nats` contains the ABP distributed event-bus implementation backed by NATS JetStream and references the core package.
- `test/TrueParser.Abp.EventBus.Nats.Tests` contains ABP integration, event-bus, and thread-safety tests.
- `TrueParser.Abp.Nats.slnx` is the repository solution file.

There is no application host in this repository. Do not add an executable, API, microservice, or unrelated shared project to solve a library-level problem unless explicitly requested.

Follow the existing project structure, `.editorconfig` when present, readable C# formatting, nullable conventions, and package metadata. Keep infrastructure in the package that owns it.

## Scope and simplicity

- Implement only the requested change and its necessary focused tests or documentation.
- Choose the smallest production-grade change that fits the existing ABP and NATS design.
- Do not add speculative abstractions, wrappers, factories, compatibility shims, retries, providers, or layers for convenience.
- Ask before making a compatibility change when compatibility requirements were not stated.
- Preserve existing behavior, safeguards, public APIs, option defaults, and tests unless the task explicitly changes them.
- Do not remove files or substantial code, weaken tests, or broaden a refactor without explicit approval. Before removing behavior, find its callers and tests and establish that the requirement is obsolete.
- Never use phase names in production or test filenames.

## ABP and NATS invariants

When changing this area, verify the impact on the following repository contracts:

- `TrueParserAbpNatsModule` binds `TrueParser:Nats`, registers the singleton `INatsConnectionPool`, and adds the `nats` health check.
- `TrueParserAbpEventBusNatsModule` binds `TrueParser:EventBus:Nats`, registers the event serializer, replaces the ABP distributed event bus with `NatsDistributedEventBus`, and initializes it during application startup.
- The connection pool is keyed by connection name and owns the lifecycle of its NATS connections. Preserve safe lazy creation, reuse, and asynchronous disposal when changing connection handling.
- Event-bus changes must account for stream creation, subject-prefix mapping, durable consumer identity, startup/shutdown, retry behavior, explicit acknowledgements, negative acknowledgements on handler failure, and concurrent subscribe/unsubscribe operations.
- Preserve ABP correlation and tenant metadata carried in published NATS headers unless the task explicitly changes that contract.
- Configuration changes must preserve the documented `TrueParser:Nats` and `TrueParser:EventBus:Nats` paths and update README examples when user-facing configuration changes.
- Keep the two NuGet packages independently meaningful: the event-bus package may depend on the core NATS package, while core infrastructure must not depend on the event-bus package.

Do not change stream retention, consumer delivery semantics, subject naming, acknowledgement behavior, connection selection, or lifecycle ownership as an incidental refactor. Add focused regression coverage when any of these contracts changes.


## Glider MCP - Compiler-Semantic C# Analysis

This repository has access to the Glider MCP server, which provides compiler-grade semantic analysis of the .NET codebase.

### Core Rule

For C# symbols and relationships, Glider MCP is the primary and authoritative navigation mechanism.

Do not use `grep`, `find`, `rg`, Graft literal search, or other text-based matching to locate C# symbol definitions, implementations, references, callers, inheritance relationships, semantic dependencies, or compiler diagnostics unless the Glider MCP tools cannot provide the required information.

Graft remains required for repository orientation, architecture context, subsystem understanding, indexed source context, and non-semantic codebase exploration. Glider and Graft are complementary:

- use Graft to understand the repository, subsystem, architectural context, and relevant source areas;
- use Glider to establish compiler-semantic facts about C# symbols, references, callers, implementations, overrides, inheritance, dependencies, impact, and diagnostics;
- use normal local file tools to read exact source ranges and edit files after the relevant location is known.

### Required Glider Usage

Use Glider when:

1. **Locating C# symbols**

- Find classes, interfaces, records, structs, enums, methods, constructors, properties, fields, events, and other C# symbols through Glider.
- Do not guess file paths or rely on textual name matches when Glider can resolve the symbol.
- When Glider returns a `symbolKey`, retain it and reuse it in later semantic operations instead of re-resolving the symbol by name.

2. **Tracing dependencies**

- Before changing a method signature, interface, base class, shared contract, or other referenced C# symbol, use Glider references, callers, implementations, overrides, inheritance, dependency, and impact tools to identify affected code.
- Do not change shared contracts until the affected semantic relationships are known.

3. **Resolving dependency-injection implementations**

- When a service is consumed through an interface, use Glider to identify the concrete implementation or implementations.
- Do not infer the implementation only from naming conventions, folder layout, or the interface definition.

4. **Understanding C# call flow**

- Use Glider caller and outgoing-call information when determining how methods, constructors, services, handlers, and other code paths are reached.

5. **Diagnosing compilation errors**

- When code changes introduce or may have introduced compiler errors, use Glider diagnostics to obtain the exact diagnostic code, file, line, and character when available.
- Do not guess compiler failures when Glider diagnostics can provide the actual result.

### C# Change Workflow

For C# changes:

1. **Analyze** - Use Graft for repository and subsystem context, then use Glider to map the affected C# symbols, implementations, callers, references, overrides, inheritance relationships, dependency relationships, and relevant call flow.

2. **Plan** - Form the smallest required change from the exact semantic relationships discovered and the active `TASK.md` scope.

3. **Execute** - Read only the necessary source ranges and edit only the required files using normal local file tools.

4. **Verify** - Run Glider diagnostics after the code changes and resolve any compiler errors or broken references introduced by the change. Then run the focused tests required by this guide and the active `TASK.md` slice.

### Workspace Guidance

Use the repository's actual solution or project file for Glider analysis. Prefer the top-level `.sln` or `.slnx` when the task spans multiple projects, and use a specific `.csproj` only when the task is intentionally scoped to that project. Do not invent or convert solution formats.

### Glider Failure Fallback

If the Glider MCP server or a required Glider operation fails, report that limitation and then use the narrowest appropriate fallback.

For C# fallback navigation, prefer Graft's indexed context and structural tools before raw text search. Raw `grep`, `find`, or `rg` for C# symbol discovery is a last resort after Glider and applicable Graft tooling cannot provide the required result.

Text search remains appropriate for non-C# content such as configuration, documentation, JSON, YAML, XML, project files, scripts, SQL, literal error strings, generated artifacts, and data where compiler-semantic C# analysis does not apply.

<!-- graft:start -->
## Graft — repo context graph

This repo is indexed in `graft/`: small linked markdown nodes that explain each
system and carry exact file:line spans, kept in sync with the code through git.

For ANY task here — understanding how something works, finding where code lives,
or scoping a change — get context from the graph before grepping or opening
source files. Re-ask freely (it's cheap) and reuse literal identifiers you
already have (symbol, error string, file name) as the query. New to this repo?
Run `graft map` first — a token-budgeted orientation (no LLM, no key).

- `graft ask "<your question>" --source` returns ranked nodes with relevant
  code spans inlined. Use `--full` for a whole definition when the crux is not
  enough. For exhaustive tasks, use `graft grep "<literal>"`; ranked ask results
  are not exhaustive.
- `graft skeleton <file>` returns every definition's signature and span for a
  cheap API overview.
- `graft callers <symbol>` gives precomputed exact edges. Add `--direction out`
  for what the symbol calls or `--depth N` for a transitive walk.
- `graft/INDEX.md` lists every indexed node.
- If a returned span is truncated, open the exact pointed-to source range before
  finalizing. Do not reread whole source files without a task-specific reason.
- After substantial code changes, run `graft build` (deterministic, no API key).
<!-- graft:end -->
