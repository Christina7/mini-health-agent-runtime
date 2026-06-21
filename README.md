# mini-health-agent-runtime

[![CI](https://github.com/Christina7/mini-health-agent-runtime/actions/workflows/ci.yml/badge.svg)](https://github.com/Christina7/mini-health-agent-runtime/actions/workflows/ci.yml)

A small, **runnable agent runtime** in C# / .NET 8 — a domain-agnostic orchestration core
(`AgentRuntime`) hosting **two very different health agents**: a *reactive* **symptom-triage &
care-navigation** agent (`CareTriageAgent`) and a *multi-step* **health-planning** agent
(`HealthPlanAgent`). One engine, two domains, neither aware of the other — that is the proof the core
is domain-agnostic. It reproduces, in miniature, the architecture of a production agent platform: a
reason → act → observe loop, config-driven execution with JSON-Patch flights, a failure / degradation
framework, a work-context store, safety guardrails, and OpenTelemetry tracing.

> ⚠️ **Educational only — not medical advice.** This is a teaching/example project. The triage
> agent is a navigation aid with a hard-coded emergency-escalation guardrail; the planner produces
> illustrative calorie/task targets behind an always-on unsafe-goal guardrail. Neither is a
> diagnostic or clinical tool — do not use them for real medical or dietary decisions.

The default path is **fully offline and deterministic**: `dotnet run` works with **zero API keys**,
no network, and no database — all data is synthetic JSON in the repo.

---

## The first agent — symptom triage (reactive)

Type symptoms; the agent runs a triage turn and returns an **urgency level**
(`SelfCare` / `SeeGp` / `UrgentCare` / `Emergency`), a recommended action, the tools it used, a
not-medical-advice disclaimer — and a **trace tree** of the turn.

```
> dizziness and abdominal pain

Agent: Seek urgent care today. Sit or lie down... Note where the pain is and whether it worsens.

  ┌─ Triage ─────────────────────────────
  │ Urgency:     UrgentCare
  │ Action:      Seek urgent care today.
  │ Tools used:  symptom_kb
  │ Educational only — not medical advice.
  └──────────────────────────────────────

[trace]
  triage.turn  (2.40 ms)
    └ guardrail  (0.10 ms)
    └ agent.step  (1.10 ms)
      └ tool:symptom_kb  (0.40 ms)
    └ agent.step  (0.30 ms)
```

Three behaviours fall out of the architecture, all demonstrable live:

- **Red-flag escalation** — `chest pain + shortness of breath` short-circuits to an emergency message *before any planning*.
- **Graceful degradation** — a failing tool is retried, then the turn degrades to a safe answer instead of crashing (`⚠ degraded` in the trace).
- **Config-driven behaviour** — a JSON-Patch *flight* changes thresholds or disables a tool with **no recompile**.

---

## The second agent — health planning (multi-step)

Triage "feels like a chatbot" for concrete reasons, not architectural ones: it has **one tool**, takes
**one step**, and the shape is *message → reply card*. The runtime is already agentic — so the second
agent is built to **exhibit what a chatbot can't**: you *declare a goal* (lose fat / improve sleep /
boost energy) plus a simple profile, and the agent does **multi-step, multi-tool work** that produces
a **living artifact** — a plan card, a daily task checklist, and progress that accumulates across turns.

It runs on the **same runtime** as triage and shares nothing with it but `AgentRuntime`. Two distinct
multi-tool chains become visible in the trace:

- **Create a plan** → `profile_analyzer → plan_generator → task_decomposer → finish`. `profile_analyzer`
  computes BMR (Mifflin–St Jeor) / TDEE / BMI; `plan_generator` **reads those observations** (a real
  upstream dependency the trace shows — it doesn't recompute) and applies a goal + deficit-cap policy;
  `task_decomposer` turns the targets into a checklist.
- **Log a day** → `nutrition_calculator → progress_evaluator → finish`. The day's calories vs. target →
  under / on / over, then a short human annotation appended to the artifact's progress list.

```
┌ Plan ───────────────────────────┐  ┌ Trace — create turn ─────────────────┐
│  LOSE FAT                        │  │ ▾ plan.turn                2.6 ms ▕██▏ │
│  2331 kcal/day · 144 g · 133 d   │  │   ▾ guardrail              0.1 ms ▕▏  │
│  A safe ~20% deficit; cap-bound  │  │   ▾ agent.step             1.0 ms ▕█▏ │
│  ☐ Nutrition  hit protein target │  │     ▾ tool:profile_analyzer 0.4 ms ▕▌│
│  ☐ Movement   8k steps           │  │   ▾ agent.step             0.7 ms ▕▋▏│
│  ☐ Sleep      7–8 h              │  │     ▾ tool:plan_generator  0.3 ms ▕▍│
│  Educational only — not advice.  │  │   ▾ agent.step  …  tool:task_decomposer│
└──────────────────────────────────┘  └──────────────────────────────────────┘
```

The same agentic properties as triage fall out of the architecture, made visible in the planner console:

- **Safety by construction** — an always-on `UnsafeGoalGuardrail` (no flight can disable it) short-circuits
  a `LoseFat` goal that is already underweight, *aims* at underweight, or implies a crash-diet intake
  (< 1200 kcal), returning a professional-referral message with **no plan**. Request 80 kg in 28 days or
  84 days and you get the **same safe plan** — the deficit cap binds, so the timeline stretches rather
  than the intake dropping.
- **Config-driven intensity** — `aggressive-plan` / `conservative-plan` flights move the deficit *cap*
  (≈ 25 % vs. 15 % of TDEE), so the same goal yields a different calorie target with **no recompile**.
- **Graceful degradation** — the "Break plan generator" toggle makes a tool throw; the turn still returns
  a conservative, clearly-`⚠ degraded` plan instead of crashing, and the failed tool is flagged in the trace.

The agent is driven by a **typed envelope**, not a chat string — the host deserializes the HTTP body into
a `PlanEnvelope` and hands it to the session out-of-band (so `WorkContext.History` never fills with JSON):

```jsonc
// POST /plan — create
{ "action": "Create", "goal": "LoseFat",
  "profile": { "ageYears": 30, "sex": "Male", "weightKg": 90, "heightCm": 180,
               "activityLevel": "Moderate", "targetDays": 84, "goalWeightKg": 80 } }

// POST /plan — log a day (the prior plan is held server-side per conversationId; not resent)
{ "action": "Log", "conversationId": "…", "log": { "caloriesLogged": 2200, "tasksCompleted": 3 } }
```

> Inputs are **metric-only** (kg/cm) — the browser form converts before POST, so the tested core never
> sees a unit flag. The running plan lives in the session, in-memory, lost on restart (same as triage's
> session store). The `/plan` contract and `PlanEnvelope` schema are documented in [DESIGN.md](DESIGN.md).

_Open `/plan-app` in the running web app to drive it: set a goal, watch the create-chain trace, log a
day, toggle the flights and the degrade switch._

---

## Architecture

`AgentRuntime` is **domain-agnostic** — zero health knowledge, and it could host any agent. The proof
is that it hosts **two**: `CareTriageAgent` and `HealthPlanAgent` are **siblings** that each reference
`AgentRuntime` only, never each other. Each has its own composition root
(`CareTriageSession` / `HealthPlanSession`) wiring the same engine to a different domain. Thin hosts
contain no agent logic — the web app (`HealthAgents.Web`) serves both (`POST /triage`, `POST /plan`),
and a console CLI drives triage.

The only net-new **runtime** code the second agent required is **one line**: the orchestrator's root
span name became a constructor argument (`"triage.turn"` vs `"plan.turn"`). Everything else — the loop,
the trace renderer, the session store, flight loading, the degrade mapping, the `ToJson`/`FromJson`
pattern — is reused as-is. That reuse *is* the domain-agnostic claim, demonstrated rather than asserted.

![Architecture: two thin hosts converge on one composition root, built on a domain-agnostic core holding the seven runtime concerns](docs/diagrams/architecture.png)

<sub>Source: [`docs/diagrams/architecture.excalidraw`](docs/diagrams/architecture.excalidraw) — editable on [excalidraw.com](https://excalidraw.com).</sub>

One turn: `OnUserMessage` → **guardrail pipeline** (red-flag runs first, can short-circuit) →
**plan → act → observe loop** (planner picks the next step; tools run through `ExecutionScope`;
observations feed back) → **Finish** with a `TriageResult` → trace tree emitted.

![One turn: BeginTurn → guardrail (red-flag short-circuits to an emergency reply) → a bounded plan→act→observe loop with a tool through ExecutionScope (retry→degrade→fallback) → Finish with a TriageResult, the trace tree emitted throughout](docs/diagrams/one-turn-flow.png)

> **Deep dive:** [ARCHITECTURE.md](ARCHITECTURE.md) walks the hosting model, a `POST /triage`
> request, one full turn, and how to debug each component. Original design notes — contracts,
> schemas, control flow — are in [DESIGN.md](DESIGN.md).

---

## How it was built — TDD slices

Built test-first in vertical slices; each left the repo green and runnable. (Each row = one merged PR.)

**Milestone 1 — the engine** (offline, headless, CLI):

| # | Slice | Contribution | Key types |
|---|-------|--------------|-----------|
| 1 | Orchestrator finish path | Core reason→act→observe loop skeleton | `AgentOrchestrator`, `WorkContext`, `ILlmClient`, `PlanDecision`, `TurnResult` |
| 2 | Act → observe loop | Tool framework + the real loop | `ITool`, `ToolRegistry`, `PlanDecision.CallTool`, observations |
| 3 | Red-flag guardrail | Pre-planning safety pipeline | `IGuardrail`, `GuardrailVerdict`, `RedFlagGuardrail` |
| 4 | Step budget | Bounded loop → safe degraded fallback (no infinite loop) | `MaxSteps` |
| 5 | Triage policy | Pure score → urgency mapping | `TriagePolicy`, `UrgencyLevel`, `TriageThresholds` |
| 6 | Triage brain | Real symptom scoring + planner + structured result | `SymptomKnowledgeBaseTool`, `MockTriagePlanner`, `TriageResult` |
| 7 | Failure framework | Unified retry / degrade / swallow + safe error messages | `ExecutionScope`, `ScopeResult<T>`, `CompliantException`, `FailureMode` |
| 8 | Degrade wiring | Tool calls run through the scope → live degradation | `WorkContext.Degraded` |
| 9 | Config engine | Base config + ordered JSON-Patch flight overlays (allow-listed) | `RuntimeConfigProvider` |
| 10 | Config wiring | Thresholds / retries / enabled tools from config + `--flight` | `CareTriageConfig` |
| 11 | Safety invariant | Composition root; **no flight can disable the guardrail** | `CareTriageSession` |
| 12 | Observability | Per-turn OpenTelemetry trace tree (latency + degraded tag) | `RuntimeActivitySource`, `TraceCollector`, `TraceNode` |

**Milestone 2 — the web app** (same runtime, server-side, with a visual trace tree):

| # | Slice | Contribution | Key types |
|---|-------|--------------|-----------|
| 13 | Web host | `POST /triage` over the same `CareTriageSession`; per-conversation session store; `WebApplicationFactory` test | `HealthAgents.Web`, `TriageSessionStore`, `CareTriageDomain` |
| 14 | Browser UI | Color-coded triage card + collapsible trace tree + demo toolbar (no build step). Plus a per-turn state-reset fix surfaced by multi-turn use | `wwwroot/index.html`, `WorkContext.BeginTurn` |
| 15 | CI | GitHub Actions build + test on every push/PR, green badge | `.github/workflows/ci.yml` |

**Milestone 3 — a second agent** (multi-step health planning on the *same* runtime):

| # | Slice | Contribution | Key types |
|---|-------|--------------|-----------|
| 16 | Second-agent skeleton | New `HealthPlanAgent` library (domain types + `HealthPlanResult` round-trip) and the **one** runtime change: a parameterized root span name | `HealthPlanResult`, `PlanMath`, `rootSpanName` |
| 17 | Create chain | `profile_analyzer → plan_generator → task_decomposer`; `plan_generator` **consumes the analyzer's TDEE** from observations; deficit-*cap* policy | `MockHealthPlanner`, `PlanPolicy`, `TurnInputHolder` |
| 18 | Log chain + living artifact | `nutrition_calculator → progress_evaluator`; typed `PlanEnvelope`; progress accumulates across turns | `PlanEnvelope`, `ProgressEntry`, `DayStatus` |
| 19 | Composition root + safety | `HealthPlanSession` owns the holder and threads the artifact; `UnsafeGoalGuardrail` wired **unconditionally** | `HealthPlanSession`, `UnsafeGoalGuardrail`, `HealthPlanConfig` |
| 20 | Web + flights + console | `POST /plan` + `PlanSessionStore`; aggressive / conservative flights; planner console at `/plan-app` + degrade demo | `HealthAgents.Web`, `wwwroot/planner.html` |

**74 unit + integration tests** pin every behaviour (xUnit + Moq + `WebApplicationFactory`).

---

## Concept → file map

The runtime mirrors the standard concerns of a production agent platform. Each one is a
self-contained, navigable piece of the codebase:

| Concept | Where it lives | What it does |
|---------|----------------|-------------|
| Agent orchestration | `AgentRuntime/Orchestration/AgentOrchestrator.cs` | Multi-step reason→act→observe loop (tool calls / decision loops) |
| Config-driven execution + flights | `AgentRuntime/Config/RuntimeConfigProvider.cs` | Base config + JSON-Patch flights — change behavior with no recompile |
| Failure / degradation framework | `AgentRuntime/Failure/` | `CompliantException` / `FailureMode` / degraded responses; retry→degrade→fallback |
| Work context store | `AgentRuntime/Context/WorkContext.cs` | Cross-turn state / memory (per-conversation `History`, per-turn reset) |
| Tool selection & invocation | `AgentRuntime/Tools/` | Tool registry + invocation strategy |
| Distributed tracing | `AgentRuntime/Observability/` | OpenTelemetry spans: agent steps, tool chains, latency breakdown |
| Safety invariant | `CareTriageAgent/Guardrails/RedFlagGuardrail.cs` · `HealthPlanAgent/Guardrails/UnsafeGoalGuardrail.cs` | An always-on guardrail per agent that config/flights cannot override |
| Two agents, one runtime | `CareTriageAgent/` · `HealthPlanAgent/` (siblings on `AgentRuntime`) | The same engine drives a reactive one-tool agent and a multi-step, multi-tool one |

---

## Inputs & outputs

**Input** — free-text symptoms, plus optional flags:

| Flag | Effect |
|------|--------|
| `--flight <name>` | Apply an allow-listed JSON-Patch overlay (repeatable). Shipped: `strict-thresholds`, `disable-symptom-kb` |
| `--break-symptom-kb` | Force the symptom tool to fail (demonstrates retry → degrade → safe fallback) |

**Output** — the agent reply, a structured `TriageResult` (`urgency`, `recommendedAction`,
`toolsInvoked`, `degraded`, `disclaimer`), and the turn's trace tree.

The **health planner** is web-only (`POST /plan`); its typed `PlanEnvelope`, intensity flights
(`aggressive-plan` / `conservative-plan`), and `breakPlanGenerator` degrade toggle are described under
[The second agent](#the-second-agent--health-planning-multi-step) above and in [DESIGN.md](DESIGN.md).

---

## Quick start

**Prerequisite:** the .NET 8 SDK — nothing else.

```bash
dotnet build MiniHealthAgentRuntime.sln
dotnet test                                                                 # 74 passing

# The web app serves both agents (offline, no keys):
#   /  guided walkthrough   ·   /app  triage chat   ·   /plan-app  health planner
dotnet run --project src/HealthAgents.Web                                # then open the printed http://localhost:5xxx
#   press Ctrl+C in this terminal to stop the server

# Triage flows via the CLI (all offline, deterministic)
dotnet run --project src/CareTriageAgent.Cli -- "sore throat and mild fever"           # SelfCare
dotnet run --project src/CareTriageAgent.Cli -- "dizziness and abdominal pain"         # UrgentCare
dotnet run --project src/CareTriageAgent.Cli -- "severe chest pain and shortness of breath"  # 🚨 red-flag

# Config-driven behaviour — same input, different outcome, no recompile
dotnet run --project src/CareTriageAgent.Cli -- --flight strict-thresholds "headache"  # SelfCare → SeeGp
dotnet run --project src/CareTriageAgent.Cli -- --flight disable-symptom-kb "sore throat"

# Graceful degradation
dotnet run --project src/CareTriageAgent.Cli -- --break-symptom-kb "sore throat"       # ⚠ degraded, no crash
```

> **Just want the tour?** A self-contained **walkthrough page** explains the architecture and replays
> the four behaviors (with trace trees) — no server needed. Open
> `src/HealthAgents.Web/wwwroot/walkthrough.html` directly in a browser, or run the web app and
> visit `/` (the live chat app is at `/app`).

---

## Milestone 2 — the web app

Milestone 1 (above) is the **engine**: the whole runtime, proven offline, headless, with a CLI
surface. Milestone 2 turns it into a **browser app** that runs the same runtime server-side and
*visualizes* the trace tree — built, and shipped in the slices above.

```
┌ Conversation ───────────────┐ ┌ Trace — last turn ──────────────────┐
│ › dizziness and abdominal…  │ │ ▾ triage.turn            2.40 ms ▕██▏ │
│ ┌ UrgentCare ─────────────┐ │ │   ▾ guardrail            0.10 ms ▕▍ │ │
│ │ Seek urgent care today. │ │ │   ▾ agent.step           1.10 ms ▕█▏ │
│ │ Tools invoked: symptom… │ │ │     ▾ tool:symptom_kb    0.40 ms ▕▌ │ │
│ │ Educational only — not… │ │ │   ▾ agent.step           0.30 ms ▕▎ │ │
│ └─────────────────────────┘ │ └──────────────────────────────────────┘
```

> _Run `dotnet run --project src/HealthAgents.Web` and open the printed URL to see the live UI._
<!-- Lead image: capture a screenshot of the browser UI and drop it in, e.g. docs/web-ui.png, then:
![Care Triage web UI — chat pane + live trace tree](docs/web-ui.png) -->

The runtime itself doesn't change for the web surface — the host is a thin new layer over the same
`CareTriageSession`. That separation is the point: it's a **runtime**, not a script. (The one runtime
edit during M2 was a bug fix: per-turn working state is now reset each turn so follow-ups are
re-scored.)

### Future / not implemented

Captured here so the architecture leaves room for them; **not built** in the current repo:

- **Provider cache** — a cacheable `IWorkContextProvider` (e.g. a clinic-finder) with query-dependent
  cross-turn memoization, so a repeated query would show a **cache-hit span** in the trace. The
  `WorkContext` store is in place; the cacheable-provider mechanism is not.
- **Real Claude provider** — an `AnthropicLlmClient` behind config (`ANTHROPIC_API_KEY`); the
  deterministic mock stays the default so the repo always runs offline.
- **Live hosted URL / streaming / rate-limiting** — see [DESIGN.md](DESIGN.md) → Future stages.
