"""BYO-LLM Mythic+ Skybox Rift Synthesizer CLI -> Deterministic Linter -> BabBox Hot-Reload Deploy."""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import urllib.error
import urllib.request
import warnings
from pathlib import Path
from typing import Any, Protocol

warnings.filterwarnings("ignore", category=SyntaxWarning, module=r"google\.genai.*")

from google import genai  # noqa: E402
from google.genai import types as genai_types  # noqa: E402
from openai import OpenAI  # noqa: E402

from room_catalog import (  # noqa: E402
    MAX_TOTAL_Z_SPAN_M,
    ROOM_TEMPLATES,
    build_system_prompt_catalog_block,
)
from validator import (  # noqa: E402
    REPO_ROOT,
    RiftManifestModel,
    build_strict_llm_json_schema,
    validate_manifest_text,
)

DEFAULT_BABBOX_HOST = "babbagebox-rtx-lan"
DEFAULT_BABBOX_REMOTE_PATH = (
    "/opt/stacks/rift-sandbox/config/bepinex/ValheimRiftEngine/active_rift.json"
)
DEFAULT_GEMINI_MODEL = "gemini-2.5-pro"
DEFAULT_LOCAL_OLLAMA_BASE_URL = "http://localhost:11434/v1"
MAX_LINT_RETRIES = 2


class SynthesizerBackend(Protocol):
    """Provider-agnostic LLM synthesizer protocol."""

    @property
    def name(self) -> str: ...

    def generate(
        self,
        system_prompt: str,
        messages: list[dict[str, str]],
        json_schema: dict[str, Any],
    ) -> str: ...


class OpenAICompatibleBackend:
    """OpenAI-compatible Chat Completions backend (OpenAI, vLLM, LiteLLM, Carbon, Ollama)."""

    def __init__(self, base_url: str | None, api_key: str, model: str) -> None:
        self._base_url = base_url
        self._model = model
        self._client = OpenAI(base_url=base_url, api_key=api_key, timeout=180.0)

    @property
    def name(self) -> str:
        endpoint = self._base_url or "https://api.openai.com/v1"
        return f"openai-compatible ({self._model} @ {endpoint})"

    def generate(
        self,
        system_prompt: str,
        messages: list[dict[str, str]],
        json_schema: dict[str, Any],
    ) -> str:
        chat_messages: list[dict[str, Any]] = [
            {"role": "system", "content": system_prompt},
            *messages,
        ]

        extra_kwargs: dict[str, Any] = {}
        if self._base_url and ":11434" in self._base_url:
            extra_kwargs["reasoning_effort"] = "none"

        try:
            resp = self._client.chat.completions.create(
                model=self._model,
                messages=chat_messages,  # type: ignore[arg-type]
                temperature=0.35,
                response_format={
                    "type": "json_schema",
                    "json_schema": {
                        "name": "rift_manifest",
                        "strict": True,
                        "schema": json_schema,
                    },
                },
                **extra_kwargs,
            )
        except Exception:
            resp = self._client.chat.completions.create(
                model=self._model,
                messages=chat_messages,  # type: ignore[arg-type]
                temperature=0.35,
                response_format={"type": "json_object"},
            )

        content = resp.choices[0].message.content
        if not content:
            raise RuntimeError("OpenAI-compatible endpoint returned empty message content.")
        return content


class GoogleGenAIBackend:
    """Native google-genai backend (Gemini 2.5 Pro / custom Carbon Gemini endpoint)."""

    def __init__(self, api_key: str, model: str, base_url: str | None = None) -> None:
        self._model = model
        self._base_url = base_url
        if base_url:
            http_opts = genai_types.HttpOptions(base_url=base_url)
            self._client = genai.Client(api_key=api_key, http_options=http_opts)
        else:
            self._client = genai.Client(api_key=api_key)

    @property
    def name(self) -> str:
        suffix = f" @ {self._base_url}" if self._base_url else ""
        return f"google-genai ({self._model}{suffix})"

    def generate(
        self,
        system_prompt: str,
        messages: list[dict[str, str]],
        json_schema: dict[str, Any],
    ) -> str:
        contents: list[genai_types.Content] = []
        for msg in messages:
            role = "model" if msg["role"] == "assistant" else "user"
            contents.append(
                genai_types.Content(
                    role=role,
                    parts=[genai_types.Part.from_text(text=msg["content"])],
                )
            )

        config = genai_types.GenerateContentConfig(
            system_instruction=system_prompt,
            response_mime_type="application/json",
            response_json_schema=json_schema,
            temperature=0.35,
        )

        resp = self._client.models.generate_content(
            model=self._model,
            contents=contents,
            config=config,
        )
        if not resp.text:
            raise RuntimeError("google-genai returned an empty response.")
        return resp.text


