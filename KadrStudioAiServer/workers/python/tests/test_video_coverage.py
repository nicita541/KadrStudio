import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from kadr_worker import analyzers


class VideoCoverageTests(unittest.TestCase):
    def analyze(self, timestamps, cancel=None):
        class Capture:
            index = -1
            released = False

            def isOpened(self):
                return True

            def read(self):
                self.index += 1
                return (True, object()) if self.index < len(timestamps) else (False, None)

            def get(self, key):
                return {1: 24, 2: 240, 3: timestamps[self.index] * 1000 if self.index >= 0 else 0}[key]

            def release(self):
                self.released = True

        self.capture = Capture()
        cv = SimpleNamespace(VideoCapture=lambda _: self.capture, CAP_PROP_FPS=1,
                             CAP_PROP_FRAME_COUNT=2, CAP_PROP_POS_MSEC=3,
                             COLOR_BGR2GRAY=4, HISTCMP_BHATTACHARYYA=5,
                             cvtColor=lambda *args: 0, calcHist=lambda *args: 0,
                             normalize=lambda *args: None, compareHist=lambda *args: 0,
                             absdiff=lambda *args: 0)
        asset = SimpleNamespace(kind="visual-proxy", path=Path(__file__))
        parameters = {"sourceId": "source", "sourceFingerprint": "fingerprint",
                      "sourceDurationTicks": 10 * analyzers.TICKS_PER_SECOND,
                      "gaps": [{"channel": "frames", "startTicks": 0,
                                "durationTicks": 10 * analyzers.TICKS_PER_SECOND}]}
        with patch.dict("sys.modules", {"cv2": cv, "numpy": SimpleNamespace(mean=lambda _: 0)}):
            return analyzers.analyze_video([asset], parameters, cancel)

    def test_early_eof_does_not_claim_requested_duration_or_invent_samples(self):
        result = self.analyze([0, 1 / 24, 2 / 24])
        coverage = result["coverage"]["channels"][str(analyzers.CHANNEL_FRAMES)]
        self.assertEqual(3, sum(item["sampleCount"] for item in coverage))
        self.assertLessEqual(max(item["range"]["start"]["ticks"] + item["range"]["duration"]["ticks"]
                                 for item in coverage), analyzers.TICKS_PER_SECOND // 8)

    def test_empty_decoder_has_no_measured_coverage_or_shots(self):
        result = self.analyze([])
        self.assertEqual([], result["coverage"]["channels"][str(analyzers.CHANNEL_FRAMES)])
        self.assertEqual([], result["shots"])

    def test_pts_gap_is_not_reported_as_continuously_measured(self):
        result = self.analyze([0, 1 / 24, 5])
        coverage = result["coverage"]["channels"][str(analyzers.CHANNEL_FRAMES)]
        midpoint = 2 * analyzers.TICKS_PER_SECOND
        self.assertFalse(any(item["range"]["start"]["ticks"] <= midpoint <
                             item["range"]["start"]["ticks"] + item["range"]["duration"]["ticks"]
                             for item in coverage))

    def test_missing_monotonic_pts_cannot_be_promoted_to_dense_evidence(self):
        result = self.analyze([0, 0, 0])
        coverage = result["coverage"]["channels"][str(analyzers.CHANNEL_FRAMES)]
        self.assertEqual(1, sum(item["sampleCount"] for item in coverage))

    def test_cancellation_releases_the_decoder(self):
        calls = iter(range(10))
        with self.assertRaises(analyzers.JobCancelled):
            self.analyze([0, 1 / 24, 2 / 24], cancel=lambda: next(calls) >= 2)
        self.assertTrue(self.capture.released)


if __name__ == "__main__":
    unittest.main()
