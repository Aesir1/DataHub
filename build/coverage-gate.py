#!/usr/bin/env python3
"""TE-4: fail when line coverage of the gated assemblies is below the threshold; report the rest.

Usage: coverage-gate.py <threshold> <gated,assemblies> <cobertura.xml>...
"""
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict


def main() -> int:
    threshold, gated, files = float(sys.argv[1]), set(filter(None, sys.argv[2].split(","))), sys.argv[3:]
    lines: dict[str, dict[tuple[str, int], bool]] = defaultdict(dict)
    for path in files:
        for package in ET.parse(path).getroot().iter("package"):
            name = package.get("name", "")
            for cls in package.iter("class"):
                filename = cls.get("filename", "")
                for line in cls.iter("line"):
                    key = (filename, int(line.get("number", "0")))
                    # A line counts as covered if any test run covered it.
                    lines[name][key] = lines[name].get(key, False) or int(line.get("hits", "0")) > 0

    failed = False
    for name in sorted(lines):
        if not name.startswith("DataHub.") or ".Tests" in name:
            continue
        total = len(lines[name])
        covered = sum(lines[name].values())
        rate = covered / total if total else 1.0
        gate = name in gated
        status = "OK" if not gate or rate >= threshold else "FAIL"
        failed |= status == "FAIL"
        print(f"{status:4} {name:32} {rate:7.1%}  ({covered}/{total}){'  [gated]' if gate else ''}")

    missing = gated - set(lines)
    if missing:
        print(f"FAIL no coverage data for {', '.join(sorted(missing))}")
        failed = True
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
