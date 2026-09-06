#!/usr/bin/env python3
"""Validate generated Cobertura and lcov coverage reports without thresholds."""

from __future__ import annotations

import argparse
import glob
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


class CoverageValidationError(ValueError):
    """Raised when a coverage report is missing or unreadable."""


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


def validate_cobertura(report: Path) -> None:
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


def _parse_nonnegative_integer(value: str, field: str, report: Path) -> int:
    try:
        parsed = int(value)
    except ValueError as error:
        raise CoverageValidationError(
            f"lcov report has an invalid {field} value '{value}': {report}"
        ) from error
    if parsed < 0:
        raise CoverageValidationError(
            f"lcov report has a negative {field} value '{value}': {report}"
        )
    return parsed


def validate_lcov(report: Path) -> None:
    require_nonempty(report)
    try:
        lines = report.read_text(encoding="utf-8").splitlines()
    except (OSError, UnicodeError) as error:
        raise CoverageValidationError(f"lcov report is unreadable: {report}: {error}") from error

    in_record = False
    record_has_data = False
    records = 0
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
            _parse_nonnegative_integer(values[1], "DA hit count", report)
            if source_line == 0:
                raise CoverageValidationError(
                    f"lcov report has a zero DA line number at line {line_number}: {report}"
                )
            record_has_data = True

    if in_record:
        raise CoverageValidationError(f"lcov report has an unterminated record: {report}")
    if records == 0:
        raise CoverageValidationError(f"lcov report contains no readable records: {report}")


def validate(format_name: str, patterns: list[str]) -> list[Path]:
    validator = validate_cobertura if format_name == "cobertura" else validate_lcov
    reports: list[Path] = []
    for pattern in patterns:
        matched = resolve_reports(pattern)
        for report in matched:
            validator(report)
        reports.extend(matched)
    return reports


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--format", choices=("cobertura", "lcov"), required=True)
    parser.add_argument("--report", action="append", required=True, help="Report path or glob")
    arguments = parser.parse_args()

    try:
        reports = validate(arguments.format, arguments.report)
    except CoverageValidationError as error:
        print(f"::error::{error}", file=sys.stderr)
        return 1

    for report in reports:
        print(f"Validated {arguments.format} coverage report: {report}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
