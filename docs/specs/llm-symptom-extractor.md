# Spec: LLM symptom extractor (narrow NLU front-end for triage)

Status: draft v2 (incorporates code review) · Owner: Christina7 · Source: design interview + review.

## 0. Alignment with current source (read first)

This spec targets the **current code and README**, not DESIGN.md's aspirational sections. DESIGN.md
still describes an `AnthropicLlmClient` *planner* (`claude-opus-4-8`), a provider cache, and a
`clinic_finder` tool that **do not exist** in the source; README's `### Future / not implemented`
correctly marks those unbuilt. This spec deliberately does **not** implement DESIGN's full Anthropic
*planner*. It adds a narrower, separate capability — an LLM **symptom extractor** behind its own
domain seam. Reconciling DESIGN.md with reality is a **separate follow-up**, out of scope here.

## 1. Motivation

Triage matches symptoms by literal substring (`SymptomKnowledgeBaseTool` → `text.Contains(keyword)`,
`SymptomKnowledgeBaseTool.cs:35`). That is brittle: #36 ("chest pressure" scored 0 → SelfCare) was a
missing keyword, and the list mishandles negation ("I do **not** have chest pain" still substring-
matches). Phrasing space is infinite; a keyword list is finite. **This is an NLU problem, not a
storage problem** — a DB would store the same brittle list elsewhere.

The project's thesis is a reusable agent runtime with "decide next step" isolated behind the
`ILlmClient` seam, today driven by deterministic mock planners. This spec adds real LLM capability in
a **deliberately narrow role** without dissolving the deterministic safety core.

## 2. Decisions (settled in interview)

1. **LLM role = narrow NLU extractor, not the planner.** The LLM only turns free text into a
   normalized symptom list. The planner still selects tools, `TriagePolicy` still classifies urgency,
   `RedFlagGuardrail` still owns the safety floor. The LLM proposes which known symptoms are present;
   the deterministic core disposes the urgency.
2. **Placement = a `symptom_extractor` tool, with keyword matching as the offline fallback.** Planner
   calls it before `symptom_kb`. `llmProvider:"mock"` ⇒ keyword extractor; `"anthropic"` ⇒ LLM
   extractor. Makes triage genuinely multi-step.
3. **Output contract = closed-set IDs + present/absent.** Only IDs already in the taxonomy; no
   severity, no urgency; unknown symptoms omitted → safely contribute 0.
4. **Taxonomy in a versioned JSON file**, the single source feeding both the LLM prompt and the
   deterministic scoring **and the red-flag rules** (see §5). The KB needs no DB.
5. **Persistence = append-only JSONL audit only. No DB.** Sessions stay in-memory; plan-agent
   durability out of scope.
6. **Tests stay deterministic** via a fake extractor (see §6).

## 3. Seams & architecture (resolves review High-2; arch note)

The extractor is a **CareTriageAgent domain feature**, not a runtime feature — it follows the same
tool pattern HealthPlanAgent uses. The runtime `ILlmClient` (planner seam, returns `PlanDecision`,
`ILlmClient.cs:10`) is **unchanged and unrelated**.

