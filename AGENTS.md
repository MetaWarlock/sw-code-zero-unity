# Darkwing Duck Unity port

## Scope

Follow the current human request. If the current task is research, planning or writing instructions, do only that; do not install software, implement the port or run experiments on that basis.

When explicitly assigned implementation of the port, read these documents before changing game code:

1. `Docs/DarkwingPort/IMPLEMENTATION_BRIEF_RU.md` — scope, architecture, milestones and acceptance criteria.
2. `Docs/DarkwingPort/RESEARCH_HANDOFF_RU.md` — existing findings, artifacts, addresses, uncertainties and sources.
3. `Research/DarkwingDuck/STATUS.md`, `FACTS.md`, `GAPS.md`, `COVERAGE.csv` when present.

These are explicit reading instructions; do not assume ZCode automatically imports linked files.

## Implementation rules

- Target the existing project at `D:\Art\SW Code Zero Unity`; specify this project path in Unity commands because another Editor may be running.
- Preserve `Art/Darkwing Duck (USA).nes` unchanged. Use its documented SHA-256 to identify the source revision.
- Deliver native, editable C# gameplay in Unity. An emulator wrapper or ROM execution inside Unity does not complete the task. A dedicated APU synthesizer is allowed for sound.
- Preserve original behavior and content. Do not invent constants, skip unknown object IDs, replace bosses with generic behavior, or silently simplify mechanics.
- Distinguish confirmed facts, source hints and hypotheses. Record evidence and bank context for addresses.
- Use deterministic original-ROM comparisons. Do not replace original goldens with output from the implementation or loosen acceptance criteria to make failing comparisons pass.
- Reuse existing working tools and artifacts. Install or change infrastructure only for a concrete need. Avoid repeated broad searches, full log dumps and unrelated refactors.
- Work through small dependency-ordered milestones. Update the four progress files with findings, remaining gaps and the next concrete action.
- Scene/prefab/settings changes in a connected Unity Editor should use Editor APIs/available Unity CLI commands. Do not hand-edit live serialized YAML.
- Keep research tools and emulator dependencies out of the final game runtime. Document required build dependencies and asset provenance.
- Full completion requires all seven missions, all bosses, secrets/bonus areas, UI/progression/final sequence, audio, acceptance evidence and a verified standalone build. A prototype or successful compilation is insufficient.
- Keep work local unless the user separately requests publishing or remote repository changes.

Use Russian for user-facing progress and handoff notes. Keep reports short and evidence-based.
