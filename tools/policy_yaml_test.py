#!/usr/bin/env python3
"""Proves tools/policy_yaml.py (ADR-0055): the subset of YAML the security policy files use reads as YAML does, and
everything outside it is refused with its line. Each test's docstring ends with what it proves. Standard library only;
`make trace` runs it first, with `--junit`."""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.dont_write_bytecode = True  # no __pycache__ beside the tools
sys.path.insert(0, str(Path(__file__).resolve().parent))
import policy_yaml  # noqa: E402

DOCUMENT = """\
# A comment line.
exceptions:
  - finding: CVE-2031-0001   # a comment after a value
    package: "@scope/name"
    reason: >-
      A folded reason
      over two lines.
    approved: 2031-01-10
    count: 3
  - finding: 'it''s quoted'
    package: lightningcss*
    notes: |
      kept
      as lines
allowed:
- MIT
- Apache-2.0
empty: []
none:
flag: true
"""


class PolicyYamlTest(unittest.TestCase):
    def test_the_policy_subset_reads_as_yaml_does(self) -> None:
        """Mappings, lists at either indentation, quoted and plain scalars, folded and literal blocks, comments and empty values. [ADR-0055]"""
        self.assertEqual(policy_yaml.load(DOCUMENT), {
            "exceptions": [
                {"finding": "CVE-2031-0001", "package": "@scope/name", "reason": "A folded reason over two lines.", "approved": "2031-01-10", "count": 3},
                {"finding": "it's quoted", "package": "lightningcss*", "notes": "kept\nas lines\n"},
            ],
            "allowed": ["MIT", "Apache-2.0"],
            "empty": [],
            "none": None,
            "flag": True,
        })

    def test_what_the_subset_leaves_out_is_refused_with_its_line(self) -> None:
        """Tabs, anchors, flow collections, a plain value holding ': ', a repeated key and a stray indentation are refused, naming the line. [ADR-0055]"""
        cases = {
            "a:\n\tb: c\n": (2, "tab"),
            "a: &anchor value\n": (1, "starts a YAML feature"),
            "a: [b, c]\n": (1, "starts a YAML feature"),
            "a: b: c\n": (1, "reads as a key"),
            "a: 1\na: 2\n": (2, "appears twice"),
            "a:\n  b: 1\n    c: 2\n": (3, "indented more"),
            "a: 'open\n": (1, "not closed"),
        }
        for text, (line, words) in cases.items():
            with self.subTest(text=text):
                with self.assertRaises(policy_yaml.PolicyYamlError) as raised:
                    policy_yaml.load(text)
                self.assertEqual(raised.exception.line, line)
                self.assertIn(words, str(raised.exception))


if __name__ == "__main__":
    import unittest_junit

    sys.exit(unittest_junit.main(sys.modules[__name__], "policy_yaml_test"))
