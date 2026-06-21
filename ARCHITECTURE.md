# Architecture

A deep-dive for reading, extending, and debugging the runtime. The [README](README.md) is the
quick start and concept→file map; this document explains how the pieces fit and how one turn flows
through them. For the original design rationale, contracts, and schemas, see [DESIGN.md](DESIGN.md).

> ⚠️ **Educational only — not medical advice.** The triage agent is a navigation aid with a
> hard-coded emergency-escalation guardrail, not a diagnostic system.

---

## Hosting model

Three layers, each depending only on the one below it:

```
HealthAgents.Web   CareTriageAgent.Cli      ← thin hosts (HTTP / console). No agent logic.
        \                   /
         CareTriageAgent                        ← the HEALTH domain: tools, guardrail, planner, data
              |
         AgentRuntime                           ← domain-agnostic runtime: the reusable core
```

- **`AgentRuntime`** is the reusable core. It knows nothing about health — it owns the agent loop,
  the tool/guardrail/LLM abstractions, config + flights, the failure framework, the work-context
  store, and tracing. It could host any agent.
- **`CareTriageAgent`** is the health domain built on top: the symptom knowledge base, the red-flag
  guardrail, the deterministic planner, the triage policy, and the shared domain data
  (`CareTriageDomain`). Its composition root, `CareTriageSession`, wires the runtime to the domain.
- **`HealthAgents.Web`** and **`CareTriageAgent.Cli`** are thin hosts. Neither contains agent
  logic: the Web host marshals HTTP ↔ runtime; the CLI host feeds a message from the command line.
  Both drive the *same* `CareTriageSession.OnUserMessageAsync`.

That separation is the point — it's a **runtime**, not a triage script. The first thing to verify
when extending it: domain concepts (symptoms, urgency, chest pain) must never leak into
`AgentRuntime`.

---

## Request walk-through — `POST /triage`

What happens when the browser posts a message (`src/HealthAgents.Web/Program.cs`):

1. **Bind + validate.** The JSON body binds to `TriageRequest { conversationId?, message, flights?,
   provider?, breakSymptomKb? }`. A blank `message` → `400`.
2. **Resolve the conversation.** An absent `conversationId` is minted server-side. `TriageSessionStore`
   (`ConcurrentDictionary<string, CareTriageSession>`, idle-expiry) returns the existing session for
   that id, or builds one on first contact via `BuildSession`. A conversation's config is therefore
   fixed when its session is created — the first request for an id wins — which keeps a multi-turn
   session internally consistent.
3. **Build the session (first turn only).** `BuildSession` resolves the effective `CareTriageConfig`
   from the base config + requested flights (allow-listed — an unknown flight throws and maps to
   `400`), selects the symptom tool (or `BrokenSymptomTool` if `breakSymptomKb`), and constructs the
   `CareTriageSession` with the always-on red-flag guardrail.
4. **Run one turn.** `session.OnUserMessageAsync(message)` drives the orchestrator (see below).
5. **Serialize the response.** `TriageResponse { conversationId, reply, triage?, trace?, degraded,
   turnCount }`. `triage` is the structured `TriageResult` (null until a turn reaches a Finish);
   `trace` is the serialized `TraceNode` tree; `turnCount` is the number of remembered user turns
   (which makes the server-side multi-turn memory observable). A `CompliantException` maps to a `200`
   carrying only the user-safe message + `degraded:true` — internal detail never reaches the client.
6. **Render.** `wwwroot/index.html` draws the color-coded triage card and the collapsible trace tree.

The CLI host (`src/CareTriageAgent.Cli/Program.cs`) does the same minus HTTP: parse args → build the
session → one `OnUserMessageAsync` → print the reply, triage card, and trace as text.

---

## One turn through the orchestrator

![One turn: user message → BeginTurn → guardrail (red-flag short-circuits to an emergency reply) → a bounded plan→act→observe loop with a tool through ExecutionScope → Finish with a TriageResult, while the trace tree is emitted throughout](docs/diagrams/one-turn-flow.png)

