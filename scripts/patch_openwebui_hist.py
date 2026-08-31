#!/usr/bin/env python3
"""ShineosQA: history truncation patch (v1.0.74, idempotent)

Background (measured on Ryzen 7 5700U CPU / CPU inference):
- Open WebUI sends the full chat history to Ollama every turn, so prefill grows
  as the conversation lengthens (measured: +7.6s time-to-first-token at 10 turns).
- This product is single-question oriented, so messages sent to Ollama are capped
  at "system prompt + last 12 messages (6 turns)".
- Only the outgoing payload is truncated; UI display and saved chat (form_data)
  are unchanged, so the full history remains visible in the chat window.

Target: generate_chat_completion in open_webui/routers/ollama.py (just before the Ollama call).

Usage:
  python patch_openwebui_hist.py [open_webui package root]
  (omitted: auto-detected from the running venv via sys.prefix)

Exit code: 0 = already applied (or applied now) / 1 = failure
"""
import shutil
import sys
from pathlib import Path

MARKER = "# [Shineos patch] history truncation (keep last 12)"
ANCHOR = (
    "    prefix_id = api_config.get('prefix_id')\n"
    "    payload['model'] = strip_provider_model_prefix(payload['model'], prefix_id)\n"
    "\n"
    "    return await send_request(\n"
)
INJECT = (
    "    prefix_id = api_config.get('prefix_id')\n"
    "    payload['model'] = strip_provider_model_prefix(payload['model'], prefix_id)\n"
    "\n"
    "    # [Shineos patch] history truncation (keep last 12)\n"
    "    # Full history grows prefill every turn on CPU (measured: +7.6s at 10 turns).\n"
    "    # Send only system prompt + last 12 messages (6 turns). UI/saved chat unchanged.\n"
    "    _msgs = payload.get('messages') or []\n"
    "    _sys = [m for m in _msgs if m.get('role') == 'system']\n"
    "    _non_sys = [m for m in _msgs if m.get('role') != 'system']\n"
    "    if len(_non_sys) > 12:\n"
    "        payload['messages'] = _sys + _non_sys[-12:]\n"
    "\n"
    "    return await send_request(\n"
)


def target_path(explicit=None):
    """Resolve the target file, confined to open_webui/routers/ollama.py."""
    if explicit:
        root = Path(explicit).resolve() / "routers"
    else:
        # When run with the venv python, sys.prefix is the venv root itself
        root = Path(sys.prefix) / "Lib" / "site-packages" / "open_webui" / "routers"
    candidate = (root / "ollama.py").resolve()
    if candidate.parent.name != "routers" or candidate.name != "ollama.py":
        raise FileNotFoundError(f"invalid target: {candidate}")
    if not candidate.is_file():
        raise FileNotFoundError(f"not found: {candidate}")
    return candidate


def main():
    explicit = sys.argv[1] if len(sys.argv) > 1 else None
    try:
        path = target_path(explicit)
    except FileNotFoundError as e:
        print(f"[error] {e}")
        return 1
    print(f"target: {path}")
    src = path.read_text(encoding="utf-8")
    if MARKER in src:
        print("[skip] already patched: " + str(path))
        return 0
    if ANCHOR not in src:
        print("[error] anchor not found in: " + str(path))
        print("        Open WebUI version may have changed; review ANCHOR.")
        return 1
    shutil.copy2(path, str(path) + ".shineos.bak")
    path.write_text(src.replace(ANCHOR, INJECT, 1), encoding="utf-8", newline="")
    print("[done] patched: " + str(path))
    return 0


if __name__ == "__main__":
    sys.exit(main())
