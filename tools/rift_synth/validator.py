"""Deterministic Potato-PC, JSON Schema, and Mythic+ Gameplay Linter for Rift Manifests."""

from __future__ import annotations

import difflib
import json
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Literal

import jsonschema
from pydantic import BaseModel, ConfigDict, Field, ValidationError

from room_catalog import (
    CORRIDOR_LENGTH_M,
    CREATURE_CATALOG,
    MAX_MOBS_PER_WAVE,
    MAX_ROOMS,
    MAX_TOTAL_Z_SPAN_M,
    MAX_WAVES_PER_ROOM,
    MIN_ROOMS,
    NEGATIVE_AFFIXES,
    POSITIVE_BLESSING_AFFIXES,
    REWARD_ITEM_CATALOG,
    ROOM_TEMPLATES,
    compute_total_z_span,
)

REPO_ROOT = Path(__file__).resolve().parents[2]
SCHEMA_PATH = REPO_ROOT / "schemas" / "rift_manifest.schema.json"

GlobalAffixLiteral = Literal["molten_slag", "bolstering", "thunder_blessing", "shielded_pack"]
RoomTemplateLiteral = Literal["entry_vestibule", "crucible_square", "gauntlet_bridge", "sanctum_oct"]
PillarLayoutLiteral = Literal["none", "four_corners", "center_los_blocker"]
AnchorLiteral = Literal[
    "center",
    "north_left",
    "north_right",
    "south_left",
    "south_right",
    "sniper_perch_ne",
    "sniper_perch_nw",
]


class SpawnSpecModel(BaseModel):
    model_config = ConfigDict(extra="forbid")

    prefab: str = Field(min_length=2, max_length=64)
    anchor: AnchorLiteral
    count: int = Field(ge=1, le=4)
    stars: int = Field(ge=0, le=2)
    is_boss: bool


class WaveSpecModel(BaseModel):
    model_config = ConfigDict(extra="forbid")

    wave_index: int = Field(ge=0, le=2)
    spawns: list[SpawnSpecModel] = Field(min_length=1, max_length=10)


class RoomSpecModel(BaseModel):
    model_config = ConfigDict(extra="forbid")

    room_index: int = Field(ge=0, le=3)
    template: RoomTemplateLiteral
    pillar_layout: PillarLayoutLiteral
    waves: list[WaveSpecModel] = Field(min_length=0, max_length=3)


class LootDropSpecModel(BaseModel):
    model_config = ConfigDict(extra="forbid")

    prefab: str = Field(min_length=2, max_length=64)
    amount: int = Field(ge=1, le=999)
    quality: int = Field(ge=1, le=4)


class RewardSpecModel(BaseModel):
    model_config = ConfigDict(extra="forbid")

    base_drops: list[LootDropSpecModel] = Field(min_length=1, max_length=8)
    timed_bonus_drops: list[LootDropSpecModel] = Field(min_length=1, max_length=8)


class RiftManifestModel(BaseModel):
    model_config = ConfigDict(extra="forbid")

    rift_id: str = Field(pattern=r"^[a-z0-9]+(?:-[a-z0-9]+)*$", min_length=3, max_length=64)
    title: str = Field(min_length=3, max_length=80)
    keystone_level: int = Field(ge=1, le=15)
    par_time_seconds: int = Field(ge=180, le=900)
    global_affixes: list[GlobalAffixLiteral] = Field(min_length=1, max_length=3)
    rooms: list[RoomSpecModel] = Field(min_length=MIN_ROOMS, max_length=MAX_ROOMS)
    rewards: RewardSpecModel


@dataclass
class LintResult:
    valid: bool
    manifest: RiftManifestModel | None = None
    errors: list[str] = field(default_factory=list)
    total_z_span_m: float = 0.0


def load_json_schema() -> dict[str, Any]:
    with SCHEMA_PATH.open("r", encoding="utf-8") as fh:
        return json.load(fh)


def build_strict_llm_json_schema() -> dict[str, Any]:
    """Returns an OpenAI/Gemini strict-compatible JSON Schema derived from rift_manifest.schema.json."""
    raw = load_json_schema()
    schema = json.loads(json.dumps(raw))
    schema.pop("$schema", None)
    schema.pop("$id", None)
    # Strip keywords rejected by strict OpenAI structured output validators while keeping validator.py authoritative
    _strip_unsupported_strict_keywords(schema)
    return schema


