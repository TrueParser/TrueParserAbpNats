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

## Roslyn compiler MCP

For C# symbols and relationships, the Roslyn MCP server `roslyn_code_navigator` is the primary navigation and compilation aid. Use it before text search when the question is semantic.

Use the available Roslyn operations for:

- symbol discovery: `SearchSymbols` and `GetSymbolInfo`;
- references, callers, implementations, and inheritance: `FindReferences` and `FindImplementations`;
- project/dependency inspection: `ListProjects` and `AnalyzeDependencies`;
- compiler verification: `BuildSolution`;
- test execution and full logs when useful: `TestSolution`, `StartTest`, `GetTestStatus`, and `GetTestTrx`.

The current solution is `.slnx`; the Roslyn navigator currently accepts `.sln`, `.slnf`, and `.csproj` paths, so pass the relevant `.csproj` when a Roslyn operation rejects the solution path. Use the repository solution with the normal `dotnet` CLI for solution-wide operations.

For a C# change:

1. Use Graft for repository and subsystem context.
2. Use Roslyn to resolve affected symbols, implementations, references, overrides, and dependency edges before changing a signature or contract.
3. Read only the exact source ranges needed and make the smallest edit.
4. Run Roslyn `BuildSolution` or an equivalent targeted build and resolve introduced compiler errors before testing.
5. Run focused tests, then the broader required suite when the change warrants it.

Do not use `rg`, `grep`, `find`, or literal Graft search to establish C# symbol definitions, references, callers, implementations, or inheritance when Roslyn can answer the question. Text search remains appropriate for README/docs, JSON/YAML/XML/project files, scripts, SQL, literal messages, and other non-semantic content. If Roslyn is unavailable or cannot resolve the required fact, report the limitation and use Graft structural tools, then the narrowest text-search fallback.

## Testing

Use the existing test project and test style. For a bug fix, first add a focused regression test that fails against the current implementation, then fix production code and prove that the same test passes without weakening it.

The normal test command is:

```powershell
dotnet test test/TrueParser.Abp.EventBus.Nats.Tests
```

Tests marked with `NatsFact` are live NATS tests and run only when `RUN_NATS_TESTS=true`. They require a reachable NATS Server 2.10+ with JetStream enabled, for example `nats-server -js`. Do not claim live integration coverage when those tests were gated or skipped. Keep live-test gating explicit and preserve the existing test isolation, unique stream/subject setup, and cleanup behavior.

When an integration or concurrency run fails, capture complete diagnostics rather than relying on truncated console output. Use detailed console logging or a TRX logger and inspect the resulting full log. Do not hide failures with skips, early returns, relaxed assertions, or changes to test timing solely to make a run green.

When a benchmark is required, capture a comparable baseline before implementation and repeat the same benchmark afterward.

Run only the tests relevant to the change first, then expand coverage in proportion to the blast radius. Once the required suite is green, stop editing; do not make an unrequested polishing or refactoring pass afterward.

Use normal `bin/` and `obj/` output directories. Keep temporary and build artifacts out of Git and do not introduce custom artifact directories or alternate output paths without explicit authorization.

## Git and file safety

- Use targeted status and diffs: `git status --short`, `git diff --stat`, and diffs for affected files.
- Never reset, checkout, restore, clean, revert, overwrite, or delete existing work without explicit approval.
- Do not commit, push, or force-push unless explicitly requested.
- Use `apply_patch` for repository edits and keep the diff narrow.
- Before a destructive or broad operation, resolve the exact target and confirm its scope. Prefer recoverable operations when possible.

## Completion report

Report only verified facts:

- what changed and which files were affected;
- builds and tests run, including whether live NATS tests were gated or executed;
- any unverified database, provider, or environment-dependent behavior;
- remaining open items or limitations;
- one suitable Conventional Commit message (without creating the commit).

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
