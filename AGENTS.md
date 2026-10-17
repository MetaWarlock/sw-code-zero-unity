# Darkwing Duck Unity port

## Scope

Follow the current human request. If the current task is research, planning or writing instructions, do only that; do not install software, implement the port or run experiments on that basis.

When explicitly assigned implementation of the port, read these documents before changing game code:

1. `Docs/DarkwingPort/IMPLEMENTATION_BRIEF_RU.md` — scope, architecture, milestones and acceptance criteria.
2. `Docs/DarkwingPort/RESEARCH_HANDOFF_RU.md` — existing findings, artifacts, addresses, uncertainties and sources.
3. `Research/DarkwingDuck/STATUS.md`, `FACTS.md`, `GAPS.md`, `COVERAGE.csv` when present.

These are explicit reading instructions; do not assume ZCode automatically imports linked files.

## Available research tools

- REA `rea-agents@6.5.0` is installed globally (`rea` CLI); its MCP server is registered in Qoder. Use it when useful for focused original-code analysis: 6502 disassembly, pseudocode, calls and references.
- Before first use, read `C:\Users\Warlock\.qoder\skills\reverse-engineer-anything\SKILL.md`; consult only relevant supporting references.
- Ghidra 12.1.4: `D:\Tools\ghidra_12.1.4_PUBLIC`; JDK 21: `D:\Tools\jdk-21.0.12.1+1`. If the current process lacks the configured environment, pass these paths as `GHIDRA_INSTALL_DIR` and `JAVA_HOME`, with `REA_ANALYSIS_PROVIDER=ghidra`.
- If REA MCP tools are absent from the current session, use the working CLI and continue. Reuse the installed toolchain; diagnose specific failures instead of repeating setup or broad readiness checks.
- Raw 6502 analysis does not model iNES/MMC1 bank switching. Preserve bank identity and load address. Distinguish memory addresses from file offsets: with a bank loaded at `0x8000`, procedure `0x8F00` is file offset `0x0F00`. Use explicit `--file-offset` for file coordinates; `REA_GHIDRA_SEED_FILE` takes loaded-memory addresses.
- Keep Mesen and existing capture/comparison tools as the behavioral reference. REA pseudocode is analysis evidence, not proof of runtime equivalence. Save and reuse analysis results; avoid restarting a full ROM investigation.

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
