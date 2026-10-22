import importlib.util
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