def resolve_backend(provider_override: str | None = None) -> SynthesizerBackend:
    """Selects OpenAI-compatible or Google GenAI backend from environment or local Ollama discovery."""
    rift_base_url = os.environ.get("RIFT_LLM_BASE_URL")
    rift_api_key = os.environ.get("RIFT_LLM_API_KEY") or os.environ.get("OPENAI_API_KEY")
    rift_model = os.environ.get("RIFT_LLM_MODEL")

    gemini_key = os.environ.get("GEMINI_API_KEY") or os.environ.get("GOOGLE_API_KEY")
    gemini_base_url = os.environ.get("RIFT_GEMINI_BASE_URL") or os.environ.get("GOOGLE_GENAI_BASE_URL")

    if provider_override == "gemini":
        if not gemini_key:
            raise RuntimeError("GEMINI_API_KEY is required when --provider=gemini.")
        return GoogleGenAIBackend(
            api_key=gemini_key,
            model=rift_model or DEFAULT_GEMINI_MODEL,
            base_url=gemini_base_url,
        )

    if provider_override == "openai" or rift_base_url or (rift_api_key and not gemini_key):
        if rift_base_url or rift_api_key:
            return OpenAICompatibleBackend(
                base_url=rift_base_url,
                api_key=rift_api_key or "local-byo-llm",
                model=rift_model or "gpt-4.1",
            )

    if gemini_key:
        return GoogleGenAIBackend(
            api_key=gemini_key,
            model=rift_model or DEFAULT_GEMINI_MODEL,
            base_url=gemini_base_url,
        )

    discovered_model = _probe_local_ollama_model(rift_model)
    if discovered_model:
        return OpenAICompatibleBackend(
            base_url=DEFAULT_LOCAL_OLLAMA_BASE_URL,
            api_key="ollama",
            model=discovered_model,
        )

    raise RuntimeError(
        "No LLM backend configured! Set either:\n"
        "  - GEMINI_API_KEY (for google-genai, default model gemini-2.5-pro), OR\n"
        "  - RIFT_LLM_BASE_URL + RIFT_LLM_API_KEY + RIFT_LLM_MODEL (for OpenAI-compatible endpoints)."
    )


def _probe_local_ollama_model(preferred_model: str | None) -> str | None:
    try:
        with urllib.request.urlopen("http://localhost:11434/api/tags", timeout=1.5) as resp:
            data = json.loads(resp.read().decode("utf-8"))
        names = [m.get("name", "") for m in data.get("models", []) if m.get("name")]
        if not names:
            return None
        if preferred_model and preferred_model in names:
            return preferred_model
        for candidate in ("qwen3.6:35b-a3b-q8_0", "qwen2.5-coder:7b"):
            if candidate in names:
                return candidate
        return names[0]
    except (urllib.error.URLError, TimeoutError, json.JSONDecodeError, KeyError):
        return None


