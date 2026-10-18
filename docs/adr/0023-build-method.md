# ADR-0023: The build method from S2: instruction files for Claude Code, review in the design session

Date: 2026-10-18
Status: Accepted

## Context

S1 was built in one conversation: the code was written in the design session, packaged, run on the laptop, and fixed from the output. That worked for a skeleton of nineteen tasks and taught a lot, but it kept the design session busy with files and zips. From S2 the coding moves to Claude Code, an agent that works in the repository with the build and the tests at hand, while the design session keeps the thinking: what to build, in what order, what "done" means, and the review of what came back.

## Decision

1. **One instruction file per slice** in `docs/prompts/`, named after the slice (`S2.md`). It is a living document: sections are added or sharpened as the slice advances, one task per section.
2. **Every section opens with the prompt.** A short block, five to ten lines, that is pasted into Claude Code as is. It names the task, points at the section, and states the done-when. The rest of the section is the instructions Claude Code follows: what to build and where, the decisions already taken (with their ADRs), the tests that prove it, the documentation to update, and what not to do.
3. **`CLAUDE.md` at the repository root** holds the standing rules Claude Code reads on every run: the commands, the conventions, the boundaries the build enforces, the writing rules, and where the guides are. The instruction file for a task never repeats what `CLAUDE.md` says.
4. **Review here.** Claude Code's result comes back to the design session as a diff summary and the test output; fixes and enhancements are worked out together and go back as the next prompt. The design session does not write the code; it writes the next instruction.
5. **The definition of done is the same** (`docs/guides/definition-of-done.md`): tests at the level that proves the change, an ADR for a decision, documentation where behaviour changed. A task's section ends with its review checklist.

## Consequences

- The design session's output becomes instructions and reviews, which are short, and the repository holds the code; the slip between "what we agreed" and "what is in git" that S1 had with its zips disappears.
- Claude Code can run `make test`, `make shell-check` and the smoke itself, so a task comes back proven rather than written.
- The instruction files are documentation too: they say why each thing was built, next to the ADR that decided it.
