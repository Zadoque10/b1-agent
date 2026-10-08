# CLAUDE.md

Guidance for AI coding assistants working in this repository.

## What this is

An LLM assistant for SAP Business One users: lookups, calculated insights (aging, credit, ATP, reorder,
pricing, daily brief) and sales quotations that a person confirms. .NET 10, ASP.NET Core minimal APIs,
Microsoft.Extensions.AI.

## Commands

- Build: `dotnet build`
- Test: `dotnet test` (no SAP or LLM needed)
- Run: `dotnet run --project src/B1Agent.Api` → http://localhost:5080 (demo data by default)
- CI builds Release with `-warnaserror`, so keep the build warning-free.

## Architecture

- `B1Agent.Core` must not reference ASP.NET Core or any LLM vendor SDK. It only knows `IChatClient`.
- `ISapB1Client` is the only way to reach SAP. `ServiceLayerClient` (real) and `DemoSapB1Client` (sample data)
  both map through `SlMapper`, so the model sees identical shapes in both modes.
- Tools live in `Agent/B1Tools.cs`. The LLM vendor is wired only in `B1Agent.Api/Llm/LlmSetup.cs`.
- Business rules are pure functions in `Insights/Calculations.cs`; `B1Insights` only loads data and calls them.
- Writes: `ISapB1Writer` is separate from `ISapB1Client` and is only used by `QuotationService.ConfirmAsync`,
  which only the `/api/actions/{id}/confirm` endpoint calls.
- The API is stateless: the client sends the conversation each time.

## Rules for changes

- No tool writes to SAP. A new write must follow the quotation pattern: a tool prepares a proposal into
  `PendingActionStore`, the UI shows it, a person confirms through an API endpoint, `ISapB1Writer` executes it.
  Never inject `ISapB1Writer` into `B1Tools`.
- Keep arithmetic out of the model: put a new rule in `Calculations` with unit tests, return the result from a tool.
- Never let the model build OData. Tools take plain values; escape them with `ODataQuery.Literal`.
- Clamp row counts in the client (`SapB1Options.MaxRows`), not only in tool descriptions.
- Tool results are compact records from `SapB1/Models.cs`. Add fields there deliberately; don't return raw Service Layer JSON.
- Tools catch `ServiceLayerException` and return a short message. Don't let SAP errors escape to the model loop.
- Every new tool needs: a `[Description]` on the method and each parameter, demo data that exercises it,
  and a test. Service Layer query changes need a test against `FakeServiceLayerHandler` that asserts the URL.
- Service Layer property names are case-sensitive: request bodies are serialised in PascalCase (`WriteJson`).
- Service Layer field names follow its metadata (`CardCode`, `DocDueDate`, `ItemWarehouseInfoCollection`...).
  Check them against `$metadata` rather than guessing.

## Conventions

- C# file-scoped namespaces, primary constructors where they keep things short, records for data.
- Async all the way, `CancellationToken` as the last parameter.
- Test names describe behaviour in plain English with underscores.
- Never commit secrets. Local settings go in `dotnet user-secrets`.
