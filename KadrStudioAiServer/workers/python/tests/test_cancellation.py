from __future__ import annotations

import sys
import time
import unittest
from pathlib import Path

from kadr_worker import analyzers


class CancellationTests(unittest.TestCase):
    def test_run_analyzer_refuses_dispatch_after_grpc_cancellation(self) -> None:
        caught: BaseException | None = None
        try:
            analyzers.run_analyzer(
                "audio-events", [], {}, Path.cwd(), cancel=lambda: True)
        except BaseException as exception:  # assert the cancellation contract below
            caught = exception

        self.assertIsNotNone(caught)
        self.assertEqual("JobCancelled", type(caught).__name__)

    def test_blocking_subprocess_is_terminated_when_job_is_cancelled(self) -> None:
        started = time.monotonic()
        caught: BaseException | None = None
        try:
            analyzers._run_process_cancellable(
                [sys.executable, "-c", "import time; time.sleep(30)"],
                cancel=lambda: time.monotonic() - started >= 0.2,
            )
        except BaseException as exception:  # assert the cancellation contract below
            caught = exception

        self.assertIsNotNone(caught)
        self.assertEqual("JobCancelled", type(caught).__name__)
        self.assertLess(time.monotonic() - started, 5)


if __name__ == "__main__":
    unittest.main()
