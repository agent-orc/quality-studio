from __future__ import annotations

import importlib.util
import json
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).with_name("validate_coverage.py")
SPEC = importlib.util.spec_from_file_location("validate_coverage", SCRIPT)
assert SPEC and SPEC.loader
validate_coverage = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(validate_coverage)


class report:
    """Writes report contents to a throwaway file and yields its path."""

    def __init__(self, contents: str):
        self.contents = contents
        self.temporary_directory: tempfile.TemporaryDirectory[str] | None = None

    def __enter__(self) -> Path:
        self.temporary_directory = tempfile.TemporaryDirectory()
        path = Path(self.temporary_directory.name) / "coverage-report"
        path.write_text(self.contents, encoding="utf-8")
        return path

    def __exit__(self, *_: object) -> None:
        assert self.temporary_directory
        self.temporary_directory.cleanup()


class baseline:
    """Writes a coverage baseline with a single 'api' project and yields its path."""

    def __init__(self, line_percent: float, tolerance: float):
        self.line_percent = line_percent
        self.tolerance = tolerance
        self.temporary_directory: tempfile.TemporaryDirectory[str] | None = None

    def __enter__(self) -> Path:
        self.temporary_directory = tempfile.TemporaryDirectory()
        path = Path(self.temporary_directory.name) / "coverage-baseline.json"
        path.write_text(
            json.dumps(
                {
                    "tolerancePercentagePoints": self.tolerance,
                    "projects": {"api": {"linePercent": self.line_percent}},
                }
            ),
            encoding="utf-8",
        )
        return path

    def __exit__(self, *_: object) -> None:
        assert self.temporary_directory
        self.temporary_directory.cleanup()


class CoverageValidationTests(unittest.TestCase):
    def test_valid_cobertura_report_parses(self) -> None:
        with report("<coverage><packages /></coverage>") as path:
            validate_coverage.validate_cobertura(path)

    def test_malformed_cobertura_report_fails(self) -> None:
        with report("<coverage>") as path:
            with self.assertRaisesRegex(
                validate_coverage.CoverageValidationError, "unreadable XML"
            ):
                validate_coverage.validate_cobertura(path)

    def test_valid_lcov_report_parses(self) -> None:
        with report("TN:\nSF:src/app.ts\nDA:1,1\nend_of_record\n") as path:
            validate_coverage.validate_lcov(path)

    def test_empty_report_fails(self) -> None:
        with report("") as path:
            with self.assertRaisesRegex(validate_coverage.CoverageValidationError, "empty"):
                validate_coverage.validate_lcov(path)

    def test_missing_report_pattern_fails(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            pattern = str(Path(directory) / "**" / "coverage.cobertura.xml")
            with self.assertRaisesRegex(validate_coverage.CoverageValidationError, "missing"):
                validate_coverage.resolve_reports(pattern)


class CoverageMeasurementTests(unittest.TestCase):
    def test_cobertura_summary_attributes_are_used(self) -> None:
        with report('<coverage lines-covered="30" lines-valid="40"><packages /></coverage>') as path:
            self.assertEqual((30, 40), validate_coverage.measure_cobertura(path))

    def test_cobertura_falls_back_to_counting_lines(self) -> None:
        document = (
            "<coverage><packages><package><classes><class><lines>"
            '<line number="1" hits="2" /><line number="2" hits="0" />'
            "</lines></class></classes></package></packages></coverage>"
        )
        with report(document) as path:
            self.assertEqual((1, 2), validate_coverage.measure_cobertura(path))

    def test_lcov_counts_hit_and_total_lines(self) -> None:
        with report("SF:src/app.ts\nDA:1,1\nDA:2,0\nDA:3,4\nend_of_record\n") as path:
            self.assertEqual((2, 3), validate_coverage.measure_lcov(path))

    def test_line_percent_rounds_and_tolerates_an_empty_report(self) -> None:
        self.assertEqual(0.0, validate_coverage.line_percent(0, 0))
        self.assertEqual(66.67, validate_coverage.line_percent(2, 3))


class CoverageBaselineTests(unittest.TestCase):
    def test_coverage_at_the_baseline_passes(self) -> None:
        with baseline(70.0, tolerance=0.5) as path:
            self.assertIn("70.00%", validate_coverage.compare_to_baseline(path, "api", 70.0))

    def test_coverage_inside_the_tolerance_passes(self) -> None:
        with baseline(70.0, tolerance=0.5) as path:
            validate_coverage.compare_to_baseline(path, "api", 69.5)

    def test_coverage_below_the_tolerance_fails(self) -> None:
        with baseline(70.0, tolerance=0.5) as path:
            with self.assertRaisesRegex(
                validate_coverage.CoverageValidationError, "below the baseline"
            ):
                validate_coverage.compare_to_baseline(path, "api", 69.49)

    def test_rising_coverage_passes(self) -> None:
        with baseline(70.0, tolerance=0.5) as path:
            validate_coverage.compare_to_baseline(path, "api", 91.0)

    def test_unknown_project_fails(self) -> None:
        with baseline(70.0, tolerance=0.5) as path:
            with self.assertRaisesRegex(
                validate_coverage.CoverageValidationError, "no linePercent for project"
            ):
                validate_coverage.compare_to_baseline(path, "frontend", 90.0)

    def test_baseline_without_projects_fails(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "baseline.json"
            path.write_text("{}", encoding="utf-8")
            with self.assertRaisesRegex(
                validate_coverage.CoverageValidationError, "no 'projects' object"
            ):
                validate_coverage.read_baseline(path)

    def test_update_baseline_records_the_measured_value(self) -> None:
        with baseline(70.0, tolerance=0.5) as path:
            validate_coverage.update_baseline(path, "api", 74.25)
            document = json.loads(path.read_text(encoding="utf-8"))
            self.assertEqual(74.25, document["projects"]["api"]["linePercent"])


class CommittedBaselineTests(unittest.TestCase):
    def test_the_repository_baseline_covers_every_measured_project(self) -> None:
        path = Path(__file__).resolve().parents[2] / "tests" / "coverage-baseline.json"
        document = validate_coverage.read_baseline(path)
        self.assertGreater(validate_coverage.baseline_tolerance(document), 0)
        self.assertEqual(
            {
                "AgentOrchestrator.CodeQuality.Tests",
                "QualityStudio.Api.Tests",
                "frontend",
            },
            set(document["projects"]),
        )


if __name__ == "__main__":
    unittest.main()
