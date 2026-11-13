# Project Directive: valheim-rift-synth (BYO-LLM M+ Dungeon Synthesizer Test Bench)

## 0. Role, Environment & Non-Negotiable Constraints
You are building a two-part Bring-Your-Own-LLM Mythic+ Dungeon Synthesizer Test Bench for Valheim 1.0.21+.
- Local Dev Host (ZedBook-Max2): macOS arm64 (M2 Max), .NET SDK, Python 3.12+ managed strictly via uv, SSH key auth configured to BabBox.
- Target Sandbox Host (BabBox): Headless Linux x86_64 dedicated Valheim 1.0 server (WorldVersion: 41+ chunked world format) running BepInExPack_Valheim 5.4.2351+. Bind sandbox server to ports 2458-2459 with -world RiftSandbox if an active server already occupies 2456.
- Scope Boundary: NO CarrierPigeons / database telemetry integration in v0.1. Input is a pure natural-language CLI prompt; output is a validated JSON Rift Manifest hot-reloaded by the Valheim dedicated server.
- Fail Fast & Zero Bloat: Deliver complete, production-ready C# and Python files. Never output truncated "// ... rest of code" placeholders. Do not depend on Jotunn or external asset bundles for v0.1—use pure BepInEx + HarmonyX + publicized vanilla Valheim server assemblies so client mod requirements are zero (100% server-authoritative ZDOs).

---

## 1. Repository Layout (kebab-case paths, snake_case Python)
Initialize the workspace with the following exact structure:

valheim-rift-synth/
├── GEMINI.md
├── Makefile                          # sync-libs, build-mod, deploy-mod, synth-rift
├── schemas/
│   └── rift_manifest.schema.json     # Single source of truth contract
├── tools/
│   └── rift_synth/
│       ├── pyproject.toml            # uv project: openai, google-genai, pydantic, jsonschema
│       ├── room_catalog.py           # Curated vanilla prefab & socket registry
│       ├── validator.py              # Deterministic "Potato-PC" & bounds linter
│       └── cli.py                    # Prompt -> BYO-LLM -> Lint -> SCP to BabBox
└── src/
    └── ValheimRiftEngine/
        ├── ValheimRiftEngine.csproj  # net48 / netstandard2.1 + BepInEx.AssemblyPublicizer.MSBuild
        ├── Plugin.cs                 # Entrypoint, Config, FileSystemWatcher hot-reload
        ├── Models/
        │   └── RiftManifest.cs       # Pure C# DTOs + lightweight JSON parser
        ├── Generation/
        │   ├── SkyboxArenaBuilder.cs # Spawns topological node rooms + corridors at Y=4500f
        │   └── InstanceCleaner.cs    # Deterministic ZDO teardown on completion/abort
        ├── Runtime/
        │   ├── RiftDirector.cs       # Server-side state machine, wave gates, keystone timer
        │   └── AffixController.cs    # Server-authoritative M+ mob/player affixes
        └── Patches/
            ├── ChatCommandPatch.cs   # Intercepts /rift [start|abort|status|reload|kit] via ZRoutedRpc
            └── CombatHooksPatch.cs   # Tracks rift mob deaths, enforces affix damage/AOEs

---

## 2. Architectural Guardrails (CRITICAL — Read Before Writing Code)

1. Server-Side ZDO Authority & Potato-PC Protection:
   - Every prefab spawned by the Rift Engine MUST be instantiated on the server (ZNet.instance.IsServer()) via UnityEngine.Object.Instantiate(ZNetScene.instance.GetPrefab(name), pos, rot).
   - Single-Zone Constraint: The entire Rift layout MUST fit inside a single 60m x 60m x 40m bounding box centered on the current active Rift origin at Y = 4500f (aligned to a single Valheim 64x64m zone center: zoneX * 64f, 4500f, zoneZ * 64f). This guarantees older client PCs never cross a zone boundary mid-pull and never trigger Unity Resources.UnloadUnusedAssets() GC stutters.
   - Active AI Cap: Never allow more than 12 active AI creatures simultaneously. Rooms with higher mob budgets MUST stagger spawns across sequential waves (wave_index: 0, 1, 2).
   - Zero Custom Assets: Build arenas strictly out of indestructible-flagged vanilla structural prefabs (blackmarble_floor_large [4x4m], blackmarble_wall_4x2, blackmarble_column_2, dvergrprops_torch, iron_grate wave gates, piece_chest_wood reward chest) by setting WearNTear.m_noRoofWear = true and ZDO health to 1e9f (infinite/indestructible health) so AoE slams cannot drop players out of the skybox.

