"""Detector controls: python -m unittest discover -s Art/Tools/DarkwingResearch/stand -p test_health.py"""
import importlib.util
from contextlib import redirect_stdout
import io
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest

import stand


class HealthControls(unittest.TestCase):
    def setUp(self):
        self.raw = bytearray(stand.BUFFER_STRIDE * 10)
        for i in range(10):
            base = i * stand.BUFFER_STRIDE
            self.raw[base + 0x5B0:base + 0x5B2] = b"\x07\x24"
            for offset, value in ((0x5B2, 0x80), (0x5B4, 0x80), (0x5B6, 0x81), (0x5B8, 0x81)):
                self.raw[base + offset] = value

    def test_identical(self):
        self.assertTrue(all(v is None for v in stand.compare_health(self.raw, self.raw)
                            ["firstDifferencesAfterBothHudsInstalled"].values()))

    def test_heart_edit(self):
        edited = bytearray(self.raw)
        edited[8 * stand.BUFFER_STRIDE + 0x5B4] = 0x82
        report = stand.compare_health(self.raw, edited)
        self.assertEqual(report["firstDifferencesAfterBothHudsInstalled"]["healthTiles"]["poll"], 9)
        self.assertTrue(any(e["event"] == "health" and e["poll"] == 9 and e["damageAfter"] == 1
                            for e in report["events"]["port"]))

    def test_missing_medkit(self):
        edited = bytearray(self.raw)
        base = 8 * stand.BUFFER_STRIDE
        edited[base + 0x4D2], edited[base + 0x422] = 0x80, 0x23
        report = stand.compare_health(self.raw, edited)
        self.assertEqual(report["firstDifferencesAfterBothHudsInstalled"]["healthItems"]["poll"], 9)
        events = report["events"]["port"]
        self.assertTrue(any(e["event"] == "itemAppears" and e["poll"] == 9 for e in events))
        self.assertTrue(any(e["event"] == "itemDisappears" and e["poll"] == 10 for e in events))

    def test_bad_lengths(self):
        for a, b in ((b"", b""), (self.raw, self.raw[:-1]), (self.raw[:-1], self.raw[:-1]),
                     (self.raw, self.raw[:-stand.BUFFER_STRIDE])):
            with self.subTest(lengths=(len(a), len(b))), self.assertRaises(ValueError):
                stand.compare_health(a, b)

    def test_rng_without_heart_change(self):
        edited = bytearray(self.raw)
        edited[8 * stand.BUFFER_STRIDE + 0xE4] = 1
        first = stand.compare_health(self.raw, edited)["firstDifferencesAfterBothHudsInstalled"]
        self.assertEqual(first["rng"]["poll"], 9)
        self.assertIsNone(first["healthTiles"])

    def test_report_line_endings_and_existing_output(self):
        temp_root = Path("Art/Temp").resolve()
        assert temp_root.is_relative_to(Path.cwd().resolve())
        with tempfile.TemporaryDirectory(dir=temp_root) as directory:
            root = Path(directory)
            source, output = root / "buffers.bin", root / "health.json"
            source.write_bytes(self.raw)
            args = SimpleNamespace(original=source, port=source, start=8, output=output)
            with redirect_stdout(io.StringIO()):
                stand.cmd_health(args)
            self.assertNotIn(b"\r", output.read_bytes())
            with self.assertRaises(ValueError):
                stand.cmd_health(args)

    def test_framebuffer_roundtrip_and_truncation(self):
        spec = importlib.util.spec_from_file_location("compare_frames", Path(stand.__file__).parents[1] / "compare_frames.py")
        compare = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(compare)
        temp_root = Path("Art/Temp").resolve()
        assert temp_root.is_relative_to(Path.cwd().resolve())
        with tempfile.TemporaryDirectory(dir=temp_root) as directory:
            source, output = Path(directory) / "frame.indices", Path(directory) / "frame.png"
            source.write_bytes(bytes(range(64)) * 960)
            compare.write_frame_png(source, output)
            self.assertEqual(compare.read_frame_rgb(source), compare.read_png_rgb(output))
            with self.assertRaises(ValueError):
                compare.write_frame_png(source, output)
            for data in (b"", b"\0" * 61439, b"\0" * 61441, b"\x40" * 61440):
                source.write_bytes(data)
                with self.assertRaises(ValueError):
                    compare.read_frame_rgb(source)


if __name__ == "__main__":
    unittest.main()
