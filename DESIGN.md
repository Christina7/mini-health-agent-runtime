# Design: a runnable mini "Agent Runtime" (health triage)

## Context

This is a small, open-source **agent runtime** that reproduces, in miniature, the core architecture of a production AI agent platform: agent orchestration, config-driven execution (RuntimeConfig + JSON-Patch flights), a failure-handling framework (`CompliantException` / `FailureMode` / degraded responses), a work-context store (cross-turn cacheability), and **OpenTelemetry-based** distributed tracing — all in C#/.NET 8.

A health **symptom-triage & care-navigation** agent is built on top as the sample domain. Healthcare is chosen deliberately — it makes safety guardrails and graceful degradation load-bearing and demonstrable. The runtime is domain-agnostic; all health specifics live in the sample app.

**Surfaces.** Milestone 1 ships a CLI host (deterministic, offline). Milestone 2 adds a thin **ASP.NET Core minimal-API** host serving a single self-contained `index.html` chat page that exposes `POST /triage`; the **`AgentRuntime` orchestrator runs server-side in C#** (no agent logic leaks into JavaScript). The page renders the conversation **and a live per-turn trace tree** (conversation → agent step → tool calls, with latency and a `degraded` tag), so the observability is visually demonstrable, not just asserted in console text.

**Decisions made:** C#/.NET 8; pluggable LLM with a deterministic **mock provider as the default** so `dotnet run` works with zero keys, plus an optional real Claude provider; use case = **symptom triage & care navigation**, framed strictly as an educational/navigation tool with "not medical advice" disclaimers and a red-flag emergency-escalation guardrail (never a diagnostic system).

Runtime library assembly: **`AgentRuntime`**; sample app: **`CareTriageAgent`**.

## Goal

A `dotnet build && dotnet test && dotnet run` repo where:
- A **reusable agent-runtime library** implements orchestration, config/flights, tools, failure modes, work context, and tracing — each component a self-contained, real agent-platform concern.
- A **CareTriageAgent.Web** ASP.NET host serves a browser chat UI and runs the runtime server-side, conducting a multi-turn triage session on deterministic mock data (so it runs offline), optionally backed by the real Claude API. The page visualizes the per-turn OpenTelemetry trace tree.
- A **CareTriageAgent.Cli** thin scenario-runner replays scripted transcripts non-interactively for deterministic verification and CI.
- **Tests + GitHub Actions CI** (unit + a `WebApplicationFactory` integration test against `POST /triage`) prove it works.
- A **README** documents the architecture and a concept→file map.

## Repository layout

```
mini-health-agent-runtime/
├─ src/
│  ├─ AgentRuntime/                  # the reusable, DOMAIN-AGNOSTIC runtime library (the core)
│  │  ├─ Orchestration/             # AgentOrchestrator (reason→act→observe loop), IGuardrail hook
│  │  ├─ Tools/                     # ITool, ToolRegistry, tool-selection strategy
│  │  ├─ Config/                    # RuntimeConfig, RuntimeConfigProvider, JSON-Patch flights
│  │  ├─ Failure/                   # CompliantException, FailureMode, ExecutionScope, ScopeResult
│  │  ├─ Context/                   # WorkContext, IWorkContextProvider, cacheability
│  │  ├─ Llm/                       # ILlmClient, PlanDecision, AnthropicLlmClient (generic), ScriptedLlmClient (test double)
│  │  └─ Observability/             # ActivitySources, RuntimeTracing, in-memory span collector (for the web trace tree)
│  ├─ CareTriageAgent/              # CLASS LIBRARY: the HEALTH domain on top of the runtime (no host)
│  │  ├─ Guardrails/                # RedFlagGuardrail (implements IGuardrail)
│  │  ├─ Tools/                     # SymptomKnowledgeBaseTool, ClinicFinderTool (ITool; ClinicFinder reads via the provider below)
│  │  ├─ Providers/                 # ClinicDirectoryProvider (IWorkContextProvider — cacheable, cross-turn)
│  │  ├─ Triage/                    # SymptomState, TriagePolicy, TriageResult, MockTriagePlanner (ILlmClient)
│  │  ├─ Data/                      # synthetic JSON: symptom KB, red-flag rules, clinics, scenarios
│  │  ├─ config/runtimeconfig.json  # base config
│  │  ├─ config/flights/*.json      # JSON-Patch flight overlays
│  │  └─ CareTriageSession.cs       # composition root: wires runtime + domain into one OnUserMessage entry point
│  ├─ CareTriageAgent.Web/          # ASP.NET Core minimal-API HOST — the demo surface
│  │  ├─ wwwroot/index.html         # self-contained chat UI + trace-tree panel (vanilla JS, no build step)
│  │  └─ Program.cs                 # POST /triage, GET / (static page); per-conversation WorkContext store
│  └─ CareTriageAgent.Cli/          # thin console scenario-runner (deterministic --scenario replay for CI)
│     └─ Program.cs
├─ tests/AgentRuntime.Tests/         # xUnit + Moq (runtime + domain unit tests)
├─ tests/CareTriageAgent.Web.Tests/  # WebApplicationFactory integration test against POST /triage
├─ .github/workflows/ci.yml          # dotnet build + test
├─ MiniHealthAgentRuntime.sln
├─ LICENSE                            # MIT — standard for a public repo
├─ README.md                         # what it is, quickstart (screenshot/GIF), concept→file mapping table
└─ ARCHITECTURE.md                   # deep-dive: each component, file location, data flow, how to debug
```

