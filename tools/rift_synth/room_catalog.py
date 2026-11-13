"""Curated vanilla Valheim 1.0.21+ creature, reward item, room template, and socket registry."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Final


@dataclass(frozen=True)
class CreatureEntry:
    prefab: str
    biome: str
    role: str
    boss_viable: bool = False


@dataclass(frozen=True)
class RewardItemEntry:
    prefab: str
    category: str
    max_stack: int
    max_quality: int


@dataclass(frozen=True)
class RoomTemplateSpec:
    template: str
    width_x: float
    length_z: float
    description: str


CORRIDOR_LENGTH_M: Final[float] = 4.0
MAX_TOTAL_Z_SPAN_M: Final[float] = 58.0
MAX_MOBS_PER_WAVE: Final[int] = 10
MAX_WAVES_PER_ROOM: Final[int] = 3
MIN_ROOMS: Final[int] = 3
MAX_ROOMS: Final[int] = 4

ROOM_TEMPLATES: Final[dict[str, RoomTemplateSpec]] = {
    "entry_vestibule": RoomTemplateSpec(
        template="entry_vestibule",
        width_x=8.0,
        length_z=8.0,
        description="8m x 8m safe staging box with countdown brazier (MUST be room_index=0 with waves=[]).",
    ),
    "crucible_square": RoomTemplateSpec(
        template="crucible_square",
        width_x=16.0,
        length_z=16.0,
        description="16m x 16m open melee/caster brawl arena with elevated NE/NW sniper perches.",
    ),
    "gauntlet_bridge": RoomTemplateSpec(
        template="gauntlet_bridge",
        width_x=8.0,
        length_z=16.0,
        description="8m wide x 16m long bridged chasm with recessed side lantern hazards and sniper perches.",
    ),
    "sanctum_oct": RoomTemplateSpec(
        template="sanctum_oct",
        width_x=20.0,
        length_z=20.0,
        description="20m x 20m octagonal vaulted boss chamber (10m ceiling) with reward dais.",
    ),
}

PILLAR_LAYOUTS: Final[tuple[str, ...]] = (
    "none",
    "four_corners",
    "center_los_blocker",
)

SPAWN_ANCHORS: Final[tuple[str, ...]] = (
    "center",
    "north_left",
    "north_right",
    "south_left",
    "south_right",
    "sniper_perch_ne",
    "sniper_perch_nw",
)

NEGATIVE_AFFIXES: Final[dict[str, str]] = {
    "molten_slag": "Every 6s in combat, stationary players get a 2s fiery telegraph warning followed by an AoE fire burst (punishes doorway camping).",
    "bolstering": "Dying non-boss Rift mobs buff allies within 8m (+15% scale & outgoing damage).",
    "shielded_pack": "One elite per multi-mob wave is immune to damage until all adds in the wave die.",
}

POSITIVE_BLESSING_AFFIXES: Final[dict[str, str]] = {
    "thunder_blessing": "Positive Seasonal Kiss Affix: Players above 50% stamina trigger a chain-lightning AoE every 10s in melee range.",
}

ALL_GLOBAL_AFFIXES: Final[dict[str, str]] = {
    **NEGATIVE_AFFIXES,
    **POSITIVE_BLESSING_AFFIXES,
}

# 36 battle-tested vanilla Valheim 1.0.21 creature prefabs across Black Forest -> Ashlands
CREATURE_CATALOG: Final[dict[str, CreatureEntry]] = {
    entry.prefab: entry
    for entry in (
        # Black Forest
        CreatureEntry("Greydwarf_Elite", "BlackForest", "melee_bruiser"),
        CreatureEntry("Greydwarf_Shaman", "BlackForest", "healer_poison"),
        CreatureEntry("Skeleton_Poison", "BlackForest", "melee_poison_elite", boss_viable=True),
        CreatureEntry("Skeleton_NoArcher", "BlackForest", "melee_fodder"),
        CreatureEntry("Ghost", "BlackForest", "wraith_skirmisher"),
        CreatureEntry("Troll", "BlackForest", "heavy_giant", boss_viable=True),
        # Swamp
        CreatureEntry("Draugr_Elite", "Swamp", "heavy_melee_bruiser", boss_viable=True),
        CreatureEntry("Draugr_Ranged", "Swamp", "ranged_sniper"),
        CreatureEntry("BlobElite", "Swamp", "aoe_poison_ooze"),
        CreatureEntry("Wraith", "Swamp", "flying_ambusher"),
        CreatureEntry("Abomination", "Swamp", "colossal_tank", boss_viable=True),
        CreatureEntry("Surtling", "Swamp", "ranged_fire_turret"),
        # Mountain
        CreatureEntry("Wolf", "Mountain", "fast_pack_rusher"),
        CreatureEntry("Ulv", "Mountain", "cave_leap_rusher"),
        CreatureEntry("Fenring", "Mountain", "agile_stalker", boss_viable=True),
        CreatureEntry("Fenring_Cultist", "Mountain", "flame_caster_elite", boss_viable=True),
        CreatureEntry("StoneGolem", "Mountain", "armored_colossus", boss_viable=True),
        CreatureEntry("Hatchling", "Mountain", "flying_frost_sniper"),
        # Plains
        CreatureEntry("Goblin", "Plains", "melee_skirmisher"),
        CreatureEntry("GoblinArcher", "Plains", "ranged_sniper"),
        CreatureEntry("GoblinBrute", "Plains", "armored_berserker", boss_viable=True),
        CreatureEntry("GoblinShaman", "Plains", "fireball_shield_caster", boss_viable=True),
        CreatureEntry("BlobTar", "Plains", "tar_slow_spitter"),
        CreatureEntry("Lox", "Plains", "heavy_stampede_beast", boss_viable=True),
        # Mistlands
        CreatureEntry("Dverger", "Mistlands", "crossbow_rogue"),
        CreatureEntry("DvergerMageFire", "Mistlands", "heavy_fire_mage", boss_viable=True),
        CreatureEntry("DvergerMageIce", "Mistlands", "frost_nova_mage", boss_viable=True),
        CreatureEntry("DvergerMageSupport", "Mistlands", "ward_healer_mage"),
        CreatureEntry("Seeker", "Mistlands", "flying_chitin_bruiser"),
        CreatureEntry("SeekerBrood", "Mistlands", "swarm_add"),
        CreatureEntry("SeekerBrute", "Mistlands", "armored_soldier_boss", boss_viable=True),
        # Ashlands
        CreatureEntry("Charred_Twitcher", "Ashlands", "skirmish_rusher"),
        CreatureEntry("Charred_Archer", "Ashlands", "volley_sniper"),
        CreatureEntry("Charred_Melee", "Ashlands", "greatsword_warrior", boss_viable=True),
        CreatureEntry("Charred_Mage", "Ashlands", "hellfire_warlock", boss_viable=True),
        CreatureEntry("Morgen", "Ashlands", "apex_bone_horror", boss_viable=True),
    )
}

# 26 curated vanilla Valheim 1.0.21 reward items (verified 100% against live ZNetScene)
REWARD_ITEM_CATALOG: Final[dict[str, RewardItemEntry]] = {
    entry.prefab: entry
    for entry in (
        # Treasure & Currencies
        RewardItemEntry("Coins", "currency", max_stack=999, max_quality=1),
        RewardItemEntry("Ruby", "treasure", max_stack=20, max_quality=1),
        RewardItemEntry("AmberPearl", "treasure", max_stack=50, max_quality=1),
        RewardItemEntry("Thunderstone", "artifact", max_stack=20, max_quality=1),
        # Combat Consumables & Alchemy
        RewardItemEntry("Bloodbag", "material", max_stack=50, max_quality=1),
        RewardItemEntry("BombOoze", "throwable", max_stack=40, max_quality=1),
        RewardItemEntry("Bilebag", "material", max_stack=50, max_quality=1),
        RewardItemEntry("MeadHealthMajor", "consumable", max_stack=10, max_quality=1),
        RewardItemEntry("MeadStaminaMedium", "consumable", max_stack=10, max_quality=1),
        RewardItemEntry("MeadEitrMinor", "consumable", max_stack=10, max_quality=1),
        RewardItemEntry("MisthareSupreme", "food", max_stack=10, max_quality=1),
        RewardItemEntry("MeatPlatter", "food", max_stack=10, max_quality=1),
        # Crafting Metals & Essences
        RewardItemEntry("Iron", "metal", max_stack=30, max_quality=1),
        RewardItemEntry("Silver", "metal", max_stack=30, max_quality=1),
        RewardItemEntry("BlackMetal", "metal", max_stack=30, max_quality=1),
        RewardItemEntry("Carapace", "material", max_stack=50, max_quality=1),
        RewardItemEntry("Eitr", "essence", max_stack=30, max_quality=1),
        RewardItemEntry("CelestialFeather", "essence", max_stack=50, max_quality=1),
        # Weapons & Staves
        RewardItemEntry("SwordSilver", "weapon", max_stack=1, max_quality=4),
        RewardItemEntry("AtgeirBlackmetal", "weapon", max_stack=1, max_quality=4),
        RewardItemEntry("Battleaxe", "weapon", max_stack=1, max_quality=4),
        RewardItemEntry("SledgeDemolisher", "weapon", max_stack=1, max_quality=4),
        RewardItemEntry("SwordMistwalker", "weapon", max_stack=1, max_quality=4),
        RewardItemEntry("THSwordKrom", "weapon", max_stack=1, max_quality=4),
        RewardItemEntry("StaffFireball", "weapon", max_stack=1, max_quality=4),
        RewardItemEntry("StaffIceShards", "weapon", max_stack=1, max_quality=4),
    )
}


def compute_total_z_span(templates: list[str]) -> float:
    """Computes total North-to-South (+Z) span including 4m inter-room corridors."""
    if not templates:
        return 0.0
    room_sum = sum(ROOM_TEMPLATES[t].length_z for t in templates if t in ROOM_TEMPLATES)
    corridor_sum = CORRIDOR_LENGTH_M * max(0, len(templates) - 1)
    return room_sum + corridor_sum


def build_system_prompt_catalog_block() -> str:
    """Renders the deterministic catalog & Potato-PC rules injected into the LLM system prompt."""
    creatures_by_biome: dict[str, list[str]] = {}
    for c in CREATURE_CATALOG.values():
        tag = f"{c.prefab} ({c.role}{', BOSS-OK' if c.boss_viable else ''})"
        creatures_by_biome.setdefault(c.biome, []).append(tag)

    biome_lines = "\n".join(
        f"  - {biome}: {', '.join(items)}"
        for biome, items in creatures_by_biome.items()
    )

    reward_lines = ", ".join(
        f"{r.prefab} (max_stack={r.max_stack}, max_quality={r.max_quality})"
        for r in REWARD_ITEM_CATALOG.values()
    )

    template_lines = "\n".join(
        f"  - {t.template}: {t.width_x:.0f}m wide x {t.length_z:.0f}m long — {t.description}"
        for t in ROOM_TEMPLATES.values()
    )

    affix_lines = "\n".join(
        f"  - {k}: {v}"
        for k, v in ALL_GLOBAL_AFFIXES.items()
    )

    return f"""You are the Valheim Mythic+ Skybox Rift Architect.
