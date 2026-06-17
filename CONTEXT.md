# Mini Agent Runtime

A public, runnable agent-runtime library (and a health symptom-triage sample agent built on it) that mirrors, in miniature, the agent-orchestration architecture concepts from production work. The runtime is domain-agnostic; the health domain lives on top of it.

## Language

**Planner**:
The component that decides the agent's next step — call a tool, ask the user, or finish — by reading the current `WorkContext`. It only decides; it never executes the step itself (the orchestrator does). The mock (offline, plain C#) and the Anthropic client are interchangeable implementations.
_Avoid_: Brain, AI, Model, Decider

**State-dependent (of a Planner)**:
A planner whose next-step decision is computed from the current conversation and tool observations already in `WorkContext`, so the loop can take different paths and revisit choices — as opposed to a fixed, hardcoded sequence of steps. The default mock planner is required to be state-dependent (it is still plain keyless C#, not an LLM).
_Avoid_: Scripted, fixed-sequence
