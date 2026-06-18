06/18
Build a public .Net/C# agent-runtime portfolio repo via TDD slices.

After Slice 6:
What to look for in the card

  ┌─ Triage ─────────────────────────────
  │ Urgency:     SeeGp           ← changes with score
  │ Action:      ...             ← changes with urgency band
  │ Tools used:  symptom_kb      ← proves the tool actually ran
  │ Educational only — not medical advice.
  └──────────────────────────────────────

The symptom keywords the KB knows: sore throat, fever, headache, cough, dizziness, abdominal pain, chest pain/tightness, shortness of breath/trouble breathing. Words outside that list score 0.


GREEN — 31 tests, and slice 10's config-driven --flight demo works live.

Verify it yourself — the payoff is watching the same input change outcome with no recompile:
dotnet run --project src/CareTriageAgent.Cli -- "headache"                          # SelfCare
dotnet run --project src/CareTriageAgent.Cli -- --flight strict-thresholds "headache"   # SeeGp
dotnet run --project src/CareTriageAgent.Cli -- --flight disable-symptom-kb "sore throat"  # Tools used: (none)
dotnet run --project src/CareTriageAgent.Cli -- --flight bogus "headache"            # rejected (allow-list)
dotnet test                                                                          # 31 passing
You can also edit src/CareTriageAgent.Cli/config/runtimeconfig.json or the flight files and re-run — behavior changes with zero code edits (the config files are copied to the build output).

What's in this slice: RuntimeConfigProvider.Resolve<T> (typed config), CareTriageConfig, shipped config/runtimeconfig.json + config/flights/*.json, and the CLI now builds thresholds, retry count, and enabled tools entirely from the effective config — plus --flight.

