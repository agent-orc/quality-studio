#!/usr/bin/env python3
"""Validate Cobertura and lcov coverage reports and hold line coverage at its baseline.

Structural validation always runs. When a baseline is given, the measured line
coverage of the matched reports is compared against the recorded value for that
project; coverage may move up freely and down only within the recorded tolerance.
Raise a baseline with ``--update-baseline`` and commit the changed file.
"""

from __future__ import annotations

import argparse
import glob
import json
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

DEFAULT_TOLERANCE_PERCENTAGE_POINTS = 0.5


class CoverageValidationError(ValueError):
    """Raised when a coverage report is missing, unreadable, or below its baseline."""


def resolve_reports(pattern: str) -> list[Path]:
    reports = sorted(Path(path) for path in glob.glob(pattern, recursive=True))
    if not reports:
        raise CoverageValidationError(f"Coverage report is missing: {pattern}")
    return reports


def require_nonempty(report: Path) -> None:
    if not report.is_file():
        raise CoverageValidationError(f"Coverage report is missing: {report}")
    if report.stat().st_size == 0:
        raise CoverageValidationError(f"Coverage report is empty: {report}")


def parse_cobertura(report: Path) -> ET.Element:
    require_nonempty(report)
    try:
        root = ET.parse(report).getroot()
    except (ET.ParseError, OSError) as error:
        raise CoverageValidationError(
            f"Cobertura report is unreadable XML: {report}: {error}"
        ) from error

    root_name = root.tag.rsplit("}", 1)[-1]
    if root_name != "coverage":
        raise CoverageValidationError(
            f"Cobertura report has an invalid root element '{root_name}': {report}"
        )
    return root


def validate_cobertura(report: Path) -> None:
    parse_cobertura(report)


def measure_cobertura(report: Path) -> tuple[int, int]:
    """Returns (covered lines, total lines) of a Cobertura report."""
    root = parse_cobertura(report)
    covered = root.get("lines-covered")
    valid = root.get("lines-valid")
    if covered is not None and valid is not None:
        return (
            _parse_nonnegative_integer(covered, "lines-covered", report),
            _parse_nonnegative_integer(valid, "lines-valid", report),
        )

    total = 0
    hit = 0
    for line in root.iter():
        if line.tag.rsplit("}", 1)[-1] != "line":
            continue
        total += 1
        if _parse_nonnegative_integer(line.get("hits", "0"), "hits", report) > 0:
            hit += 1
    return hit, total


def _parse_nonnegative_integer(value: str, field: str, report: Path) -> int:
    try:
        parsed = int(value)
    except ValueError as error:
        raise CoverageValidationError(
            f"Coverage report has an invalid {field} value '{value}': {report}"
        ) from error
    if parsed < 0:
        raise CoverageValidationError(
            f"Coverage report has a negative {field} value '{value}': {report}"
        )
    return parsed


def measure_lcov(report: Path) -> tuple[int, int]:
    """Validates an lcov report structurally and returns (covered lines, total lines)."""
    require_nonempty(report)
    try:
        lines = report.read_text(encoding="utf-8").splitlines()
    except (OSError, UnicodeError) as error:
        raise CoverageValidationError(f"lcov report is unreadable: {report}: {error}") from error

    in_record = False
    record_has_data = False
    records = 0
    total = 0
    hit = 0
    for line_number, line in enumerate(lines, start=1):
        if not line:
            continue
        if line == "end_of_record":
            if not in_record:
                raise CoverageValidationError(
                    f"lcov report ends an incomplete record at line {line_number}: {report}"
                )
            if not record_has_data:
                raise CoverageValidationError(
                    f"lcov report has a record without line data at line {line_number}: {report}"
                )
            records += 1
            in_record = False
            record_has_data = False
            continue
        if ":" not in line:
            raise CoverageValidationError(
                f"lcov report has an invalid line {line_number}: {report}"
            )

        field, value = line.split(":", 1)
        if field == "SF":
            if in_record or not value:
                raise CoverageValidationError(
                    f"lcov report has an invalid source record at line {line_number}: {report}"
                )
            in_record = True
        elif field == "DA":
            if not in_record:
                raise CoverageValidationError(
                    f"lcov report has line data before a source at line {line_number}: {report}"
                )
            values = value.split(",")
            if len(values) < 2:
                raise CoverageValidationError(
                    f"lcov report has invalid line data at line {line_number}: {report}"
                )
            source_line = _parse_nonnegative_integer(values[0], "DA line", report)
            hits = _parse_nonnegative_integer(values[1], "DA hit count", report)
            if source_line == 0:
                raise CoverageValidationError(
                    f"lcov report has a zero DA line number at line {line_number}: {report}"
                )
            record_has_data = True
            total += 1
            if hits > 0:
                hit += 1

    if in_record:
        raise CoverageValidationError(f"lcov report has an unterminated record: {report}")
    if records == 0:
        raise CoverageValidationError(f"lcov report contains no readable records: {report}")
    return hit, total


