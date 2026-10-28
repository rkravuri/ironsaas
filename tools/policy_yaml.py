#!/usr/bin/env python3
"""Reads the strict subset of YAML that the security policy files use (security/*.yaml, ADR-0055), with Python's
standard library only, like the other tools: mappings and lists by indentation with spaces, plain, single-quoted and
double-quoted scalars, folded (`>`, `>-`) and literal (`|`, `|-`) blocks, `[]` and `{}` for empty collections, and
comments. Anything else (tabs, anchors, tags, flow collections with content, a plain value holding ": ") is refused
with its line, so a policy file never means something other than what it says.

Integers, `true`, `false` and `null` read as Python values; everything else, dates included, as text, which the
reader of each field checks.
"""

from __future__ import annotations

import re
from dataclasses import dataclass

KEY = re.compile(r"^([A-Za-z_][A-Za-z0-9_.-]*):(?:[ ]+(.*))?$")
BLOCK_STYLES = {">", ">-", "|", "|-"}


class PolicyYamlError(ValueError):
    """A line the reader does not accept, with its number."""

    def __init__(self, line: int, message: str) -> None:
        super().__init__(f"line {line}: {message}")
        self.line = line


@dataclass
class _Line:
    number: int
    indent: int
    content: str


def load(text: str) -> object:
    """The document's value: a mapping, a list or a scalar; None for an empty document."""
    return _Parser(text.splitlines()).document()


def _strip_comment(text: str, number: int) -> str:
    """The text before a comment: a '#' that starts the text or follows a space, outside quotes."""
    quote = ""
    index = 0
    while index < len(text):
        character = text[index]
        if quote == "'":
            if character == "'":
                if index + 1 < len(text) and text[index + 1] == "'":
                    index += 1
                else:
                    quote = ""
        elif quote == '"':
            if character == "\\":
                index += 1
            elif character == '"':
                quote = ""
        elif character in "'\"" and (index == 0 or text[index - 1] in " :-[,"):
            quote = character
        elif character == "#" and (index == 0 or text[index - 1] == " "):
            return text[:index].rstrip()
        index += 1
    if quote:
        raise PolicyYamlError(number, "a quoted value is not closed on its line")
    return text.rstrip()


def _scalar(text: str, number: int) -> object:
    if text == "[]":
        return []
    if text == "{}":
        return {}
    if text[0] in "[{&*!%@`|>":
        raise PolicyYamlError(number, f"'{text[0]}' starts a YAML feature the policy files do not use; quote the value")
    if text[0] == '"':
        if len(text) < 2 or not text.endswith('"'):
            raise PolicyYamlError(number, "text after a double-quoted value")
        body = text[1:-1]
        escapes = {'"': '"', "\\": "\\", "n": "\n", "t": "\t", "/": "/"}
        out = []
        index = 0
        while index < len(body):
            if body[index] == "\\":
                if index + 1 >= len(body) or body[index + 1] not in escapes:
                    raise PolicyYamlError(number, "an escape the policy files do not use")
                out.append(escapes[body[index + 1]])
                index += 2
            elif body[index] == '"':
                raise PolicyYamlError(number, "an unescaped quote inside a double-quoted value")
            else:
                out.append(body[index])
                index += 1
        return "".join(out)
    if text[0] == "'":
        if len(text) < 2 or not text.endswith("'"):
            raise PolicyYamlError(number, "text after a single-quoted value")
        body = text[1:-1]
        if re.search(r"(?<!')'(?!')", body.replace("''", "")):
            raise PolicyYamlError(number, "an unescaped quote inside a single-quoted value")
        return body.replace("''", "'")
    if ": " in text or text.endswith(":"):
        raise PolicyYamlError(number, "a plain value holding ': ' reads as a key; quote it")
    if text in ("null", "~"):
        return None
    if text in ("true", "false"):
        return text == "true"
    if re.fullmatch(r"-?[0-9]+", text):
        return int(text)
    return text


