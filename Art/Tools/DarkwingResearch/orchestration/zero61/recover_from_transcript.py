"""ZERO-61: recover the diagnostic probe sources that were written to OS temp and deleted.

Source of truth is the executing agent's own Claude Code transcript (JSONL).  Each probe was
created by a Bash tool call of the form  cat > /tmp/z61_<name>.py <<'EOF' ... EOF  (quoted
heredoc: bash writes the body verbatim plus a trailing newline).  This script extracts those
bodies byte-for-byte from the recorded tool_use input, never from memory.

Cross-check: when a probe failed, Python's traceback quoted source lines read from the file on
disk ("File ".../z61_x.py", line N" followed by the line).  Those quotations are compared with
line N of the recovered body.  The only available check (z61_err v1, line 4) does NOT match:
the transport collapsed "\\" to "\" before bash.  Recovered bytes are therefore exact copies of
the recorded tool arguments; equality with the deleted disk files is unproven (see manifest).

Usage (from the project root, read-only on the transcript):
  python -I Art/Tools/DarkwingResearch/orchestration/zero61/recover_from_transcript.py \
      <transcript.jsonl> <last_tool_use_id> <output_dir>
"""
import hashlib
import json
import re
import sys
from pathlib import Path

HEREDOC = re.compile(r"cat > /tmp/(z61_\w+\.py) <<'EOF'\n(.*?)\nEOF(?:\n|$)", re.S)
TB_LINE = re.compile(r'File "[^"]*?(z61_\w+\.py)", line (\d+)[^\n]*\n {4}([^\n]*)')


def result_text(block):
    content = block.get("content")
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        return "\n".join(x.get("text", "") for x in content if isinstance(x, dict))
    return ""


def main(transcript, last_id, out_dir):
    out_dir = Path(out_dir)
    uses, results = [], {}
    for line_no, raw in enumerate(open(transcript, encoding="utf-8")):
        rec = json.loads(raw)
        msg = rec.get("message")
        if not isinstance(msg, dict) or not isinstance(msg.get("content"), list):
            continue
        for block in msg["content"]:
            if not isinstance(block, dict):
                continue
            if block.get("type") == "tool_use":
                uses.append({"line": line_no, "timestamp": rec.get("timestamp"), "id": block["id"],
                             "name": block["name"], "input": block["input"]})
            elif block.get("type") == "tool_result":
                results[block["tool_use_id"]] = {"is_error": bool(block.get("is_error")),
                                                 "text": result_text(block)}
    end = next(i for i, u in enumerate(uses) if u["id"] == last_id)
    uses = uses[: end + 1]

    versions = {}
    for use in uses:
        if use["name"] != "Bash":
            continue
        for name, body in HEREDOC.findall(use["input"].get("command", "")):
            versions.setdefault(name, []).append((use, (body + "\n").encode("utf-8")))

    manifest = {"schema": "zero61.recovered-probes.v1", "transcript": str(transcript),
                "transcript_sha256": hashlib.sha256(Path(transcript).read_bytes()).hexdigest(),
                "last_tool_use_id": last_id, "probes": []}
    for name, items in versions.items():
        for idx, (use, data) in enumerate(items):
            final = idx == len(items) - 1
            rel = Path("restored") / (name if final else f"superseded/{name[:-3]}.v{idx + 1}.py")
            target = out_dir / rel
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
            # Traceback quotations come from the result of the same call or a later call that ran
            # the same file before the next rewrite.
            src_lines = data.decode("utf-8").split("\n")
            checks = []
            for res_use in [use] + [u for u in uses if u["line"] > use["line"]]:
                cmd = res_use["input"].get("command", "") if res_use["name"] == "Bash" else ""
                if res_use is not use and f"/tmp/{name} <<" in cmd:
                    break
                text = results.get(res_use["id"], {}).get("text", "")
                for tb_name, n, quoted in TB_LINE.findall(text):
                    if tb_name == name:
                        n = int(n)
                        own = src_lines[n - 1].strip() if n <= len(src_lines) else None
                        checks.append({"tool_use_id": res_use["id"], "line": n,
                                       "match": own == quoted.strip()})
            res = results.get(use["id"], {})
            manifest["probes"].append({
                "file": rel.as_posix(), "name": name, "version": idx + 1, "final_on_disk": final,
                "sha256": hashlib.sha256(data).hexdigest(), "bytes": len(data),
                # Observed on z61_err v1: a "\\" in the tool argument reached the disk file as "\".
                # Files without it carry no observed transport difference.
                "double_backslash_count": data.count(b"\\\\"),
                "source_tool_use_id": use["id"], "source_transcript_line": use["line"],
                "source_timestamp": use["timestamp"], "write_call_is_error": res.get("is_error"),
                "traceback_disk_line_checks": checks,
            })
    commands = [{"tool_use_id": u["id"], "timestamp": u["timestamp"], "tool": u["name"],
                 "input": u["input"], "result_is_error": results.get(u["id"], {}).get("is_error")}
                for u in uses]
    (out_dir / "zero61-commands.json").write_text(
        json.dumps(commands, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    manifest["commands_archive"] = "zero61-commands.json"
    manifest["commands_count"] = len(commands)
    (out_dir / "recovered-manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    for p in manifest["probes"]:
        print(p["file"], p["sha256"], p["bytes"], p["double_backslash_count"],
              p["source_tool_use_id"], p["traceback_disk_line_checks"])


if __name__ == "__main__":
    main(*sys.argv[1:4])