> **Why a `CareTriageAgent` class library + two thin hosts:** the health domain (guardrails, tools, triage, planner, data) is shared by both the web host and the CLI runner. Neither host contains agent logic — the Web host marshals HTTP↔runtime; the CLI host feeds a scripted transcript. This keeps the "runtime, not a script" separation honest and makes the integration test trivial.

## Core runtime components

1. **Orchestration / agent loop — `Orchestration/AgentOrchestrator.cs`**
   Implements the multi-step reason→act→observe loop: ask `ILlmClient` for the next step → either call a tool (`ITool`) or emit the final answer → feed the observation back → repeat until done or a max-step budget is hit. Reproduces "core agent orchestration runtime… multi-step agent workflows (agent chain / tool calls / decision loops)."

2. **Config-driven execution + flights — `Config/RuntimeConfigProvider.cs`**
   Loads `runtimeconfig.json`, then merges ordered **JSON-Patch flight** overlays (using **`JsonPatch.Net`**, System.Text.Json-native — single JSON stack, no Newtonsoft) to produce the effective `RuntimeConfig`. Config controls: which tools are enabled, triage urgency thresholds, retry counts, prefetch toggle, and which LLM provider to use — changeable with no recompile. Reproduces "configuration-driven execution model… feature flags / runtime config… RuntimeConfig + JSON Patch flights… iterate without code redeploy."

3. **Tool framework + selection — `Tools/ITool.cs`, `ToolRegistry.cs`**
   `ITool` exposes a name, JSON input schema, and `ExecuteAsync`. The registry filters to config-enabled tools and hands their schemas to the planner. Reproduces "tool selection & invocation strategy."

4. **Failure-handling framework — `Failure/`**
   `CompliantException` (safe, user-facing message vs. internal detail), `FailureMode` enum (`Unknown` → `CanImpactResponse` / `NeverImpactsResponse`), an `ExecutionScope.TryExecute(action, FailureMode)` that applies **retry → fallback → degrade**, and a `DegradedResponse` flag surfaced on the result. When a triage tool fails, the agent degrades to a safe "couldn't verify — consult a professional" answer instead of crashing. Reproduces "unified error handling and failure mode framework… retry / degrade / fallback… CompliantException… DegradedResponse tracking." This is the centerpiece that healthcare makes meaningful.

5. **Work Context / memory — `Context/WorkContext.cs`, `IWorkContextProvider`**
   Holds cross-turn conversation state, intermediate agent state, and tool I/O; providers expose a **cacheability key** so query-dependent results are reused across turns; lifecycle/expiry managed centrally. Reproduces "Work Context Provider… cross-turn cacheability for query-dependent providers… context/memory/state management… reduced redundant computation." The demo ships a **concrete** `ClinicDirectoryProvider : IWorkContextProvider` (`CareTriageAgent/Providers/`) that the clinic lookup goes through, so the cache hit is **visible in the trace tree** on a repeated turn — not merely asserted by a mock.

6. **Observability / distributed tracing — `Observability/RuntimeTracing.cs`**
   An `ActivitySource` emits spans for the conversation, each agent step, and each tool call, with latency and a `degraded` tag; wired to the OpenTelemetry console exporter (and OTLP-ready). A small **in-memory span collector** — **one process-wide `ActivityListener`** registered at startup that buckets sampled activities by the root `triage.turn` Activity's `TraceId` (so concurrent `POST /triage` requests never cross-contaminate), reconstructing each turn's tree from `Activity.Parent`/child links — captures the same spans as a serializable tree so the web host can return them in the `POST /triage` response and the browser can **render the trace tree visually**; the CLI prints the same tree as text. Reproduces "OpenTelemetry-based distributed tracing… agent decision steps, tool invocation chains, latency breakdown… production debugging." Use `OpenTelemetry` + `OpenTelemetry.Exporter.Console` packages.