2. Grid-Deterministic Topological Layout (No Raw LLM Float Hallucinations):
   - Do NOT let the LLM emit raw x, y, z float coordinates for walls or floors.
   - Instead, the LLM emits an ordered graph of 3 to 4 RoomSpec nodes connected North-to-South (+Z) inside the 60m x 60m zone budget using discrete templates constructed procedurally by SkyboxArenaBuilder.cs:
     * entry_vestibule (8m x 8m safe staging box with countdown brazier)
     * crucible_square (16m x 16m open arena, optional pillar_layout: none | four_corners | center_los_blocker)
     * gauntlet_bridge (8m wide x 16m long bridged chasm with side hazards)
     * sanctum_oct (20m x 20m boss chamber)
   - Each template exposes deterministic relative spawn anchors (center, north_left, north_right, south_left, south_right, sniper_perch_ne, sniper_perch_nw). The LLM assigns vanilla creature prefab IDs, star levels (0-2, mapped to Character.SetLevel(stars + 1)), and affixes to those named anchors.

3. BYO-LLM Provider Agnosticism (tools/rift_synth):
   - Support an OpenAI-compatible chat completions endpoint (RIFT_LLM_BASE_URL, RIFT_LLM_API_KEY, RIFT_LLM_MODEL) AND native google-genai (GEMINI_API_KEY, default model gemini-2.5-pro or user-configured Carbon endpoint) via a clean Python Protocol (SynthesizerBackend).
   - Use structured JSON Schema output enforced against schemas/rift_manifest.schema.json.
   - Include a retry-with-linter-feedback loop (max 2 retries): if validator.py rejects an invalid prefab name, over-cap mob wave, or total length > 58m, feed the exact validation error back to the LLM to self-correct before deploying to BabBox.

---

## 3. Core Data Contract (schemas/rift_manifest.schema.json)

Implement this exact JSON contract shared by Python (Pydantic) and C# (RiftManifest):
- rift_id (string, kebab-case slug)
- title (string, displayed via MessageHud.MessageType.Center on entry)
- keystone_level (int, 1-15; scales HP multiplier 1.0 + 0.18 * level and damage 1.0 + 0.10 * level)
- par_time_seconds (int, 180-900)
- global_affixes (array of 1-3 enums):
  * molten_slag: Every 6s in combat, spawns a telegraphing vfx_surtling_death warning under stationary players followed 2s later by an AoE fire tick.
  * bolstering: Dying non-boss Rift mobs buff nearby allies inside 8m (+15% scale & damage).
  * thunder_blessing (Positive Seasonal Kiss Affix): Players exceeding 50% stamina trigger a chain-lightning AoE (fx_lightning_hit) every 10s in melee range.
  * shielded_pack: One elite per wave gains temporary damage immunity until adds die.
- rooms (array of 3-4 RoomSpec objects):
  * room_index (int)
  * template (entry_vestibule | crucible_square | gauntlet_bridge | sanctum_oct)
  * pillar_layout (none | four_corners | center_los_blocker)
  * waves (array of WaveSpec, max 3 waves per room, hard lint limit: sum of count per wave <= 10):
    - wave_index (int)
    - spawns: array of { "prefab": "<validated_vanilla_id>", "anchor": "<anchor_id>", "count": 1-4, "stars": 0-2, "is_boss": bool }
- rewards:
  * base_drops: array of { "prefab": "<vanilla_item_id>", "amount": int, "quality": 1-4 }
  * timed_bonus_drops: extra chest loot injected if elapsed_seconds <= par_time_seconds (+1 / +2 / +3 Keystone chest logic).

---

## 4. Step-by-Step Execution Milestones

Execute in this exact order, verifying compilation at each stage:

### Milestone 1: Workspace Bootstrap & BabBox Assembly Sync
1. Inspect SSH connectivity to BabBox (check ~/.ssh/config for host alias, Valheim dedicated server install path, and BepInEx path). Ask the user ONLY if the remote Valheim server path cannot be auto-discovered via SSH.
2. Create Makefile target `make sync-libs` that rsyncs valheim_server_Data/Managed/{assembly_valheim.dll,assembly_utils.dll,assembly_guiutils.dll,UnityEngine*.dll} and BepInEx/core/{BepInEx.dll,0Harmony.dll} from BabBox into .lib/ (gitignored).
3. Configure ValheimRiftEngine.csproj using BepInEx.AssemblyPublicizer.MSBuild (0.4.2+) against .lib/assembly_valheim.dll so private fields (ZNetScene.m_namedPrefabs, Character.m_nview, WearNTear, MonsterAI) compile cleanly without reflection boilerplate. Verify `dotnet build` succeeds with 0 warnings.

### Milestone 2: Server-Side Skybox Rift Engine (ValheimRiftEngine.dll)
1. Implement SkyboxArenaBuilder.cs: builds watertight, indestructible blackmarble rooms + connecting hallways + locked iron_grate doors between rooms inside a single 64m zone at Y = 4500f, tracking every spawned ZDOID in a HashSet<ZDOID> for 100% leak-free teardown in InstanceCleaner.cs.
2. Implement RiftDirector.cs & AffixController.cs:
   - Records each participating player's pre-Rift world coordinates, teleports all nearby players (within 20m of command caller) into entry_vestibule, runs a 5-second center-HUD countdown, drops Gate 0, and starts the Keystone clock.
   - Monitors active Rift creature ZDOIDs on a lightweight 0.5s server coroutine (NO per-frame Update() scans). When a wave clears, spawns the next wave; when all waves in a room clear, destroys that room's exit iron_grate with a dramatic lightning VFX (fx_lightning_hit).
   - On final boss kill, calculates Par Time vs Elapsed (+1 / +2 / +3 Keystone upgrade), spawns the reward chest + a return portal (or `/rift leave` teleport back to saved origin), and broadcasts the final scorecard to chat/HUD.
3. Implement FileSystemWatcher hot-reloading on BepInEx/config/ValheimRiftEngine/active_rift.json + server chat commands (`/rift status`, `/rift start`, `/rift abort`, `/rift leave`, and admin-gated `/rift kit dwarf` which grants maxed Carapace armor, Battleaxe/Shield, and endgame food for rapid sandbox testing).

### Milestone 3: Python BYO-LLM Synthesizer CLI (tools/rift_synth)
1. Initialize tools/rift_synth with uv. Populate room_catalog.py with ~35 battle-tested vanilla creature IDs across biomes (Greydwarf_Elite, Skeleton_Poison, Draugr_Elite, BlobElite, Troll, Wolf, Fenring, Fenring_Cultist, Goblin, GoblinBrute, GoblinShaman, Growth, Surtling, Dverger, DvergerMageFire, DvergerMageIce, Seeker, SeekerBrute, Charred_Melee, Charred_Mage, Morgen) and curated reward item IDs (Coins, Thunderstone, Bloodbag, OozeBomb, SilverSword, BlackMetalAtgeir, etc.).
2. Implement validator.py enforcing: valid creature/item prefabs, max 10 mobs per wave, max 4 rooms, total Z-span <= 58m, at least 1 boss in the final room, and presence of 1 positive blessing affix alongside negative affixes.
3. Implement cli.py so running:
   `uv run rift-synth "Keystone 5 Dwarven Forge Run: heavy fire mages and armored brutes, punish doorway camping, reward aggressive melee"`
   synthesizes the JSON, validates it, prints an ASCII topology preview in the terminal, and SCPs active_rift.json directly to BabBox's BepInEx/config/ValheimRiftEngine/active_rift.json.

### Milestone 4: Live Smoke Test on BabBox
1. Run `make build-mod && make deploy-mod` to push ValheimRiftEngine.dll to BabBox and restart the sandbox Valheim service, verifying `[Info   :ValheimRiftEngine] Loaded v0.1.0` in BepInEx/LogOutput.log.
2. Execute one end-to-end `uv run rift-synth` run from ZedBook-Max2 and tail BabBox's LogOutput.log to confirm the hot-reload watcher parses and validates active_rift.json without restarting the Valheim server.