def synthesize_manifest_with_retries(
    prompt: str,
    backend: SynthesizerBackend,
    max_retries: int = MAX_LINT_RETRIES,
) -> tuple[RiftManifestModel, float, int]:
    """Runs Prompt -> LLM -> Potato-PC Linter with up to `max_retries` self-correction rounds."""
    system_prompt = build_system_prompt_catalog_block()
    strict_schema = build_strict_llm_json_schema()

    messages: list[dict[str, str]] = [
        {
            "role": "user",
            "content": (
                f"Synthesize a complete Valheim Mythic+ RiftManifest JSON for the following dungeon request:\n\n"
                f"{prompt}\n\n"
                f"Return ONLY valid JSON matching the RiftManifest schema."
            ),
        }
    ]

    total_attempts = max_retries + 1
    for attempt in range(1, total_attempts + 1):
        print(f"[rift-synth] Attempt {attempt}/{total_attempts} via {backend.name} ...", flush=True)
        raw_json = backend.generate(
            system_prompt=system_prompt,
            messages=messages,
            json_schema=strict_schema,
        )

        lint = validate_manifest_text(raw_json)
        if lint.valid and lint.manifest is not None:
            return lint.manifest, lint.total_z_span_m, attempt

        formatted_errors = "\n".join(f"  - {err}" for err in lint.errors)
        print(
            f"[rift-synth] Linter rejected attempt {attempt} ({len(lint.errors)} violation(s)):\n{formatted_errors}",
            file=sys.stderr,
            flush=True,
        )

        if attempt >= total_attempts:
            raise RuntimeError(
                f"Manifest failed validation after {total_attempts} attempts.\nLast errors:\n{formatted_errors}"
            )

        messages.append({"role": "assistant", "content": raw_json})
        messages.append(
            {
                "role": "user",
                "content": (
                    "Your RiftManifest JSON was rejected by the deterministic Potato-PC & Mythic+ linter with "
                    "the following exact errors:\n"
                    f"{formatted_errors}\n\n"
                    "Fix EVERY error above and emit the corrected complete RiftManifest JSON object."
                ),
            }
        )

    raise AssertionError("Unreachable")


