# Darkwing Duck Unity port

## Scope

Follow the current human request. If the current task is research, planning or writing instructions, do only that; do not install software, implement the port or run experiments on that basis.

When explicitly assigned implementation of the port, read these documents before changing game code:

1. `Docs/DarkwingPort/IMPLEMENTATION_BRIEF_RU.md` — scope, architecture, milestones and acceptance criteria.
2. `Docs/DarkwingPort/RESEARCH_HANDOFF_RU.md` — existing findings, artifacts, addresses, uncertainties and sources.
3. `Research/DarkwingDuck/STATUS.md`, `FACTS.md`, `GAPS.md`, `COVERAGE.csv` when present.

These are explicit reading instructions; do not assume ZCode automatically imports linked files.

## Product intent and reusable knowledge

- The intended foundation is a complete, understandable and controllable native game system with the original's verified quality, whose elements can later be replaced incrementally without breaking unrelated behavior. Preserve the accurate reference baseline and explicit subsystem contracts. This intent does not authorize a new refactor, a remake or relaxed acceptance criteria during the current port.
- `Docs/GameReconstruction/KNOWLEDGE_RU.md` is the single portable knowledge base for lessons across game reconstruction projects. Before choosing a research/validation approach, read its index and relevant lessons. Current human instructions and this project's acceptance rules take precedence; do not assume NES findings apply to DOS or another game.
- Before handing off a meaningful milestone, completing a failure investigation, or adopting/changing a reusable method or workflow, update the relevant lesson when new knowledge was learned. Include scope, evidence, confidence/status, the mistake to avoid and the repeatable action. Revise or retract disproven lessons instead of appending contradictory advice. If no reusable knowledge changed, do not add a ceremonial entry.
- Search the existing lessons before adding one. Keep this base compact: no per-agent reports, shift diaries, copied traces or duplicate Darkwing specifications. Game-specific addresses/data and acceptance evidence stay in FACTS/GAPS/COVERAGE; link to them. Preserve reproducible procedures and small useful examples, not session history. A tool reinstall or new infrastructure requires a concrete task need, not a knowledge-base entry.
- The base is versioned with this project and can be carried into the next project explicitly; it is not automatically loaded by other chats/projects. Extract a shareable skill only after its workflow is demonstrated on another target and explicitly requested. Do not include ROMs, extracted proprietary assets, credentials or machine-specific setup assumptions in reusable instructions.

## Human collaboration

- The user is an available teammate and manual playtester. Actively consider human help when planning research and validation: reaching a location, recording original-game input, trying a mechanic, reproducing a bug, or comparing the feel of the port. Ask when a short human action can replace substantial automated investigation.
- Human checks are required at the gates below, not optional at the agent's discretion. Request focused help earlier when it can replace substantial investigation. Continue independent useful work while waiting; do not repeatedly ask or assume the user is available immediately.
- Make each request actionable: specify the game/build and location, the actions to try, what to observe or record, and which uncertainty the result resolves. Prepare the existing recording/test tools when needed. Prefer the smallest useful test; request a full new playthrough only when necessary. Ordinary human play, mistakes and deaths are acceptable; frame-perfect input is not required.
- A successful recorded route covers only the mechanics and branches it exercises. Picking up a weapon does not test firing it. Track important untested actions in the existing coverage records and proactively propose targeted human checks for weapons, abilities, alternate paths and other gaps before claiming readiness.
- Treat human bug reports as evidence to investigate even when automated comparisons pass. Preserve the report and reproduction details concisely in existing progress/coverage files, then reproduce and add focused regression coverage where useful. Human observations complement deterministic original-ROM evidence; neither an untested explanation nor manual success establishes full equivalence.

## Mandatory verification gates

### Reference recordings and coverage

- Before using a recording as acceptance evidence, verify ROM identity, input identity/length, start state, frame numbering and capture completeness. Replay the original twice independently and compare the actual captured bytes; state every excluded power-on window explicitly. Reject unexplained nondeterminism, missing frames or state-load/rewind contamination in a continuous playthrough. Deaths, retries and imperfect human play are valid input.
- Deterministic replay proves repeatability, not completeness or correctness of the recorder/comparator. Check visible checkpoints and the claimed ending; confirm the captured buttons drive the intended actions. Verify new or modified comparison tools reject missing/extra data and deliberate relevant mismatches before trusting a PASS. When human observation contradicts a PASS, investigate the harness and its coverage as well as the game.
- For mission acceptance require a complete original route through the real boss and return/progression, plus a second independently recorded input sequence with different actions. The second may be a focused original-game recording covering alternate actions; it must not be another replay of the same input. Request it from the user when absent. Add focused recordings until required mechanics are covered; two recordings alone do not prove coverage. Original-game bugs must be identified as reference behavior, not silently corrected or used to waive checks.
- Keep the coverage matrix in the existing COVERAGE.csv: mission, mechanic/action, reference evidence, port comparison, human result and tested revision/environment. Record missing coverage explicitly. Enumerate mechanics from original data/research, not only from the actions already recorded. Cover every available weapon's pickup, Select selection, firing both ways, projectile appearance/direction/hits/ammo; movement/jumps/hooks, enemy contact/damage/death/retry, pause, camera/doors/transitions, boss/progression, and applicable secrets/bonus paths. A pickup or an idle boss fixture does not cover use or combat.

### Checks required by event

