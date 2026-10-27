"""Local preservation gate. No game execution, cleanup, upload or automatic deletion.

snapshot copies source/evidence (including ignored recordings) outside the checkout,
captures Git history, hashes every file and verifies the destination by rereading it.
verify detects missing/extra/modified snapshot files. audit checks source ownership,
disposable source locations, and current source/evidence coverage by a snapshot.
See README.md for scope, archival lifecycle and recovery commands.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
from datetime import datetime, timezone

ROOT = Path(__file__).resolve().parents[2]
ROOTS = ("AGENTS.md", ".gitignore", ".vscode", "Tools", "AgentScripts", "Assets", "Packages",
         "ProjectSettings", "Docs", "Research", "Playthroughs", "Art")
DISPOSABLE = ("Temp", "Build", "Builds", "Library", "Logs", "obj")
SOURCE_SUFFIXES = {".py", ".ps1", ".lua", ".cs", ".csproj", ".sh", ".cmd", ".bat"}
SKIP_DIRS = {".git", "__pycache__", "bin", "obj", "node_modules"}
META = {"manifest.json", "git-history.bundle", "git-state.json"}


def digest(path):
    h = hashlib.sha256()
    with path.open("rb") as f:
        for block in iter(lambda: f.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()


def git(*args):
    return subprocess.check_output(["git", "-C", str(ROOT), *args])


def files(roots):
    found = {}
    for relative in roots:
        start = ROOT / relative
        if not start.exists():
            continue
        if start.is_symlink():
            raise ValueError(f"Refusing linked root: {start}")
        candidates = [start] if start.is_file() else None
        if candidates is None:
            candidates = []
            for parent, dirs, names in os.walk(start):
                for d in dirs:
                    p = Path(parent) / d
                    if p.is_symlink() or p.is_junction():
                        raise ValueError(f"Refusing linked directory: {p}")
                dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
                candidates.extend(Path(parent) / name for name in names
                                  if name != "UnityLockfile" and not name.endswith((".pyc", ".pyo")))
        for path in candidates:
            if path.is_symlink() or not path.resolve().is_relative_to(ROOT):
                raise ValueError(f"Refusing linked/outside file: {path}")
            found[path.relative_to(ROOT).as_posix()] = path
    return dict(sorted(found.items()))


def entry(path):
    before = path.stat()
    value = {"bytes": before.st_size, "sha256": digest(path)}
    after = path.stat()
    if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
        raise ValueError(f"File changed while hashing: {path}")
    return value


def load_manifest(destination):
    manifest = json.loads((destination / "manifest.json").read_text(encoding="utf-8"))
    if manifest.get("schema") != 1 or manifest.get("complete") is not True or not manifest.get("files"):
        raise ValueError("Incomplete/unsupported backup manifest")
    for name in manifest["files"]:
        path = destination / name
        if not path.resolve().is_relative_to(destination) or Path(name).is_absolute():
            raise ValueError(f"Unsafe manifest path: {name}")
    return manifest


def verify(destination):
    destination = destination.resolve()
    manifest = load_manifest(destination)
    expected = manifest["files"]
    errors = []
    actual = {p.relative_to(destination).as_posix() for p in destination.rglob("*") if p.is_file()}
    for name in sorted(actual - set(expected) - {"manifest.json"}):
        errors.append(f"EXTRA {name}")
    for name, wanted in expected.items():
        p = destination / name
        if not p.is_file():
            errors.append(f"MISSING {name}")
        elif p.is_symlink() or entry(p) != wanted:
            errors.append(f"MISMATCH {name}")
    return manifest, errors


def snapshot(destination, includes=()):
    destination = destination.resolve()
    if destination.is_relative_to(ROOT) or ROOT.is_relative_to(destination):
        raise ValueError("Backup must be a separate NEW directory outside the checkout")
    if destination.exists():
        raise ValueError("Snapshot already exists; never overwrite the previous backup")
    roots = tuple(dict.fromkeys((*ROOTS, *includes)))
    for name in roots:
        if Path(name).is_absolute() or not (ROOT / name).resolve().is_relative_to(ROOT):
            raise ValueError(f"Protected paths must stay within assigned project: {name}")
        if name in includes and not (ROOT / name).exists():
            raise ValueError(f"Explicitly protected input does not exist: {name}")
    sources = files((*roots, "Build/CostHarness", "Temp"))
    destination.mkdir(parents=True)
    manifest = {"schema": 1, "complete": False, "createdUtc": datetime.now(timezone.utc).isoformat(),
                "sourceRoot": str(ROOT), "head": git("rev-parse", "HEAD").decode().strip(),
                "roots": list(roots), "files": {}}
    target = destination / "manifest.json"
    target.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    for name, source in sources.items():
        wanted = entry(source)
        copied = destination / "files" / name
        copied.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, copied)
        if entry(copied) != wanted or entry(source) != wanted:
            raise ValueError(f"Copy/source changed: {name}; incomplete snapshot retained")
        manifest["files"]["files/" + name] = wanted
    state = {"head": manifest["head"], "status": git("status", "--porcelain=v1").decode("utf-8"),
             "stagedPatch": git("diff", "--cached", "--binary").decode("utf-8"),
             "workingPatch": git("diff", "--binary").decode("utf-8")}
    state_path = destination / "git-state.json"
    state_path.write_text(json.dumps(state, ensure_ascii=False, indent=2), encoding="utf-8")
    git("bundle", "create", str(destination / "git-history.bundle"), "--all")
    subprocess.run(["git", "-C", str(ROOT), "bundle", "verify", str(destination / "git-history.bundle")],
                   check=True, capture_output=True)
    for name in META - {"manifest.json"}:
        manifest["files"][name] = entry(destination / name)
    manifest["complete"] = True
    target.write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    _, errors = verify(destination)
    if errors:
        manifest["complete"] = False
        target.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
        raise ValueError("Destination verification failed: " + "; ".join(errors[:10]))
    return {"result": "PASS", "snapshot": str(destination), "files": len(manifest["files"]),
            "bytes": sum(e["bytes"] for e in manifest["files"].values()), "manifestSha256": digest(target)}


def audit(destination):
    manifest, errors = verify(destination)
    roots = tuple(dict.fromkeys((*ROOTS, *manifest.get("roots", ()))))
    current = files(roots)
    tracked = set(git("ls-files", "--cached", "-z").decode("utf-8").split("\0"))
    issues = list(errors)
    current_head = git("rev-parse", "HEAD").decode().strip()
    if manifest["head"] != current_head:
        issues.append("GIT HEAD CHANGED SINCE BACKUP")
    lock_path = ROOT / "Packages/packages-lock.json"
    locked_packages = json.loads(lock_path.read_text(encoding="utf-8")).get("dependencies", {}) if lock_path.exists() else {}
    source_names = [n for n,p in current.items() if n.startswith(("Tools/", "AgentScripts/")) and p.suffix in SOURCE_SUFFIXES]
    ignored_result = subprocess.run(["git", "-C", str(ROOT), "check-ignore", "--no-index", "--stdin", "-z"],
                                    input="\0".join(source_names).encode("utf-8") + b"\0", capture_output=True)
    if ignored_result.returncode not in (0, 1):
        raise ValueError(ignored_result.stderr.decode("utf-8"))
    ignored = set(ignored_result.stdout.decode("utf-8").split("\0"))
    for stored in manifest["files"]:
        if not stored.startswith("files/"):
            continue
        name = stored[len("files/"):]
        if any(name == r or name.startswith(r + "/") for r in roots) and name not in current:
            issues.append("MISSING CURRENT FILE " + name)
    for name, path in current.items():
        if name.startswith(("Tools/", "AgentScripts/")) and path.suffix in SOURCE_SUFFIXES:
            if name in ignored:
                issues.append("IGNORED SOURCE " + name)
            if name not in tracked:
                issues.append("UNTRACKED SOURCE " + name)
        wanted = manifest["files"].get("files/" + name)
        if wanted is None:
            issues.append("NOT BACKED UP " + name)
        elif entry(path) != wanted:
            issues.append("CHANGED SINCE BACKUP " + name)
    generated = 0
    for name, path in files(DISPOSABLE).items():
        if path.suffix not in SOURCE_SUFFIXES:
            continue
        # UPM source cache is reproduced from versioned Packages/manifest + lock;
        # custom research files elsewhere in Library are still reported.
        package = name.split("/")[2].split("@")[0] if name.startswith("Library/PackageCache/") else None
        if package in locked_packages and locked_packages[package].get("source") in ("registry", "git", "builtin"):
            generated += 1
            continue
        issues.append("DISPOSABLE SOURCE (review generated vs unique) " + name)
    return {"result": "FAIL" if issues else "PASS", "head": current_head,
            "snapshotHead": manifest["head"], "checked": len(current),
            "upmCacheSourcesExcluded": generated, "issues": issues}


def selftest():
    # Test actual snapshot reader against missing/extra/tampered data and incomplete manifests.
    with tempfile.TemporaryDirectory(prefix="darkwing-preservation-") as name:
        root = Path(name).resolve()
        file = root / "input.bin"
        file.write_bytes(b"original reference")
        manifest = {"schema": 1, "complete": True, "files": {"input.bin": entry(file)}}
        meta = root / "manifest.json"
        meta.write_text(json.dumps(manifest))
        assert not verify(root)[1], "valid snapshot rejected"
        file.write_bytes(b"changed reference")
        assert verify(root)[1], "modified file accepted"
        file.unlink()
        assert verify(root)[1], "missing file accepted"
        file.write_bytes(b"original reference")
        extra = root / "extra.bin"
        extra.write_bytes(b"extra")
        assert verify(root)[1], "extra file accepted"
        extra.unlink()
        manifest["complete"] = False
        meta.write_text(json.dumps(manifest))
        try:
            verify(root)
        except ValueError:
            pass
        else:
            raise AssertionError("incomplete snapshot accepted")
        try:
            snapshot(ROOT / "Temp/forbidden-backup")
        except ValueError:
            pass
        else:
            raise AssertionError("backup inside checkout accepted")
        for required in ("../outside-project", "__preservation_missing_required_path__"):
            try:
                snapshot(root / "required-path-check", (required,))
            except ValueError:
                pass
            else:
                raise AssertionError("invalid required path accepted: " + required)
    return {"result": "PASS", "positive": 1, "negative": 7}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    for command in ("snapshot", "verify", "audit"):
        p = sub.add_parser(command)
        p.add_argument("destination", type=Path, help="external local snapshot directory")
        if command == "snapshot":
            p.add_argument("--include", action="append", default=[], help="extra protected project-relative path, e.g. current playable build; repeatable")
    sub.add_parser("selftest")
    args = parser.parse_args()
    try:
        if args.command == "selftest":
            result = selftest()
        elif args.command == "snapshot":
            result = snapshot(args.destination, args.include)
        elif args.command == "verify":
            manifest, errors = verify(args.destination)
            result = {"result": "FAIL" if errors else "PASS", "files": len(manifest["files"]), "issues": errors}
        else:
            result = audit(args.destination.resolve())
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 0 if result["result"] == "PASS" else 1
    except (OSError, ValueError, KeyError, subprocess.CalledProcessError) as exc:
        print(json.dumps({"result": "ERROR", "error": str(exc)}, ensure_ascii=False))
        return 2


if __name__ == "__main__":
    sys.exit(main())