New domain seam in `CareTriageAgent`:
```csharp
public interface ISymptomExtractor          // CareTriageAgent.Triage
{
    Task<SymptomExtraction> ExtractAsync(string userText, CancellationToken ct);
}
public sealed record SymptomExtraction(
    IReadOnlyList<ExtractedSymptom> Symptoms, string Provider, bool Fallback);
public sealed record ExtractedSymptom(string Id, bool Present);
```
Implementations:
- `KeywordSymptomExtractor` (mock; today's substring logic moves here).
- `AnthropicSymptomExtractor` (LLM; owns its own timeout — see §7 — and internal fallback — §4).
- `ScriptedSymptomExtractor` (test double returning canned results; the deterministic test seam).

The `symptom_extractor` **tool** is a thin `ITool` that delegates to the injected `ISymptomExtractor`
and serializes `SymptomExtraction` to its `ToolResult.Output`. Provider selection (`mock`/`anthropic`)
picks which `ISymptomExtractor` the composition root injects — exactly mirroring `llmProvider`.

## 4. Failure / degraded model (resolves review High-1, Medium-6)

**Hard rule: `symptom_extractor` never throws on a provider failure or timeout.** The
`AnthropicSymptomExtractor` catches its own exceptions/timeouts, falls back to keyword extraction, and
returns a **successful** `SymptomExtraction` with `Fallback = true`. The tool therefore returns
`ToolResult(Success: true, …)`.

Consequence: the orchestrator's `CanImpactResponse` path (`AgentOrchestrator.cs:117-128`), which sets
`ctx.Degraded = true` on terminal tool failure, **does not fire for provider issues** — a keyword
fallback is a valid, non-degraded answer. `ctx.Degraded` remains reserved for genuinely unavailable
extraction (e.g. the tool disabled, or even the keyword path failing — practically never), which
routes into the planner's existing safe-degrade branch (`MockTriagePlanner.cs:36` → `SeeGp`). The
`Fallback = true` flag is recorded in the audit log, not surfaced as degraded to the user.

## 5. Data contracts (resolves review High-3, Medium-7)

### 5.1 Taxonomy JSON — one source for scoring, prompt, AND red-flags

Phrase sets are named and **referenced** by both symptoms and red-flag groups, so the KB keyword set
and the red-flag chest group are *structurally the same list* — drift is impossible by construction
(this generalizes the shared `CardiacChestPhrases` constant at `CareTriageDomain.cs:19`). `taxonomyVersion`
is logged in every audit record.

```json
{
  "taxonomyVersion": "2026-06-28.1",
  "phraseSets": {
    "cardiac_chest": ["chest pain", "chest pressure", "chest tightness"],
    "breathing":     ["shortness of breath", "short of breath", "trouble breathing", "difficulty breathing"]
  },
  "symptoms": [
    {
      "id": "chest_pain",
      "keywordsRef": "cardiac_chest",
      "baseSeverity": 7,
      "selfCareAdvice": "Chest pain should be assessed promptly.",
      "llmDescription": "Cardiac-type chest discomfort: pain, pressure, or tightness in the chest."
    }
  ],
  "redFlags": [
    {
      "id": "cardiac",
      "allOfAny": ["cardiac_chest", "breathing"],
      "message": "🚨 Possible cardiac emergency — call your local emergency number / go to the ER now."
    }
  ]
}
```
`allOfAny` lists phraseSet **names** (each name = one OR-group; all groups AND). Slice A retains the
#36 invariant test, now phrased structurally: *every phraseSet a red-flag references is also reachable
by a symptom's `keywordsRef`* (or simply: KB chest keywords ⊇ the cardiac group — still true because
both point at `cardiac_chest`).

### 5.2 `symptom_extractor` tool output
```json
{ "symptoms": [ { "id": "chest_pain", "present": true } ], "provider": "anthropic", "fallback": false }
```

### 5.3 Extractor → KB data flow (explicit, no hidden coupling)
The **planner** reads the `symptom_extractor` observation from `ctx.Observations`, takes the
`present: true` IDs, and passes them as **explicit args** to `symptom_kb`:
```json
{ "presentIds": ["chest_pain"] }
```
`symptom_kb` becomes a **pure scorer**: sum `baseSeverity` over `presentIds`. It no longer reads raw
text (`SymptomKnowledgeBaseTool.cs:35` text-matching is deleted; that logic now lives only in
`KeywordSymptomExtractor`). It does **not** reach into `ctx` for another tool's observation.