def render_ascii_topology(manifest: RiftManifestModel, total_z_m: float) -> str:
    """Renders a North-to-South (+Z) terminal ASCII map of the synthesized Skybox Rift."""
    hp_mult = 1.0 + 0.18 * manifest.keystone_level
    dmg_mult = 1.0 + 0.10 * manifest.keystone_level
    par_mm = manifest.par_time_seconds // 60
    par_ss = manifest.par_time_seconds % 60

    lines: list[str] = []
    banner = f" SKYBOX RIFT TOPOLOGY: {manifest.title} (+{manifest.keystone_level}) "
    width = max(74, len(banner) + 8)

    lines.append("═" * width)
    lines.append(banner.center(width, "═"))
    lines.append(
        f" ID: {manifest.rift_id} | Altitude: Y=4500m | Z-Span: {total_z_m:.0f}m / {MAX_TOTAL_Z_SPAN_M:.0f}m MAX"
    )
    lines.append(
        f" Scaling: HP x{hp_mult:.2f}, DMG x{dmg_mult:.2f} | Par Timer: {par_mm:02d}:{par_ss:02d}"
    )
    lines.append(f" Affixes: {', '.join(manifest.global_affixes)}")
    lines.append("═" * width)
    lines.append("                      ▲  NORTH (+Z) — BOSS SANCTUM DAIS  ▲")
    lines.append("")

    for rev_idx in range(len(manifest.rooms) - 1, -1, -1):
        room = manifest.rooms[rev_idx]
        spec = ROOM_TEMPLATES[room.template]
        box_w = 56
        pad = " " * ((width - box_w) // 2)

        header = f" Room {room.room_index}: {room.template} ({spec.width_x:.0f}m x {spec.length_z:.0f}m) "
        lines.append(f"{pad}┌{header.center(box_w - 2, '─')}┐")
        lines.append(f"{pad}│ {('Pillars: ' + room.pillar_layout).ljust(box_w - 4)} │")

        if not room.waves:
            lines.append(f"{pad}│ {'[SAFE STAGING BOX — 5s Keystone Countdown Brazier]'.ljust(box_w - 4)} │")
        else:
            for wave in room.waves:
                mob_total = sum(s.count for s in wave.spawns)
                w_hdr = f"Wave {wave.wave_index} ({mob_total}/10 mobs):"
                lines.append(f"{pad}│ {w_hdr.ljust(box_w - 4)} │")
                for sp in wave.spawns:
                    stars_str = "★" * sp.stars if sp.stars > 0 else "0★"
                    boss_tag = " [BOSS]" if sp.is_boss else ""
                    entry = f"  • {sp.count}x {sp.prefab} ({stars_str}) @ {sp.anchor}{boss_tag}"
                    lines.append(f"{pad}│ {entry[:box_w - 4].ljust(box_w - 4)} │")

        if rev_idx == len(manifest.rooms) - 1:
            lines.append(f"{pad}│ {'[Victory: Chest + Linked Return Portal]'.ljust(box_w - 4)} │")

        lines.append(f"{pad}└{'─' * (box_w - 2)}┘")

        if rev_idx > 0:
            c_pad = " " * ((width - 28) // 2)
            lines.append(f"{c_pad}        │  4m Hall  │")
            lines.append(f"{c_pad}        ╞═══GRATE═══╡  (Gate {rev_idx - 1})")
            lines.append(f"{c_pad}        │  4m Hall  │")

    lines.append("")
    lines.append("                      ▼  SOUTH (-Z) — PARTY ENTRY SPAWN  ▼")
    lines.append("─" * width)

    base_loot = ", ".join(
        f"{d.amount}x {d.prefab}" + (f" (Q{d.quality})" if d.quality > 1 else "")
        for d in manifest.rewards.base_drops
    )
    bonus_loot = ", ".join(
        f"{d.amount}x {d.prefab}" + (f" (Q{d.quality})" if d.quality > 1 else "")
        for d in manifest.rewards.timed_bonus_drops
    )
    lines.append(f" Base Chest Drops : {base_loot}")
    lines.append(f" Timed Bonus Drops: {bonus_loot}")
    lines.append("═" * width)
    return "\n".join(lines)


def deploy_manifest_via_scp(local_file: Path, host: str, remote_path: str) -> None:
    """Ensures remote ValheimRiftEngine config folder exists and SCPs active_rift.json to BabBox."""
    remote_dir = str(Path(remote_path).parent)
    subprocess.run(
        ["ssh", "-o", "BatchMode=yes", host, f"mkdir -p '{remote_dir}'"],
        check=True,
    )
    target = f"{host}:{remote_path}"
    print(f"[rift-synth] Deploying manifest via SCP -> {target} ...", flush=True)
    subprocess.run(
        ["scp", "-q", str(local_file), target],
        check=True,
    )
    print(f"[rift-synth] Hot-reload manifest deployed to {target}!", flush=True)


def build_arg_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="rift-synth",
        description="Synthesize, lint, preview, and hot-deploy a Valheim Mythic+ Skybox Rift Manifest.",
    )
    parser.add_argument(
        "prompt",
        help="Natural-language dungeon prompt (e.g. 'Keystone 5 Dwarven Forge Run: ...')",
    )
    parser.add_argument(
        "--provider",
        choices=("openai", "gemini"),
        default=None,
        help="Force LLM backend provider (defaults to auto-detection from env vars / local Ollama).",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=REPO_ROOT / "active_rift.json",
        help="Local path to write validated active_rift.json.",
    )
    parser.add_argument(
        "--host",
        default=os.environ.get("RIFT_BABBOX_HOST", DEFAULT_BABBOX_HOST),
        help=f"SSH host alias for BabBox (default: {DEFAULT_BABBOX_HOST}).",
    )
    parser.add_argument(
        "--remote-path",
        default=os.environ.get("RIFT_BABBOX_REMOTE_PATH", DEFAULT_BABBOX_REMOTE_PATH),
        help="Remote active_rift.json path on BabBox.",
    )
    parser.add_argument(
        "--no-deploy",
        action="store_true",
        help="Synthesize, validate, and write local JSON without SCPing to BabBox.",
    )
    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_arg_parser()
    args = parser.parse_args(argv)

    try:
        backend = resolve_backend(args.provider)
        manifest, total_z_m, attempts = synthesize_manifest_with_retries(
            prompt=args.prompt,
            backend=backend,
            max_retries=MAX_LINT_RETRIES,
        )
    except Exception as exc:
        print(f"[rift-synth] ERROR: {exc}", file=sys.stderr)
        return 1

    serialized = json.dumps(manifest.model_dump(mode="json"), indent=2) + "\n"
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(serialized, encoding="utf-8")

    print(
        f"[rift-synth] Validated RiftManifest '{manifest.rift_id}' in {attempts} attempt(s) -> {args.output}"
    )
    print(render_ascii_topology(manifest, total_z_m))

    if not args.no_deploy:
        try:
            deploy_manifest_via_scp(args.output, args.host, args.remote_path)
        except subprocess.CalledProcessError as exc:
            print(f"[rift-synth] SCP deployment failed: {exc}", file=sys.stderr)
            return 2

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
