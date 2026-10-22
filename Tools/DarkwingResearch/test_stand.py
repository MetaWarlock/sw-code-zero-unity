import importlib.util
import base64
from contextlib import redirect_stdout
import csv
import io
import shutil
import json
from pathlib import Path
import subprocess
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch


spec = importlib.util.spec_from_file_location("stand", Path(__file__).parent / "stand/stand.py")
stand = importlib.util.module_from_spec(spec)
spec.loader.exec_module(stand)


class ReplayBoundaryTests(unittest.TestCase):
    def scene_fixture(self, root):
        session = root / "session"
        core = root / "core"
        session.mkdir()
        core.mkdir()
        (session / "input.bin").write_bytes(bytes(2))
        replay = {"frames": 2, "captureAtStart": True, "inputs": [], "captures": []}
        (core / "spec.json").write_text(json.dumps(replay))
        (core / "buffers.bin").write_bytes(bytes(stand.BUFFER_STRIDE * 2))
        fields = ["x", "y", "state", "world", "phaseFrames", "scrollX", "chr0", "chr1", "ctrl", "mask", "completed", "maskCompleted"]
        trace = io.StringIO()
        writer = csv.DictWriter(trace, fieldnames=["frame", "gameFrame", "phase", *fields])
        writer.writeheader()
        states = []
        for frame in (1, 2):
            values = dict.fromkeys(fields, 0)
            values.update(frame=frame, gameFrame=0, phase="Mission")
            writer.writerow(values)
            values.update(scrollY=0, scoreUnits=0, livesLeft=0, livesRight=0, bossDamage=0,
                          nametable=base64.b64encode(bytes(4096)).decode(),
                          oam=base64.b64encode(bytes(256)).decode(),
                          palette=base64.b64encode(bytes(32)).decode())
            states.append(values)
        (core / "trace.csv").write_text(trace.getvalue() + "# finalGameFrame=0\n")
        route = {"editor": False, "input": replay, "states": states, "final": dict(states[-1])}
        route_path = root / "route.json"
        route_path.write_text(json.dumps(route))
        return SimpleNamespace(route=route_path, session=session, core=core), route

    def test_input_polls_and_game_clock_are_distinct(self):
        with tempfile.TemporaryDirectory() as temporary:
            arguments, _ = self.scene_fixture(Path(temporary))
            output = io.StringIO()
            with redirect_stdout(output):
                stand.cmd_scene(arguments)
            report = json.loads(output.getvalue())
            self.assertTrue(report["matchedCore"])
            self.assertEqual(report["framesConsumed"], 2)
            self.assertEqual(report["gameFramesAdvanced"], 0)

    def test_scene_still_rejects_clock_and_poll_mismatches(self):
        with tempfile.TemporaryDirectory() as temporary:
            arguments, original = self.scene_fixture(Path(temporary))
            for location, key, expected_field in [("snapshot", "gameFrame", "gameFrame"),
                                                  ("final", "gameFrame", "final gameFrame"),
                                                  ("final", "frame", "final input frame")]:
                with self.subTest(location=location, key=key):
                    route = json.loads(json.dumps(original))
                    target = route["states"][-1] if location == "snapshot" else route["final"]
                    target[key] += 1
                    arguments.route.write_text(json.dumps(route))
                    output = io.StringIO()
                    with redirect_stdout(output), self.assertRaises(SystemExit) as failure:
                        stand.cmd_scene(arguments)
                    self.assertEqual(failure.exception.code, 1)
                    report = json.loads(output.getvalue())
                    self.assertFalse(report["matchedCore"])
                    self.assertIn(expected_field, [error["field"] for error in report["errors"]])

    def test_scene_rejects_missing_or_extra_trace_polls(self):
        with tempfile.TemporaryDirectory() as temporary:
            arguments, _ = self.scene_fixture(Path(temporary))
            trace_path = arguments.core / "trace.csv"
            lines = trace_path.read_text().splitlines()
            for rows in (lines[:2] + lines[3:], lines[:3] + [lines[2]] + lines[3:]):
                trace_path.write_text("\n".join(rows))
                with self.assertRaisesRegex(ValueError, "Incomplete/duplicate Core trace polls"):
                    stand.cmd_scene(arguments)

    def test_scene_requires_actual_core_game_clock(self):
        with tempfile.TemporaryDirectory() as temporary:
            arguments, _ = self.scene_fixture(Path(temporary))
            trace_path = arguments.core / "trace.csv"
            trace_path.write_text(trace_path.read_text().replace("frame,gameFrame,", "frame,unrecordedClock,"))
            with self.assertRaisesRegex(ValueError, "Missing Core gameFrame export"):
                stand.cmd_scene(arguments)

    def test_scene_requires_one_final_core_clock(self):
        with tempfile.TemporaryDirectory() as temporary:
            arguments, _ = self.scene_fixture(Path(temporary))
            trace_path = arguments.core / "trace.csv"
            original = trace_path.read_text()
            for text in (original.replace("# finalGameFrame=0", ""), original + "# finalGameFrame=0\n"):
                trace_path.write_text(text)
                with self.assertRaisesRegex(ValueError, "Missing/duplicate Core final gameFrame"):
                    stand.cmd_scene(arguments)

    @unittest.skipUnless(shutil.which("pwsh") or shutil.which("powershell"), "PowerShell harness")
    def test_player_completion_checks_input_poll_not_game_counter(self):
        script_path = str(Path(__file__).parent / "verify_player.ps1").replace("'", "''")
        code = r'''
$tokens=$null; $errors=$null
$ast=[System.Management.Automation.Language.Parser]::ParseFile('__SCRIPT__',[ref]$tokens,[ref]$errors)
$block=$ast.Find({param($node) $node -is [System.Management.Automation.Language.IfStatementAst] -and $node.Extent.Text.StartsWith('if ($CompletedMission) {')},$true)
if (-not $block -or $errors.Count) { throw 'Cannot locate actual completion validator' }
$validate=[scriptblock]::Create($block.Extent.Text)
$CompletedMission=$true; $spec=@{frames=100}; $result=@{final=$null}
$cases=@(
 @{frame=100;game=100;count=1;mask=1;pass=$true},
 @{frame=100;game=66;count=1;mask=1;pass=$true},
 @{frame=99;game=100;count=1;mask=1;pass=$false},
 @{frame=101;game=100;count=1;mask=1;pass=$false},
 @{frame=100;game=66;count=0;mask=1;pass=$false},
 @{frame=100;game=66;count=1;mask=2;pass=$false})
foreach($case in $cases) {
 $routeData=@{final=@{frame=$case.frame;gameFrame=$case.game;phase='MapCursor';completed=$case.count;maskCompleted=$case.mask}}
 $passed=$true
 try { & $validate } catch { $passed=$false }
 if($passed -ne $case.pass) { throw ('Unexpected completion verdict: '+($case|ConvertTo-Json -Compress)) }
}
'''.replace("__SCRIPT__", script_path)
        encoded = base64.b64encode(code.encode("utf-16-le")).decode()
        run = subprocess.run([shutil.which("pwsh") or shutil.which("powershell"),
                              "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded], capture_output=True)
        self.assertEqual(run.returncode, 0, run.stderr.decode(errors="replace"))

    def test_export_declares_pre_input_and_keeps_track(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            session = root / "session"
            session.mkdir()
            track = bytes([0, 128, 64])
            (session / "input.bin").write_bytes(track)
            response = {"success": True, "data": {"result": {"success": True, "result": "mock execution"}}}
            result = subprocess.CompletedProcess([], 0, json.dumps(response), "")
            with patch.object(stand, "ROOT", root), patch.object(stand.subprocess, "run", return_value=result):
                stand.run_port(session, [1, 3], 1, output=root / "capture")
            exported = json.loads((root / "capture/spec.json").read_text())
            self.assertTrue(exported["captureAtStart"])
            self.assertEqual(exported["frames"], len(track))
            self.assertEqual((session / "input.bin").read_bytes(), track)
            fed = bytearray(len(track))
            for entry in exported["inputs"]:
                for frame in range(entry["first"], entry["last"] + 1):
                    fed[frame - 1] |= entry["buttons"]
            self.assertEqual(bytes(fed), track)
            exported["captureAtStart"] = False
            route = root / "route.json"
            route.write_text(json.dumps({"input": exported}))
            with self.assertRaisesRegex(ValueError, "explicit pre-input snapshots"):
                stand.cmd_scene(SimpleNamespace(route=route, session=session, core=root / "missing"))


if __name__ == "__main__":
    unittest.main()