7. **LLM abstraction — `Llm/`** (generic)
   `ILlmClient.PlanNextStepAsync(WorkContext, toolDescriptors)` returns a generic `PlanDecision` (`CallTool` | `AskUser` | `Finish`); the runtime owns the loop, providers only pick the next step.
   - **`AnthropicLlmClient` (generic, optional):** official `Anthropic` NuGet package, model **`claude-opus-4-8`**, `Thinking = new ThinkingConfigAdaptive()`, **structured outputs** (`OutputConfig.Format = new JsonOutputFormat{...}`) constrained to the `PlanDecision` schema. Domain-agnostic — emits decisions; the app interprets the `Finish` payload. Selected via config + `ANTHROPIC_API_KEY`. (Per the Claude API skill's C# guidance.) **Verified caveats:** `MaxTokens` is required on the call; and because `CallTool.Args` / `Finish.Result` are arbitrary `JsonElement`, strict structured output (which needs `additionalProperties:false` on every object and can't express an open-ended JSON field) doesn't fit cleanly — constrain to per-tool typed arg schemas, or use the runtime's lenient parse-and-validate path. This path is optional and key-gated; the offline mock is the default, so none of this blocks running the repo.
   - The **deterministic default planner lives in the app** (`CareTriageAgent`, `MockTriagePlanner : ILlmClient`) because the rules are health-specific — no key, offline, reproducible for tests. The runtime ships a trivial `ScriptedLlmClient` test double for its own unit tests.

