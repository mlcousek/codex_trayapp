"""Inspect bounded local session shapes; print field names only, never payload values."""
from pathlib import Path
import json
import os

home = Path(os.environ.get("CODEX_HOME", str(Path.home() / ".codex")))
found = {}
max_files = 6
max_lines = 20_000
max_line_bytes = 8 * 1024 * 1024


def keys(value):
    return sorted(value) if isinstance(value, dict) else []


for folder in ["sessions", "archived_sessions"]:
    files = sorted((home / folder).rglob("*.jsonl"))[-max_files:]
    for path in files:
        try:
            with path.open("rb") as stream:
                for _ in range(max_lines):
                    line = stream.readline(max_line_bytes + 1)
                    if not line or len(line) > max_line_bytes:
                        break
                    try:
                        record = json.loads(line)
                    except (ValueError, UnicodeDecodeError):
                        continue
                    if not isinstance(record, dict):
                        continue
                    kind = record.get("type")
                    payload = record.get("payload")
                    if not isinstance(payload, dict):
                        continue
                    if kind in ("session_meta", "turn_context"):
                        found[kind] = sorted(set(found.get(kind, [])) | set(payload))
                    elif kind == "event_msg" and payload.get("type") == "token_count":
                        info = payload.get("info")
                        info = info if isinstance(info, dict) else {}
                        shape = {
                            "payloadKeys": keys(payload),
                            "infoKeys": keys(info),
                            "totalFields": keys(info.get("total_token_usage")),
                            "lastFields": keys(info.get("last_token_usage")),
                        }
                        previous = found.get("token_count", {})
                        found["token_count"] = {
                            name: sorted(set(previous.get(name, [])) | set(fields))
                            for name, fields in shape.items()
                        }
        except OSError:
            continue

print(json.dumps(found))