def _strip_unsupported_strict_keywords(node: Any) -> None:
    if isinstance(node, dict):
        for key in ("uniqueItems", "minLength", "maxLength", "pattern", "minimum", "maximum", "minItems", "maxItems"):
            node.pop(key, None)
        for val in node.values():
            _strip_unsupported_strict_keywords(val)
    elif isinstance(node, list):
        for item in node:
            _strip_unsupported_strict_keywords(item)


def validate_manifest_text(raw_text: str) -> LintResult:
    """Parses raw LLM JSON text and runs JSON Schema + Pydantic + Potato-PC semantic linting."""
    cleaned = _extract_json_payload(raw_text)
    try:
        payload = json.loads(cleaned)
    except json.JSONDecodeError as exc:
        return LintResult(valid=False, errors=[f"Invalid JSON syntax: {exc}"])

    if not isinstance(payload, dict):
        return LintResult(valid=False, errors=["Top-level JSON value must be an object."])

    return validate_manifest_dict(payload)


def validate_manifest_dict(payload: dict[str, Any]) -> LintResult:
    errors: list[str] = []

    schema = load_json_schema()
    schema_validator = jsonschema.Draft202012Validator(schema)
    for err in sorted(schema_validator.iter_errors(payload), key=lambda e: list(e.absolute_path)):
        loc = ".".join(str(p) for p in err.absolute_path) or "<root>"
        errors.append(f"Schema violation at '{loc}': {err.message}")

    try:
        model = RiftManifestModel.model_validate(payload)
    except ValidationError as exc:
        for e in exc.errors():
            loc = ".".join(str(x) for x in e["loc"])
            errors.append(f"Model error at '{loc}': {e['msg']}")
        return LintResult(valid=False, errors=_dedupe(errors))

    # 1. Global Affix Rules: unique + >=1 positive blessing + >=1 negative curse
    if len(set(model.global_affixes)) != len(model.global_affixes):
        errors.append(f"global_affixes contains duplicate entries: {model.global_affixes}.")

    has_positive = any(a in POSITIVE_BLESSING_AFFIXES for a in model.global_affixes)
    has_negative = any(a in NEGATIVE_AFFIXES for a in model.global_affixes)
    if not has_positive:
        errors.append(
            f"global_affixes {model.global_affixes} must include at least 1 positive blessing affix "
            f"({sorted(POSITIVE_BLESSING_AFFIXES)})."
        )
    if not has_negative:
        errors.append(
            f"global_affixes {model.global_affixes} must include at least 1 negative challenge affix "
            f"({sorted(NEGATIVE_AFFIXES)}) alongside the positive blessing."
        )

    # 2. Topological Room & Single-Zone Z-Span Budget (<= 58m)
    if not (MIN_ROOMS <= len(model.rooms) <= MAX_ROOMS):
        errors.append(f"rooms count ({len(model.rooms)}) must be between {MIN_ROOMS} and {MAX_ROOMS}.")

    templates = [r.template for r in model.rooms]
    total_z = compute_total_z_span(templates)
    if total_z > MAX_TOTAL_Z_SPAN_M:
        room_parts = " + ".join(f"{r.template}({ROOM_TEMPLATES[r.template].length_z:.0f}m)" for r in model.rooms)
        corridors_m = CORRIDOR_LENGTH_M * max(0, len(model.rooms) - 1)
        errors.append(
            f"Total Rift Z-span is {total_z:.0f}m ({room_parts} + {corridors_m:.0f}m corridors), "
            f"which exceeds the {MAX_TOTAL_Z_SPAN_M:.0f}m single-zone Potato-PC limit. "
            f"Reduce to 3 rooms (e.g. entry_vestibule + crucible_square/gauntlet_bridge + sanctum_oct = 52m)."
        )

    if model.rooms:
        if model.rooms[0].template != "entry_vestibule":
            errors.append(
                f"rooms[0].template must be 'entry_vestibule' (got '{model.rooms[0].template}')."
            )
        if len(model.rooms[0].waves) != 0:
            errors.append(
                f"rooms[0] (entry_vestibule) is a safe staging room and must have waves=[] (got {len(model.rooms[0].waves)} waves)."
            )

    # 3. Per-Room Waves, Mob Counts, and Creature Prefab Validation
    for idx, room in enumerate(model.rooms):
        if room.room_index != idx:
            errors.append(f"rooms[{idx}].room_index must be {idx} (got {room.room_index}).")

        if idx >= 1:
            if room.template == "entry_vestibule":
                errors.append(f"Combat room rooms[{idx}] cannot use template 'entry_vestibule'.")
            if len(room.waves) == 0:
                errors.append(f"Combat room rooms[{idx}] ({room.template}) must have at least 1 wave.")

        if len(room.waves) > MAX_WAVES_PER_ROOM:
            errors.append(f"rooms[{idx}] has {len(room.waves)} waves (max {MAX_WAVES_PER_ROOM}).")

        for w_idx, wave in enumerate(room.waves):
            if wave.wave_index != w_idx:
                errors.append(
                    f"rooms[{idx}].waves[{w_idx}].wave_index must be {w_idx} (got {wave.wave_index})."
                )

            wave_mob_total = sum(s.count for s in wave.spawns)
            if wave_mob_total > MAX_MOBS_PER_WAVE:
                errors.append(
                    f"rooms[{idx}].waves[{w_idx}] spawns {wave_mob_total} mobs, exceeding the "
                    f"hard Potato-PC wave cap of {MAX_MOBS_PER_WAVE}. Split spawns across sequential waves."
                )

            for s_idx, spawn in enumerate(wave.spawns):
                if spawn.prefab not in CREATURE_CATALOG:
                    hint = _closest_match(spawn.prefab, CREATURE_CATALOG.keys())
                    errors.append(
                        f"rooms[{idx}].waves[{w_idx}].spawns[{s_idx}] uses unknown creature prefab "
                        f"'{spawn.prefab}'.{hint}"
                    )

    # 4. Final Room Boss Requirement
    if model.rooms:
        final_room = model.rooms[-1]
        has_boss = any(s.is_boss for w in final_room.waves for s in w.spawns)
        if not has_boss:
            errors.append(
                f"Final room rooms[{len(model.rooms) - 1}] ({final_room.template}) must contain "
                f"at least 1 spawn with is_boss=true."
            )

    # 5. Reward Item Prefab & Stack/Quality Validation
    for section_name, drops in (
        ("rewards.base_drops", model.rewards.base_drops),
        ("rewards.timed_bonus_drops", model.rewards.timed_bonus_drops),
    ):
        for d_idx, drop in enumerate(drops):
            if drop.prefab not in REWARD_ITEM_CATALOG:
                hint = _closest_match(drop.prefab, REWARD_ITEM_CATALOG.keys())
                errors.append(
                    f"{section_name}[{d_idx}] uses unknown item prefab '{drop.prefab}'.{hint}"
                )
                continue

            item_meta = REWARD_ITEM_CATALOG[drop.prefab]
            if drop.quality > item_meta.max_quality:
                errors.append(
                    f"{section_name}[{d_idx}] '{drop.prefab}' has quality={drop.quality}, "
                    f"but max_quality for '{drop.prefab}' is {item_meta.max_quality}."
                )
            if item_meta.max_stack == 1 and drop.amount > 2:
                errors.append(
                    f"{section_name}[{d_idx}] '{drop.prefab}' is unstackable equipment (max_stack=1); "
                    f"set amount to 1 or 2 (got {drop.amount})."
                )

    deduped = _dedupe(errors)
    return LintResult(
        valid=len(deduped) == 0,
        manifest=model if len(deduped) == 0 else None,
        errors=deduped,
        total_z_span_m=total_z,
    )


def _extract_json_payload(raw_text: str) -> str:
    text = raw_text.strip()
    # Strip <think>...</think> blocks emitted by reasoning models
    if "<think>" in text and "</think>" in text:
        end_idx = text.rfind("</think>")
        text = text[end_idx + len("</think>") :].strip()

    if text.startswith("```"):
        lines = text.splitlines()
        if lines and lines[0].startswith("```"):
            lines = lines[1:]
        if lines and lines[-1].strip() == "```":
            lines = lines[:-1]
        text = "\n".join(lines).strip()

    first_brace = text.find("{")
    last_brace = text.rfind("}")
    if first_brace != -1 and last_brace != -1 and last_brace > first_brace:
        return text[first_brace : last_brace + 1]
    return text


def _closest_match(bad_name: str, candidates: Any) -> str:
    matches = difflib.get_close_matches(bad_name, list(candidates), n=3, cutoff=0.5)
    return f" Did you mean: {', '.join(matches)}?" if matches else ""


def _dedupe(items: list[str]) -> list[str]:
    seen: set[str] = set()
    out: list[str] = []
    for item in items:
        if item not in seen:
            seen.add(item)
            out.append(item)
    return out
