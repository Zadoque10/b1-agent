# CLAUDE.md

Guidance for AI coding assistants working in this repository.

## What this is

An LLM assistant that answers questions about SAP Business One data by calling read-only tools
over the Service Layer. .NET 10, ASP.NET Core minimal APIs, Microsoft.Extensions.AI.

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
- The API is stateless: the client sends the conversation each time.

## Rules for changes

- Tools are read-only. Do not add a tool that writes to SAP without a human confirmation step in the API and UI.
- Never let the model build OData. Tools take plain values; escape them with `ODataQuery.Literal`.
- Clamp row counts in the client (`SapB1Options.MaxRows`), not only in tool descriptions.
- Tool results are compact records from `SapB1/Models.cs`. Add fields there deliberately; don't return raw Service Layer JSON.
- Tools catch `ServiceLayerException` and return a short message. Don't let SAP errors escape to the model loop.
- Every new tool needs: a `[Description]` on the method and each parameter, demo data that exercises it,
  and a test. Service Layer query changes need a test against `FakeServiceLayerHandler` that asserts the URL.
- Service Layer field names follow its metadata (`CardCode`, `DocDueDate`, `ItemWarehouseInfoCollection`...).
  Check them against `$metadata` rather than guessing.

## Conventions

- C# file-scoped namespaces, primary constructors where they keep things short, records for data.
- Async all the way, `CancellationToken` as the last parameter.
- Test names describe behaviour in plain English with underscores.
- Never commit secrets. Local settings go in `dotnet user-secrets`.
