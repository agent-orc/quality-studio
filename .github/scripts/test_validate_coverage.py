from __future__ import annotations

import importlib.util
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).with_name("validate_coverage.py")
SPEC = importlib.util.spec_from_file_location("validate_coverage", SCRIPT)
assert SPEC and SPEC.loader
validate_coverage = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(validate_coverage)


class CoverageValidationTests(unittest.TestCase):
    def test_valid_cobertura_report_parses(self) -> None:
        with self.report("<coverage><packages /></coverage>") as report:
            validate_coverage.validate_cobertura(report)

    def test_malformed_cobertura_report_fails(self) -> None:
        with self.report("<coverage>") as report:
            with self.assertRaisesRegex(
                validate_coverage.CoverageValidationError, "unreadable XML"
            ):
                validate_coverage.validate_cobertura(report)

    def test_valid_lcov_report_parses(self) -> None:
        with self.report("TN:\nSF:src/app.ts\nDA:1,1\nend_of_record\n") as report:
            validate_coverage.validate_lcov(report)

    def test_empty_report_fails(self) -> None:
        with self.report("") as report:
            with self.assertRaisesRegex(validate_coverage.CoverageValidationError, "empty"):
                validate_coverage.validate_lcov(report)

    def test_missing_report_pattern_fails(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            pattern = str(Path(directory) / "**" / "coverage.cobertura.xml")
            with self.assertRaisesRegex(validate_coverage.CoverageValidationError, "missing"):
                validate_coverage.resolve_reports(pattern)

    class report:
        def __init__(self, contents: str):
            self.contents = contents
            self.temporary_directory: tempfile.TemporaryDirectory[str] | None = None

        def __enter__(self) -> Path:
            self.temporary_directory = tempfile.TemporaryDirectory()
            report = Path(self.temporary_directory.name) / "coverage-report"
            report.write_text(self.contents, encoding="utf-8")
            return report

        def __exit__(self, *_: object) -> None:
            assert self.temporary_directory
            self.temporary_directory.cleanup()


if __name__ == "__main__":
    unittest.main()