def validate_lcov(report: Path) -> None:
    measure_lcov(report)


def validate(format_name: str, patterns: list[str]) -> list[Path]:
    reports: list[Path] = []
    for pattern in patterns:
        matched = resolve_reports(pattern)
        for report in matched:
            measure(format_name, report)
        reports.extend(matched)
    return reports


def measure(format_name: str, report: Path) -> tuple[int, int]:
    return measure_cobertura(report) if format_name == "cobertura" else measure_lcov(report)


def line_percent(covered: int, total: int) -> float:
    return round(100.0 * covered / total, 2) if total else 0.0


def read_baseline(path: Path) -> dict:
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except OSError as error:
        raise CoverageValidationError(f"Coverage baseline is unreadable: {path}: {error}") from error
    except json.JSONDecodeError as error:
        raise CoverageValidationError(f"Coverage baseline is invalid JSON: {path}: {error}") from error
    if not isinstance(document, dict) or not isinstance(document.get("projects"), dict):
        raise CoverageValidationError(f"Coverage baseline has no 'projects' object: {path}")
    return document


def baseline_tolerance(document: dict) -> float:
    tolerance = document.get("tolerancePercentagePoints", DEFAULT_TOLERANCE_PERCENTAGE_POINTS)
    if not isinstance(tolerance, (int, float)) or tolerance < 0:
        raise CoverageValidationError(
            f"Coverage baseline has an invalid tolerancePercentagePoints value '{tolerance}'"
        )
    return float(tolerance)


def compare_to_baseline(path: Path, key: str, measured: float) -> str:
    document = read_baseline(path)
    project = document["projects"].get(key)
    if not isinstance(project, dict) or not isinstance(project.get("linePercent"), (int, float)):
        raise CoverageValidationError(
            f"Coverage baseline has no linePercent for project '{key}': {path}"
        )

    recorded = float(project["linePercent"])
    tolerance = baseline_tolerance(document)
    floor = round(recorded - tolerance, 2)
    if measured < floor:
        raise CoverageValidationError(
            f"Line coverage of '{key}' fell to {measured:.2f}%, below the baseline "
            f"{recorded:.2f}% minus the {tolerance:.2f} point tolerance ({floor:.2f}%). "
            f"Restore the coverage or, if the drop is intended, lower the baseline in {path} "
            f"in the same commit and say why."
        )
    return (
        f"Line coverage of '{key}' is {measured:.2f}% "
        f"(baseline {recorded:.2f}%, floor {floor:.2f}%)."
    )


def update_baseline(path: Path, key: str, measured: float) -> str:
    document = read_baseline(path)
    project = document["projects"].setdefault(key, {})
    previous = project.get("linePercent")
    project["linePercent"] = measured
    path.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
    return f"Recorded baseline for '{key}': {previous} -> {measured:.2f}%."


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--format", choices=("cobertura", "lcov"), required=True)
    parser.add_argument("--report", action="append", required=True, help="Report path or glob")
    parser.add_argument("--baseline", type=Path, help="Path to the versioned coverage baseline")
    parser.add_argument("--baseline-key", help="Project key inside the baseline file")
    parser.add_argument(
        "--update-baseline",
        action="store_true",
        help="Write the measured coverage into the baseline instead of enforcing it",
    )
    arguments = parser.parse_args()

    if arguments.update_baseline and not (arguments.baseline and arguments.baseline_key):
        parser.error("--update-baseline requires --baseline and --baseline-key")
    if bool(arguments.baseline) != bool(arguments.baseline_key):
        parser.error("--baseline and --baseline-key must be given together")

    try:
        covered = 0
        total = 0
        reports: list[Path] = []
        for pattern in arguments.report:
            for report in resolve_reports(pattern):
                report_covered, report_total = measure(arguments.format, report)
                covered += report_covered
                total += report_total
                reports.append(report)

        for report in reports:
            print(f"Validated {arguments.format} coverage report: {report}")

        if arguments.baseline:
            measured = line_percent(covered, total)
            if arguments.update_baseline:
                print(update_baseline(arguments.baseline, arguments.baseline_key, measured))
            else:
                print(compare_to_baseline(arguments.baseline, arguments.baseline_key, measured))
    except CoverageValidationError as error:
        print(f"::error::{error}", file=sys.stderr)
        return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