8. **Safety guardrail — `CareTriageAgent/Guardrails/RedFlagGuardrail.cs`** (implements the runtime's `IGuardrail`)
   Registered into the orchestrator's guardrail pipeline; runs first every turn; on red-flag symptom combinations (e.g., chest pain + shortness of breath) it short-circuits orchestration and returns an emergency-escalation message. Every response carries a "not medical advice" disclaimer. This mirrors the compliance/availability-first ethos of your on-call reliability work.

## Key contracts & schemas

These are the load-bearing types and file formats. Names are the proposed C# identifiers (namespace `AgentRuntime.*`).

**Separation of concerns (a deliberate design property):** `AgentRuntime` is **domain-agnostic** — orchestration, config/flights, tools, failure modes, work context, guardrail hook, tracing. It has *zero* health knowledge and could host any agent. All health specifics — `SymptomState`, `TriagePolicy`, `RedFlagGuardrail`, the concrete tools, and the data — live in `CareTriageAgent`. This is what makes it a *runtime*, not a triage script, and is the first thing `ARCHITECTURE.md` explains.

### Planner / LLM contract — `Llm/`
The runtime owns the loop; the LLM only decides the *next step*. Mock and Anthropic providers implement the same interface, so the orchestrator is identical for both.
```csharp
public interface ILlmClient {
    Task<PlanDecision> PlanNextStepAsync(WorkContext ctx, IReadOnlyList<ToolDescriptor> tools, CancellationToken ct);
}
public abstract record PlanDecision {                                                   // all generic — no health types
    public sealed record CallTool(string ToolName, JsonElement Args, string? Reasoning) : PlanDecision;
    public sealed record AskUser(string Question)                                       : PlanDecision;
    public sealed record Finish(string Message, JsonElement? Result)                    : PlanDecision; // Result JSON → app deserializes to TriageResult
}
public sealed record ToolDescriptor(string Name, string Description, JsonElement InputSchema);
```
- **`MockTriagePlanner` (app default — `CareTriageAgent.Triage`):** deterministic rules over `WorkContext` — call `symptom_kb` if symptoms unscored; call `clinic_finder` if a referral is warranted and not yet fetched; otherwise `Finish` with a `TriageResult` payload whose urgency comes from `TriagePolicy`. Reproducible → stable CI.
- **`AnthropicLlmClient` (runtime, optional):** `claude-opus-4-8`, `ThinkingConfigAdaptive`, **structured output** (`OutputConfig.Format = JsonOutputFormat`) constrained to the `PlanDecision` schema, tool descriptors passed in the prompt; the `Finish` JSON payload is deserialized to `TriageResult` by the app.

### Tool contract — `Tools/`
```csharp
public interface ITool {
    string Name { get; }  string Description { get; }  JsonElement InputSchema { get; }
    Task<ToolResult> ExecuteAsync(JsonElement args, WorkContext ctx, CancellationToken ct);
}
public sealed record ToolResult(bool Success, JsonElement Output, string? Error = null);
```

### Failure framework — `Failure/`
```csharp
public enum FailureMode { Unknown = 0, CanImpactResponse, NeverImpactsResponse }
public sealed class CompliantException : Exception {     // base Message = internal detail (logs/trace only)
    public string UserSafeMessage { get; }               // the only text ever shown to the user
    public FailureMode FailureMode { get; }
}
public sealed record ScopeResult<T>(T? Value, bool Degraded, bool Failed);
public sealed class ExecutionScope {                      // constructed with config.resilience.toolMaxRetries
    public Task<ScopeResult<T>> TryExecuteAsync<T>(
        string op, FailureMode mode, Func<CancellationToken,Task<T>> action,
        Func<T>? fallback, CancellationToken ct);         // try → retry → (CanImpact ⇒ degrade+fallback | NeverImpacts ⇒ swallow)
}
```

### Work context — `Context/` (domain-agnostic)
`WorkContext` holds only generic state — no health types — so the runtime stays reusable. The app stores its `SymptomState` in the typed state bag under a known key.
```csharp
public sealed class WorkContext {
    public string ConversationId { get; }
    public IReadOnlyList<Turn> History { get; }           // user/agent messages across turns
    public bool Degraded { get; internal set; }
    public T? GetState<T>(string key);                    // typed bag: intermediate agent state + tool I/O
    public void SetState<T>(string key, T value);
}
public interface IWorkContextProvider {                   // cross-turn cacheability for query-dependent data
    string Name { get; }  bool IsCacheable { get; }
    string ComputeCacheKey(WorkContext ctx);              // query-dependent key
    Task<JsonElement> ProvideAsync(WorkContext ctx, CancellationToken ct);
}
```
The runtime memoizes provider output by `ComputeCacheKey` (TTL from config) so a repeated query across turns skips recomputation — cross-turn cacheability for query-dependent providers.

### Guardrail hook — `Orchestration/` (domain-agnostic)
A generic pre-planning hook the orchestrator runs every turn before any LLM/tool work. Red-flag detection is the *health implementation* of this interface, registered by the app — the runtime itself knows nothing about chest pain.
```csharp
public interface IGuardrail {
    Task<GuardrailVerdict> EvaluateAsync(WorkContext ctx, CancellationToken ct);
}
public sealed record GuardrailVerdict(bool ShortCircuit, string? Message, object? Result);
```

### Result — `TriageResult` (domain type: `CareTriageAgent.Triage`, not in the runtime)
```csharp
public enum UrgencyLevel { SelfCare, SeeGp, UrgentCare, Emergency }
public sealed record TriageResult {
    public UrgencyLevel Urgency { get; init; }
    public string RecommendedAction { get; init; }
    public IReadOnlyList<string> ToolsInvoked { get; init; }
    public string? CareNavigation { get; init; }          // e.g. nearest clinic
    public bool RedFlagTriggered { get; init; }
    public bool Degraded { get; init; }
    public string Disclaimer { get; init; }               // constant "not medical advice"
}
```

### RuntimeConfig (`config/runtimeconfig.json`)
```jsonc
{
  "agent":      { "maxSteps": 6, "llmProvider": "mock" },         // "mock" | "anthropic"
  "tools": {
    "symptom_kb":    { "enabled": true },
    "clinic_finder": { "enabled": true, "dataFile": "Data/clinics.json" }
  },
  "triage":     { "selfCareMaxScore": 2, "seeGpMaxScore": 5, "urgentCareMaxScore": 8 },
  "context":    { "providerCacheEnabled": true, "cacheTtlSeconds": 300 },
  "resilience": { "toolMaxRetries": 2, "prefetchEnabled": false },
  "anthropic":  { "model": "claude-opus-4-8" }
}
```
> The **red-flag guardrail is intentionally absent from `tools`** — it is not a toggleable tool (see Safety invariant below).

### Flight overlays (`config/flights/*.json`, JSON-Patch)
```json
// disable-clinic-finder.json
[ { "op": "replace", "path": "/tools/clinic_finder/enabled", "value": false } ]
// break-clinic-finder.json   (drives the DEGRADED demo)
[ { "op": "replace", "path": "/tools/clinic_finder/dataFile", "value": "Data/missing.json" } ]
// provider-anthropic.json
[ { "op": "replace", "path": "/agent/llmProvider", "value": "anthropic" } ]
```

### Synthetic data (`Data/`)
```jsonc
// symptom-kb.json
[ { "id":"sore_throat", "keywords":["sore throat","throat pain"], "baseSeverity":1, "selfCareAdvice":"…", "escalateIfDays":5 } ]
// red-flag-rules.json   (allOf = all terms present; anyOf = any term)
[ { "id":"cardiac", "allOf":["chest pain","shortness of breath"], "message":"Possible cardiac emergency — call emergency services." } ]
// clinics.json
[ { "name":"Maple Family Clinic", "type":"gp", "distanceKm":1.2, "inNetwork":true } ]
// scenarios/red-flag.txt   (one user line per row; '#' = comment, for --scenario replay)
```

### One-turn control flow (canonical loop — `Orchestration/AgentOrchestrator`)
```
OnUserMessage(text):
  ctx.AppendUser(text)
  using span "triage.turn"
  // 1. GUARDRAIL PIPELINE — every registered IGuardrail, before any planning; not toggleable tools
  foreach g in guardrails:                                // health app registers RedFlagGuardrail here
    v = g.EvaluateAsync(ctx)
    if v.ShortCircuit: return Reply(v.Message, v.Result)  // e.g. red-flag → Emergency
  // 2. PLAN→ACT→OBSERVE loop
  for step in 1..config.agent.maxSteps:
    using span "agent.step"
    switch llm.PlanNextStepAsync(ctx, EnabledToolDescriptors(config)):
      CallTool(name,args):
        r = scope.TryExecuteAsync(name, FailureMode.CanImpactResponse,
                                  ct => registry[name].ExecuteAsync(args,ctx,ct), fallback)
        ctx.RecordObservation(name, r);  if r.Degraded: ctx.Degraded = true
      AskUser(q):           return Reply(q)               // end turn, await next user input
      Finish(msg, resultJson): return Reply(msg, app.ToTriageResult(resultJson))  // app maps payload → TriageResult
  // 3. BUDGET EXHAUSTED → safe degraded fallback (never loops forever, never crashes)
  return Reply(SafeFallback(), degraded:true)
```

### Safety invariant
The red-flag guardrail (`CareTriageAgent/.../RedFlagGuardrail`) runs *outside* the tool registry and executes on every turn before planning. **No flight or config can disable it.** This is a deliberate "configuration cannot override a safety invariant" property — the healthcare analogue of your compliance/availability work — and it is covered by a dedicated test.

### HTTP contract — `CareTriageAgent.Web/Program.cs`
The web host is deliberately thin: it owns no agent logic. It maps HTTP to the runtime and keeps a per-conversation `WorkContext` so the multi-turn memory/cacheability behavior is exercised across requests.

```
GET  /                      → serves wwwroot/index.html (the chat UI)
POST /triage                → drives one turn through CareTriageSession.OnUserMessage
```
```jsonc
// POST /triage  request
{ "conversationId": "abc123",          // client-generated; null/absent ⇒ server mints a new one
  "message": "sore throat and mild fever",
  "flights": ["disable-clinic-finder"], // optional: named overlays from config/flights (allow-listed, not arbitrary paths)
  "provider": "mock" }                   // optional: "mock" | "anthropic"
// POST /triage  response
{ "conversationId": "abc123",
  "reply": "Looks self-manageable; see a GP if it persists >5 days…",
  "triage": {                            // null until the turn reaches a Finish (e.g. while AskUser)
    "urgency": "SeeGp", "recommendedAction": "…", "toolsInvoked": ["symptom_kb","clinic_finder"],
    "careNavigation": "Maple Family Clinic (1.2 km)", "redFlagTriggered": false, "degraded": false,
    "disclaimer": "Educational only — not medical advice." },
  "trace": { "name": "triage.turn", "durationMs": 2.1, "degraded": false,
             "children": [ { "name": "agent.step", "durationMs": 1.8, "children": [
               { "name": "tool:symptom_kb", "durationMs": 0.4, "degraded": false },
               { "name": "tool:clinic_finder", "durationMs": 0.6, "degraded": false } ] } ] } }
```
- **Session store:** an in-memory `ConcurrentDictionary<string, WorkContext>` keyed by `conversationId` (with idle expiry). Single-process, no DB — keeps the demo zero-dependency; documented as a demo simplification.
- **Flights are allow-listed:** the request may name overlays that exist in `config/flights/`; it cannot post arbitrary JSON-Patch paths (so the browser can't disable the safety guardrail — and there is no guardrail toggle to begin with). This keeps the Safety invariant intact over HTTP.
- **Errors:** a `CompliantException` thrown server-side maps to a 200 response carrying the `UserSafeMessage` and `degraded:true` (the user never sees internal detail) — the HTTP analogue of DegradedResponse; unexpected exceptions map to a 500 with a generic safe message.

### Browser UI — `wwwroot/index.html`
A single self-contained page (vanilla HTML/CSS/JS, **no npm/build step** — important: the repo stays "clone & `dotnet run`"). Two panes:
- **Chat pane:** message history, an input box, the final triage card (urgency badge color-coded by level, recommended action, care-navigation, always-visible disclaimer).
- **Trace pane:** renders the `trace` tree from each response as a collapsible tree / mini latency timeline, with red-flag and `degraded` spans highlighted — the visual proof of the OpenTelemetry tracing.
- **Demo controls:** a small toolbar to flip provider (mock/anthropic) and toggle the `break-clinic-finder` flight, so a viewer can trigger the **degraded** and **red-flag** flows live without editing files.

## CareTriageAgent (the runnable demo)

The runtime conducts a multi-turn triage conversation: gathers symptoms into `WorkContext`, runs the red-flag guardrail, has the planner select tools (symptom KB lookup → clinic finder), and recommends an urgency level (self-care / see GP / urgent care / ER) with a care-navigation suggestion. All data is synthetic JSON shipped in `Data/`. The composition root is `CareTriageSession.OnUserMessage` (in the `CareTriageAgent` library); the **web host** calls it per request and returns the reply + serialized trace tree for the browser to render, while the **CLI runner** calls the same method over a scripted transcript and prints the trace as text.

## Inputs & outputs (the demo contract)

**Inputs**
- **Primary (web):** free-text symptom messages typed into the **browser chat UI**, multi-turn. The agent asks follow-ups and remembers earlier turns via the server-side `WorkContext` keyed by `conversationId`. Sent as `POST /triage` (see HTTP contract above). Demo-control toolbar flips provider and toggles the broken-clinic flight.
- **Secondary (CLI runner):** `dotnet run --project src/CareTriageAgent.Cli -- --scenario <file> [--flight <name>] [--provider mock|anthropic]` — replays a scripted transcript non-interactively for demos, deterministic verification, and CI.
- **Files (shipped in-repo):** `config/runtimeconfig.json`, `config/flights/*.json`, and synthetic data `Data/symptom-kb.json`, `Data/red-flag-rules.json`, `Data/clinics.json`.
- **Environment:** `ANTHROPIC_API_KEY` — only when provider is `anthropic`.

**Outputs**
- **Per-turn agent reply (JSON → rendered in the page):** clarifying questions, then a final **triage recommendation**: an urgency level (`self-care | see-GP | urgent-care | ER`), a short rationale, a care-navigation suggestion (nearest mock clinic), and a "not medical advice" disclaimer — shown as a color-coded triage card.
- **Trace tree per turn:** OpenTelemetry spans (conversation → agent step → each tool call) with latency and a `degraded` tag, returned in the response and **rendered as a collapsible tree** in the browser (printed as text by the CLI runner).
- **Structured result object:** `TriageResult { urgency, recommendedAction, toolsInvoked[], careNavigation, redFlagTriggered, degraded, disclaimer }` — the `triage` field of the response and what tests assert against.
- **Lifecycle:** the browser session is stateless per request but threaded by `conversationId`; the CLI runner exits on scenario completion.

**Three representative flows** (shown in the browser; trace rendered in the trace pane)

```
NORMAL
> Sore throat and mild fever since yesterday, no trouble breathing.
Agent: Looks self-manageable; see a GP if it persists >5 days or worsens.
       Nearest in-network clinic: Maple Family Clinic (1.2 km). ⚠ Educational only — not medical advice.
[trace] triage.turn 2.1ms └ agent.step └ tool:symptom_kb └ tool:clinic_finder └ provider:clinic_directory (miss→cached)

NORMAL — follow-up turn (same conversation, repeated clinic query)
> Anything closer than Maple Family Clinic?
Agent: Maple Family Clinic (1.2 km) is the nearest in-network option. ⚠ Educational only — not medical advice.
[trace] triage.turn 0.5ms └ agent.step └ provider:clinic_directory (CACHE HIT — provider not re-invoked)

RED-FLAG  (guardrail runs first; always escalates; short-circuits orchestration)
> Severe chest pain and shortness of breath.
Agent: 🚨 Possible emergency — call your local emergency number / go to the ER now.
[trace] triage.turn 0.6ms └ guardrail:red_flag(TRIGGERED: cardiac) → escalate

DEGRADED  (tool failure → retry → safe fallback, never a crash; toggled from the demo toolbar)
> (with the break-clinic-finder flight applied)
Agent: ...I couldn't verify nearby clinics right now; consult your provider directory. [response degraded]
[trace] └ tool:clinic_finder FAILED → retry x2 → fallback (DegradedResponse)
```

## Build order (milestones — build in this sequence)

De-risk completion by building in vertical slices; each milestone leaves a green, demonstrable repo even if a later one slips.

1. **Runtime + domain + mock + CLI + tests (the proof).** `AgentRuntime`, the `CareTriageAgent` domain (guardrails, tools, `ClinicDirectoryProvider`, mock planner, data), the thin `CareTriageAgent.Cli` scenario-runner, the full xUnit suite, and GitHub Actions CI. **This milestone alone exercises every runtime component headlessly and offline** — no browser, no key.
2. **Web host + browser trace UI (the lead image).** `CareTriageAgent.Web` + `wwwroot/index.html` chat and trace-tree panel — the browser surface and README screenshot/GIF.
3. **Optional live providers.** `AnthropicLlmClient` (and, if desired, the pluggable open-model provider in Future stages). Strictly additive; the mock remains the default.

## Dependencies & how to run

**Hard prerequisite:** the **.NET 8 SDK** — nothing else (the ASP.NET Core runtime ships with it via the `Microsoft.NET.Sdk.Web` SDK; no extra package). No npm/Node — the page is plain HTML/JS. The default path is fully **offline**: no network, no API key, no database; all data is synthetic JSON in the repo. Cross-platform (Windows / macOS / Linux).

**NuGet packages**
- Runtime/domain: `OpenTelemetry`, `OpenTelemetry.Exporter.Console`, `JsonPatch.Net` (System.Text.Json-native JSON Patch for flights).
- Web host: none beyond the `Microsoft.NET.Sdk.Web` SDK (minimal API + static files are built in).
- Tests: `xunit`, `xunit.runner.visualstudio`, `Moq`, `Microsoft.NET.Test.Sdk`, `Microsoft.AspNetCore.Mvc.Testing` (WebApplicationFactory).

**Optional (live model):** `Anthropic` NuGet package + `ANTHROPIC_API_KEY` env var + network access — engaged only via `provider=anthropic` (toolbar/request) or the provider flight.

**Run commands**
```
dotnet build MiniAgentRuntime.sln
dotnet test
dotnet run --project src/CareTriageAgent.Web                   # serves the chat UI; open http://localhost:5xxx
dotnet run --project src/CareTriageAgent.Cli -- --scenario Data/scenarios/red-flag.txt        # deterministic replay
dotnet run --project src/CareTriageAgent.Cli -- --scenario Data/scenarios/normal.txt --flight disable-clinic-finder
# optional live model (web):
$env:ANTHROPIC_API_KEY="sk-..."; dotnet run --project src/CareTriageAgent.Web   # then pick "anthropic" in the toolbar
```

## Tests & CI

`tests/AgentRuntime.Tests/` (xUnit + Moq, the AutoMock-style you list). Each test pins one behavior:
1. **Red-flag escalation overrides everything** — even with all tools disabled and `provider=anthropic`, a cardiac input returns `Emergency`.
2. **Safety invariant** — a flight attempting to disable the guardrail has no effect; it still escalates (proves config can't override safety).
3. **Flight merge** — `enabled:false` removes a tool from `EnabledToolDescriptors`; a threshold patch changes the urgency mapping.
4. **Retry then succeed** — a tool failing transiently is retried up to `toolMaxRetries` and ultimately succeeds (no degrade).
5. **Degrade + fallback** — terminal failure with `CanImpactResponse` → `ScopeResult.Degraded`, fallback value used, and `TriageResult.Degraded` propagates.
6. **Swallow** — terminal failure with `NeverImpactsResponse` → continues, not marked degraded.
7. **Provider cache** — the same clinic query across two turns invokes `ClinicDirectoryProvider.ProvideAsync` once; the second turn is served from cache (asserted on the concrete provider and visible as a `provider:clinic_directory (cache hit)` span).
8. **Step budget** — a planner that always returns `CallTool` stops at `maxSteps` with a degraded fallback (no infinite loop).
9. **TriagePolicy boundaries** — table-driven `[Theory]` over score→`UrgencyLevel` thresholds.
10. **CompliantException hygiene** — `UserSafeMessage` reaches the user; internal `Message` appears only in the trace/log, never in the reply.
11. **Scenario replay (CLI)** — `--scenario red-flag.txt` deterministically yields `Emergency` (guards the end-to-end path).
12. **Web integration (`tests/CareTriageAgent.Web.Tests/`)** — `WebApplicationFactory` boots the host in-memory; `POST /triage` with a cardiac message returns 200 with `triage.urgency == "Emergency"` and a non-empty `trace` tree; a second `POST` reusing the same `conversationId` shows the prior turn was remembered (multi-turn `WorkContext` over HTTP).

`.github/workflows/ci.yml`: `dotnet restore/build/test` on push — a green badge on the repo.

## README (the most important artifact)

- One-paragraph "what this is and why," with the **not-medical-advice** disclaimer up front.
- A **screenshot / GIF** of the browser chat UI with the trace tree visible (the demo's lead image).
- A mermaid architecture diagram of the runtime (and where the web host sits relative to it).
- A **mapping table**: *concept → file/namespace in this repo → the agent-platform concern it mirrors* (e.g., `RuntimeConfigProvider` → RuntimeConfig + JSON-Patch flights; `Failure/` → CompliantException/FailureMode/degraded responses; `Context/` → Work Context Provider; `Observability/` → OpenTelemetry tracing).
- Quickstart: `dotnet run --project src/CareTriageAgent.Web` → open the browser; plus the CLI scenario-runner one-liner.
- "Enable the real Claude model" section (set `ANTHROPIC_API_KEY`, pick `anthropic` in the toolbar).

## ARCHITECTURE.md (deep-dive doc — for learning & debugging)

A separate `ARCHITECTURE.md`, written so you (and a reader) can learn and debug the runtime. It opens with the **hosting model** (domain-agnostic `AgentRuntime` ← `CareTriageAgent` domain library ← thin `*.Web` / `*.Cli` hosts) and a request walk-through for `POST /triage` (HTTP → `CareTriageSession` → orchestrator → response with serialized trace → browser render). Then, for each of the seven runtime components it gives:
- **What it does** and the agent-platform concept it mirrors.
- **Where it lives** — exact file/namespace (e.g. `src/AgentRuntime/Config/RuntimeConfigProvider.cs`) and the key type/method.
- **Data flow** — a numbered walk-through of one full turn: user input → `WorkContext` update → red-flag guardrail → planner (`ILlmClient`) → tool selection → `ExecutionScope.TryExecute` (retry/fallback/degrade) → `TriageResult` → trace emission.
- **How to debug it** — which span to read in the trace tree, how to force a `DegradedResponse` (break a tool via a flight), how to flip config without recompiling, and what each test in `AgentRuntime.Tests` pins down.

This is the learning/reference artifact; the README stays a quickstart + concept→file table and links to it.

## Verification

1. `dotnet build MiniAgentRuntime.sln` — compiles clean on .NET 8.
2. `dotnet test` — all unit + web integration tests pass (failure/degrade, red-flag, flight merge, cache, step budget, `POST /triage`).
3. `dotnet run --project src/CareTriageAgent.Web` → open the browser — runs **offline** with the mock LLM; verify in the UI: a normal flow recommends an urgency level; a red-flag input escalates to emergency; toggling the broken-clinic flight from the toolbar produces a `DegradedResponse` with a safe message rather than a crash; the **trace tree renders** for each turn; a follow-up message remembers the prior turn (multi-turn context).
4. `dotnet run --project src/CareTriageAgent.Cli -- --scenario Data/scenarios/red-flag.txt` — deterministically yields `Emergency` (CI path); apply `--flight disable-clinic-finder` and confirm behavior changes with no recompile.
5. *(Optional)* Set `ANTHROPIC_API_KEY`, choose `anthropic` in the toolbar, and confirm a live `claude-opus-4-8` planning call drives the same loop.
6. Push to GitHub; confirm the Actions CI workflow goes green.

## Notes / scope guardrails

- Public repo: use only generic names (no internal or proprietary team/system identifiers in code or docs); the README describes mirrored *concepts*, not internal details.
- Keep the real-LLM provider optional and behind config so the repo always runs offline by default.
- Health framing stays strictly educational/triage with disclaimers and an always-on emergency-escalation guardrail.

## Future stages (explicitly out of scope for this first build)

These are deferred until the web + CLI build is complete and working; captured here so the architecture leaves room for them:
1. **Live hosted URL** — deploy `CareTriageAgent.Web` to a free tier (Azure App Service / Container Apps) so the README can link a click-to-try demo, not just `dotnet run`. The runtime is unchanged; only a Dockerfile + workflow is added. (The session store would move from in-memory to distributed if multi-instance.)
2. **Rate-limit / throttling component** in the runtime (echoes your throttling + reliability work) — a cross-cutting policy around tool/LLM calls, naturally surfaced once there's a public endpoint.
3. **Server-sent events / streaming** so the browser shows agent steps and spans *as they happen* rather than after the turn completes.
4. **Additional LLM providers (pluggable, optional).** Because the runtime owns the loop behind `ILlmClient`, more providers drop in without touching orchestration: an **`OpenAiCompatibleLlmClient`** whose base URL points at **Ollama** (local, free, *no key*) or a hosted open-model endpoint (Groq / OpenRouter / Together — free tiers, OpenAI-compatible). Ships beside `AnthropicLlmClient` purely to *demonstrate* the provider abstraction. The deterministic mock stays the default so the repo always runs offline and CI stays reproducible; since strict JSON-schema structured output is uneven across open models, these providers use the runtime's lenient parse-and-validate path rather than strict structured output.