### 5.4 Disabled-tool matrix (safety-critical)
| state | behavior |
|---|---|
| both enabled | normal: extractor → present IDs → kb scores → policy classifies |
| `symptom_kb` disabled (existing `disable-symptom-kb` flight) | as today → planner safe-degrade → `SeeGp` |
| `symptom_extractor` disabled | no present IDs available → **must NOT** silently score 0 → SelfCare (that is the #36 unsafe direction). Routes into the same safe-degrade branch → `SeeGp`. |

The red-flag guardrail is unaffected by either toggle (registered outside configurable tools,
`CareTriageSession.cs:34`).

## 6. Testing (resolves review High-2 testing claim)

- All **existing** tests run with `llmProvider=mock` (the `KeywordSymptomExtractor` path) → stay
  deterministic, offline, green.
- New unit tests inject `ScriptedSymptomExtractor` (canned `SymptomExtraction`) to cover: closed-set
  filtering, present/absent → scoring, the internal-fallback path sets `Fallback=true` without
  degrading, and the disabled-tool matrix.
- **Live-API tests are opt-in** via an env var and skipped in CI. No network in the default suite.

## 7. Red-flag negation: intentional fail-safe (resolves review Medium-4)

`RedFlagGuardrail` keeps **conservative substring matching on raw text** (`RedFlagGuardrail.cs:40`).
We **do not** add negation handling to the guardrail: for an emergency rule, a false positive
("no chest pain, but short of breath" → escalates) is the **safe** direction and is **intentional**.
Real negation handling is scoped to the **scoring** path only (via the extractor's present/absent).
Add a test asserting and documenting this intentional guardrail behavior.

## 8. Audit log — operational design (resolves review Medium-5)

- **Abstraction:** `IAuditSink { Task WriteAsync(TriageAuditRecord r); }`; default `JsonlAuditSink`.
  Injected at the composition root → in-memory sink in tests (full isolation, no disk).
- **Path/config:** `audit.path` (default: `logs/triage-audit.jsonl` under content root), `audit.enabled`
  (default true), `audit.includeRawText` (default **false** — see PII).
- **Concurrency (web host is concurrent):** serialize writes through a single-writer queue
  (`System.Threading.Channels`) or a lock; one JSON object per line, flushed per record. No interleaving.
- **PII:** raw symptom text is health data. Default **redact** (`includeRawText=false` → store a hash
  or omit `inputText`); opt-in to raw text for local debugging only. Documented as a deliberate choice.
- **Rotation:** size/day-based rollover (e.g. `triage-audit-YYYY-MM-DD.jsonl`); if deferred, say so.
- **Record fields:**
```json
{
  "timestamp": "2026-06-28T12:00:00Z", "conversationId": "…", "turnIndex": 1,
  "inputText": null,                      // or hash, gated by audit.includeRawText
  "extracted": [{ "id": "chest_pain", "present": true }],
  "provider": "anthropic", "fallback": false, "model": "claude-haiku-4-5",
  "taxonomyVersion": "2026-06-28.1", "promptVersion": "v1",
  "score": 7, "urgency": "UrgentCare", "redFlag": null
}
```

## 9. Defaults

- **Model:** Anthropic **Haiku** (light/cheap/fast; intentionally narrower than DESIGN's planner
  `opus-4-8`). Key via `ANTHROPIC_API_KEY`; never committed.
- **Timeout:** owned by `AnthropicSymptomExtractor` via a linked `CancellationTokenSource` (ExecutionScope
  has no per-call timeout, `ExecutionScope.cs:24`). On timeout → internal keyword fallback (§4).
- **Retry:** the runtime `ExecutionScope` still wraps the tool for transient retry, but provider
  failures are already absorbed internally, so retries mostly cover the keyword path (cheap).
- **Safety invariant (hard):** `RedFlagGuardrail` always runs on raw user text, independent of the
  extractor; red-flag escalation never depends on the LLM.

## 10. Non-goals / out of scope

LLM as planner / free tool selection · LLM emitting severity or urgency · any database · durable
sessions / plan-agent longitudinal persistence · vector store / RAG · changing thresholds, red-flag
AND/OR semantics, or guardrail primacy · reconciling DESIGN.md (separate follow-up).

## 11. Risks / open questions

- Prompt-injection of the extractor — bounded by closed ID set + guardrail on raw text; add a test.
- Added per-turn latency (one Haiku call) — fallback bounds the tail; audit lets us measure.
- Cost per turn — revisit input-hash caching if needed.
- `promptVersion` discipline — bump on any prompt change so audit records stay interpretable.

## 12. Proposed TDD slices (one PR each, off `main`)

- **A — Taxonomy to JSON.** Move symptoms + phraseSets + red-flag rules into versioned JSON; `symptom_kb`
  and red-flag rules build from it via `keywordsRef`/`allOfAny`. Pure refactor; #36 invariant test
  retained (now structural). No behavior change.
- **B — Extractor seam (mock only).** Add `ISymptomExtractor` + `KeywordSymptomExtractor` +
  `symptom_extractor` tool; refactor `symptom_kb` to a pure `presentIds` scorer; planner does
  extractor→kb two-step passing IDs as args; disabled-tool matrix. Deterministic, green. No LLM.
- **C — Present/absent + negation.** Add present/absent to the keyword extractor, fix the scoring-path
  negation bug with fixtures; add the intentional-guardrail-false-positive test (§7).
- **D — Anthropic extractor.** `AnthropicSymptomExtractor` behind `llmProvider=anthropic`: closed-set
  prompt injects the taxonomy, owns timeout, internal fallback (`fallback:true`, non-degraded);
  `ScriptedSymptomExtractor` unit tests; live-API tests opt-in via env.
- **E — Audit log.** `IAuditSink` + `JsonlAuditSink`, config + concurrency + PII gate; record per §8.