| Event | Mandatory checks before claiming success |
|---|---|
| Gameplay/rendering/audio fix | Reproduce the reported failure on an identified version, compare with the original, then run a focused regression for the changed behavior and the existing affected checks. Record appearance, collision and sound separately where relevant. |
| Changes to shared input, timing, flow, player physics, collision, weapons, spawning, rendering/scroll/CHR/NT/OAM, audio or reset/save state | Run every available previously completed mission input on the new Core, plus affected focused tests and Suite/contracts against the recorded baseline. Check rendered output for rendering changes; state equality alone is insufficient. Request a short human regression of mission 1 and any other affected previously completed missions on the current interactive version. Earlier missions remain in the regression set throughout development. |
| Delivery of a user-visible implementation milestone or a handoff containing such changes | Prepare and identify a current playable version and request a human test with a concrete checklist, including the affected section and an earlier mission's start. Verify the actual Unity scene path; build and smoke-test a fresh standalone when delivering a standalone or claiming its readiness. A Core-only replay does not certify either interactive path. |
| First acceptance of a mission | All reference/coverage requirements above, automated replay of the available independent inputs, and a human full playthrough in the port through the real boss and progression. Also verify a fresh standalone. Missing weapon, visual, audio, secret/bonus or other required coverage keeps full acceptance open; a passing route may be reported separately. |
| Stagnation: two consecutive attempted fixes/hypothesis tests without measurable progress on the same blocker, or 60 minutes of active investigation without such progress, whichever comes first | Report the exact blocker, attempts and evidence, and request a specific human action or observation if it could resolve uncertainty. If human play cannot answer the technical question, explicitly explain why and request independent technical review instead. Do not start another speculative cycle before making this escalation; continue independent useful tasks while awaiting a response. |
| Human reports a blocker or contradicts automated PASS | Record the tested version/environment and reproduction, reopen the affected acceptance claim, and reproduce or request only the missing evidence. Treat an early visible corruption, invisible obstacle or broken essential action as a blocker before extending work that relies on that section. Do not dismiss it because an old route passes. |

### Evidence and pending checks

- For every gate record PASS / FAIL / NOT RUN / AWAITING HUMAN in the existing progress/coverage files, with commit plus relevant uncommitted changes, Editor or standalone/build identity, test/input and result. Prior manual success belongs to that tested version; after triggering changes its affected checks become pending again. Do not invent manual results or treat silence as acceptance.
- Research-only checkpoints do not require a full playthrough, but still trigger stagnation escalation. A commit is not automatically a milestone. Known pre-existing failures remain visible with their baseline; do not weaken checks to retain their score, and do not call a partially failing mission fully accepted.
- Request the human test as soon as the playable checkpoint is prepared, before declaring the stage accepted. While awaiting it, preserve the tested revision and continue independent work; a new revision needs relevant checks again. Handoffs must name pending gates. Human absence permits an explicitly unaccepted checkpoint, not automatic acceptance or indefinite guessing. Only an explicit human instruction can defer or waive a required check; record the scope of that exception.
- Apply these gates to current work too: historical route PASS remains evidence for that route, but does not grandfather a mission into full acceptance. Audit only missing gate evidence, not the whole project again. Reuse recordings/tools and existing records; do not create new report trees or require full human replays after every small commit.

## Available research tools

- REA `rea-agents@6.5.0` is installed globally (`rea` CLI); its MCP server (with the Ghidra environment below) is registered in Qoder and Claude Code (user scope, `~/.claude.json`). Use it when useful for focused original-code analysis: 6502 disassembly, pseudocode, calls and references.
- Before first use, read the REA skill: `C:\Users\Warlock\.qoder\skills\reverse-engineer-anything\SKILL.md` (Qoder) or `C:\Users\Warlock\.claude\skills\reverse-engineer-anything\SKILL.md` (Claude Code; same version); consult only relevant supporting references.
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
- Before preparing a test stand or creating any build, launch, replay, capture or comparison helper, find and read the existing tools and their invocation examples. Reuse `Tools/DarkwingResearch/build_player.py`, `run_check.py`, `verify_player.ps1`, `stand/stand.py`, the existing `stand/` launchers and relevant previous probes where applicable. This requirement also applies to temporary scripts in `Temp/`.
- Extend an existing tool minimally when a capability is missing. Create a new helper only when a concrete required capability cannot reasonably be provided by the existing tools; record the gap and why reuse or extension is unsuitable in the existing progress record before creating it. Convenience or a new work session alone is not a reason to duplicate a script.
- Update the existing `Build/UserStand` and its shortcuts in place. Do not create another stand folder, duplicate launchers or Desktop shortcuts unless the user asks for them. Check the actual shortcut targets and identify the build supplied to the user.
- Work through small dependency-ordered milestones. Update the four progress files with findings, remaining gaps and the next concrete action.
- Scene/prefab/settings changes in a connected Unity Editor should use Editor APIs/available Unity CLI commands. Do not hand-edit live serialized YAML.
- Keep research tools and emulator dependencies out of the final game runtime. Document required build dependencies and asset provenance.
- Full completion requires all seven missions, all bosses, secrets/bonus areas, UI/progression/final sequence, audio, acceptance evidence and a verified standalone build. A prototype or successful compilation is insufficient.
- Keep work local unless the user separately requests publishing or remote repository changes.

Use Russian for user-facing progress and handoff notes. Keep reports short and evidence-based.