Synthesize a single JSON object strictly conforming to RiftManifest (v0.1).

CRITICAL ARCHITECTURAL & LINTER RULES (MUST PASS 100%):
1. rift_id: lowercase kebab-case slug (e.g. "dwarven-forge-k5").
2. keystone_level: integer 1..15 (scales HP by 1 + 0.18*L and damage by 1 + 0.10*L).
3. par_time_seconds: integer 180..900.
4. global_affixes: 2 or 3 unique affixes MUST include:
   - At least ONE positive blessing affix ("thunder_blessing")
   - AND at least ONE negative affix ("molten_slag", "bolstering", or "shielded_pack").
5. rooms: 3 to 4 ordered RoomSpec objects (room_index: 0, 1, 2, ...):
   - rooms[0] MUST use template="entry_vestibule", pillar_layout="none", and waves=[] (0 waves).
   - Combat rooms (room_index >= 1) MUST NOT use "entry_vestibule" and MUST have 1 to 3 sequential waves (wave_index: 0, 1, ...).
   - SINGLE-ZONE Z-SPAN BUDGET: sum(room length_z) + {CORRIDOR_LENGTH_M:.0f}m * (num_rooms - 1) MUST be <= {MAX_TOTAL_Z_SPAN_M:.0f}m!
     * TIP: A 3-room layout [entry_vestibule (8m) + 4m + crucible_square/gauntlet_bridge (16m) + 4m + sanctum_oct (20m)] = 52m <= 58m (RECOMMENDED!).
     * Do NOT stack 4 large rooms (e.g. 8 + 4 + 16 + 4 + 16 + 4 + 20 = 72m > 58m WILL BE REJECTED).
6. Wave & Boss Constraints:
   - Total mob count per single wave (sum of spawns[].count inside a wave) MUST be <= {MAX_MOBS_PER_WAVE}.
   - Each spawn count in [1..4], stars in [0..2]. Use ranged mages/archers on "sniper_perch_ne" or "sniper_perch_nw".
   - Final room (rooms[-1]) MUST include at least 1 spawn with "is_boss": true.
7. Rewards:
   - Use ONLY curated item prefabs below. For weapons (max_stack=1), set amount=1 and quality in 1..4. For stackable items (max_quality=1), set quality=1.

ROOM TEMPLATES:
{template_lines}

PILLAR LAYOUTS: {', '.join(PILLAR_LAYOUTS)}
SPAWN ANCHORS: {', '.join(SPAWN_ANCHORS)}

GLOBAL AFFIXES:
{affix_lines}

ALLOWED VANILLA CREATURE PREFABS (DO NOT INVENT NAMES):
{biome_lines}

ALLOWED VANILLA REWARD ITEM PREFABS (DO NOT INVENT NAMES):
  {reward_lines}
"""