class _Parser:
    def __init__(self, lines: list[str]) -> None:
        self.lines = lines
        self.index = 0

    def peek(self) -> _Line | None:
        """The next line that holds something, skipping blank and comment lines."""
        while self.index < len(self.lines):
            raw = self.lines[self.index]
            number = self.index + 1
            stripped = raw.lstrip(" ")
            if stripped.startswith("\t"):
                raise PolicyYamlError(number, "a tab in the indentation; indent with spaces")
            content = _strip_comment(stripped, number)
            if content == "":
                self.index += 1
                continue
            return _Line(number, len(raw) - len(stripped), content)
        return None

    def document(self) -> object:
        first = self.peek()
        if first is None:
            return None
        if first.indent != 0:
            raise PolicyYamlError(first.number, "the document starts indented")
        if first.content == "---":
            self.index += 1
            return self.document()
        value = self.block(0)
        rest = self.peek()
        if rest is not None:
            raise PolicyYamlError(rest.number, "a line outside the document's structure: check its indentation")
        return value

    def block(self, indent: int) -> object:
        line = self.peek()
        assert line is not None
        if line.content == "-" or line.content.startswith("- "):
            return self.sequence(indent)
        if KEY.match(line.content):
            return self.mapping(indent)
        self.index += 1
        return _scalar(line.content, line.number)

    def mapping(self, indent: int, into: dict[str, object] | None = None) -> dict[str, object]:
        result: dict[str, object] = into if into is not None else {}
        while True:
            line = self.peek()
            if line is None or line.indent < indent:
                return result
            if line.indent > indent:
                raise PolicyYamlError(line.number, "indented more than the keys above it")
            if line.content == "-" or line.content.startswith("- "):
                if into is None:
                    raise PolicyYamlError(line.number, "a list item where a key was expected")
                return result
            match = KEY.match(line.content)
            if not match:
                raise PolicyYamlError(line.number, "expected 'key: value'")
            key = match.group(1)
            if key in result:
                raise PolicyYamlError(line.number, f"'{key}' appears twice")
            self.index += 1
            result[key] = self.value(match.group(2), indent, line.number)

    def value(self, rest: str | None, indent: int, number: int) -> object:
        if rest is None or rest == "":
            following = self.peek()
            if following is not None and following.indent > indent:
                return self.block(following.indent)
            if following is not None and following.indent == indent and (following.content == "-" or following.content.startswith("- ")):
                return self.sequence(indent)
            return None
        if rest in BLOCK_STYLES:
            return self.block_scalar(rest, indent)
        return _scalar(rest, number)

    def sequence(self, indent: int) -> list[object]:
        items: list[object] = []
        while True:
            line = self.peek()
            if line is None or line.indent < indent:
                return items
            if line.indent > indent:
                raise PolicyYamlError(line.number, "indented more than the list items above it")
            if not (line.content == "-" or line.content.startswith("- ")):
                return items
            self.index += 1
            rest = line.content[1:].lstrip(" ")
            item_indent = line.indent + len(line.content) - len(rest)
            if rest == "":
                items.append(self.value(None, indent, line.number))
                continue
            match = KEY.match(rest)
            if match:
                first = {match.group(1): self.value(match.group(2), item_indent, line.number)}
                items.append(self.mapping(item_indent, first))
            else:
                items.append(_scalar(rest, line.number))

    def block_scalar(self, style: str, indent: int) -> str:
        collected: list[str] = []
        block_indent: int | None = None
        while self.index < len(self.lines):
            raw = self.lines[self.index]
            if raw.strip() == "":
                collected.append("")
                self.index += 1
                continue
            current = len(raw) - len(raw.lstrip(" "))
            if current <= indent:
                break
            if block_indent is None:
                block_indent = current
            elif current < block_indent:
                raise PolicyYamlError(self.index + 1, "a block's line indented less than its first line")
            collected.append(raw[block_indent:].rstrip())
            self.index += 1
        while collected and collected[-1] == "":
            collected.pop()
        if style.startswith(">"):
            paragraphs: list[str] = []
            current_words: list[str] = []
            for text in collected:
                if text == "":
                    paragraphs.append(" ".join(current_words))
                    current_words = []
                else:
                    current_words.append(text.strip())
            paragraphs.append(" ".join(current_words))
            value = "\n".join(paragraphs)
        else:
            value = "\n".join(collected)
        return value if style.endswith("-") else value + "\n"