<sub>Source: [`docs/diagrams/one-turn-flow.excalidraw`](docs/diagrams/one-turn-flow.excalidraw).</sub>

`AgentOrchestrator.RunTurnAsync` (`src/AgentRuntime/Orchestration/AgentOrchestrator.cs`) is the
canonical loop. In order:

1. **Start the trace.** A `TraceCollector` is created and the root `triage.turn` Activity is started
   (`RuntimeActivitySource`). Every span this turn is bucketed by the root's `TraceId`.
2. **Begin the turn.** `ctx.BeginTurn()` clears the previous turn's working state (observations +
   degraded flag) so this turn is assessed on its own merits; conversation `History` carries over.
   `ctx.AppendUser(message)` records the new message.
3. **Guardrail pipeline.** Every registered `IGuardrail` runs *before any planning*, each in a
   `guardrail` span. A short-circuiting verdict (the red-flag rule matching) ends the turn immediately
   with an emergency message — no planning, no tools.
4. **Plan → act → observe loop**, bounded by `MaxSteps`. Each iteration is an `agent.step` span:
   - `ILlmClient.PlanNextStepAsync` returns a `PlanDecision`.
   - `CallTool` → the tool runs inside a `tool:<name>` span, wrapped by `ExecutionScope.TryExecuteAsync`
     (retry → degrade → fallback). The result is recorded as an observation; a degrade sets
     `ctx.Degraded`.
   - `Finish` → append the agent message and return a `TurnResult` carrying the JSON result payload.
5. **Budget exhausted** → a safe, degraded fallback reply (never an infinite loop, never a crash).
6. **Build the tree.** The collector reconstructs the `TraceNode` tree from the activities for this
   `TraceId`, attached to the `TurnResult`.

---

## The components

For each: **what it does** (and the agent-platform concept it mirrors) · **where it lives** · **how
to debug it**.

### 1. Agent orchestration
- **What.** The multi-step reason→act→observe loop: guardrails, then plan→act→observe until a Finish
  or the step budget. Mirrors core agent orchestration / tool-call decision loops.
- **Where.** `AgentRuntime/Orchestration/AgentOrchestrator.cs`; entry `RunTurnAsync`. Decisions:
  `Llm/PlanDecision.cs` (`CallTool` | `AskUser` | `Finish`).
- **Debug.** Read the trace tree top-down: `triage.turn → guardrail / agent.step → tool:*`. A turn
  that ends after `guardrail` short-circuited; a turn with N `agent.step` spans took N planner steps.

### 2. Config-driven execution + flights
- **What.** Effective config = base `runtimeconfig.json` + ordered, allow-listed JSON-Patch *flight*
  overlays. Controls enabled tools, urgency thresholds, and retry counts — no recompile. Mirrors
  feature-flag / runtime-config systems.
- **Where.** `AgentRuntime/Config/RuntimeConfigProvider.cs` (uses `JsonPatch.Net`); typed result
  `CareTriageAgent/Config/CareTriageConfig.cs`. Flights in each host's `config/flights/*.json`.
- **Debug.** To change behavior without code: add/activate a flight. An unknown flight name throws
  `ArgumentException` (the allow-list) — by design, so a client can't post arbitrary patch paths.
  Try `--flight strict-thresholds` (CLI) or the toolbar checkbox (Web) and watch the urgency shift.

### 3. Failure / degradation framework
- **What.** `ExecutionScope.TryExecuteAsync(op, mode, action, fallback)` applies retry → then, for
  `CanImpactResponse`, degrade + fallback (for `NeverImpactsResponse`, swallow). `CompliantException`
  separates the user-safe message from internal detail. Mirrors unified error-handling / degraded
  responses.
- **Where.** `AgentRuntime/Failure/` (`ExecutionScope`, `ScopeResult<T>`, `CompliantException`,
  `FailureMode`). Wired in the orchestrator's `CallTool` branch.
- **Debug.** Force a failure: `--break-symptom-kb` (CLI) or the **Break symptom KB** toggle (Web)
  swaps in `BrokenSymptomTool`. The `tool:symptom_kb` span turns `degraded`, the turn is marked
  degraded, and the planner returns a safe "couldn't verify — consult a professional" result.

### 4. Tool framework
- **What.** `ITool` exposes a name, JSON input schema, and `ExecuteAsync`; `ToolRegistry` holds the
  config-enabled tools and hands their descriptors to the planner. Mirrors tool selection & invocation.
- **Where.** `AgentRuntime/Tools/` (`ITool`, `ToolRegistry`). Concrete:
  `CareTriageAgent/Tools/SymptomKnowledgeBaseTool.cs`.
- **Debug.** A disabled tool (`disable-symptom-kb`) disappears from the planner's descriptors;
  `toolsInvoked` in the result tells you which tools actually ran.

### 5. Work context / memory
- **What.** `WorkContext` holds per-conversation `History` (cross-turn memory) and the current turn's
  observations + degraded flag. `BeginTurn()` resets the per-turn state each turn while preserving
  History. Domain-agnostic — health state goes through a typed bag, never as runtime types.
- **Where.** `AgentRuntime/Context/WorkContext.cs`.
- **Debug.** Over HTTP, `turnCount` rises as a conversation continues; a follow-up's trace shows the
  symptom tool re-running (per-turn state), while History persists. *Note:* the cacheable
  `IWorkContextProvider` (query-dependent memoization with a visible cache-hit span) is **not
  implemented** in the current build — see README → Future.

### 6. Observability / distributed tracing
- **What.** An `ActivitySource` emits spans for the conversation, each step, and each tool, with
  latency and a `degraded` tag. A process-wide `ActivityListener` buckets sampled activities by the
  root `triage.turn` `TraceId` (so concurrent requests never cross-contaminate) and reconstructs a
  serializable `TraceNode` tree. Mirrors OpenTelemetry distributed tracing.
- **Where.** `AgentRuntime/Observability/` (`RuntimeActivitySource`, `TraceCollector`, `TraceNode`).
- **Debug.** The tree *is* the debugger: latency per span, which tool degraded, whether the guardrail
  short-circuited. The Web host returns it as JSON; the CLI prints it as text.

### 7. Safety invariant (domain)
- **What.** The red-flag guardrail runs *outside* the tool registry, first, every turn. **No flight
  or config can disable it** — there is no guardrail toggle to begin with. Every reply carries the
  not-medical-advice disclaimer.
- **Where.** `CareTriageAgent/Guardrails/RedFlagGuardrail.cs`, registered unconditionally by
  `CareTriageAgent/CareTriageSession.cs`. Pinned by `SafetyInvariantTests`.
- **Debug.** Post a cardiac message with every tool disabled and any flights applied — it still
  escalates. That immunity is the property the test guards.

---

## Where the tests pin each behavior

`tests/AgentRuntime.Tests` (xUnit + Moq) pins the runtime + domain; `tests/HealthAgents.Web.Tests`
(`WebApplicationFactory`) pins the HTTP surface.

| Behavior | Test |
|----------|------|
| Red-flag escalates, even with tools off | `SafetyInvariantTests` |
| Flights merge (threshold + disable tool) | `SafetyInvariantTests`, config tests |
| Retry → degrade → fallback / swallow | `ExecutionScopeTests`, `MockTriagePlannerTests` |
| Step budget → safe fallback (no infinite loop) | `AgentOrchestratorTests` |
| Per-turn state resets across turns | `MultiTurnStateTests` |
| Trace tree shape + degraded span | `TracingTests` |
| `POST /triage`: emergency + trace + multi-turn memory | `TriageEndpointTests` |
