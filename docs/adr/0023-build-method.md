# ADR-0023: The build method from S2: instruction files for Claude Code, review in the design session

Date: 2026-10-18 (revised the same day, after the first D1 session)
Status: Accepted

## Context

S1 was built in one conversation: the code was written in the design session, packaged, run on the laptop, and fixed from the output. That worked for a skeleton of nineteen tasks, but it put the unzipping, running and pasting on the owner through many fix rounds. From S2 the coding moves to Claude Code, an agent that works in the repository with the build and the tests at hand, while the design session keeps the thinking: what to build, in what order, what "done" means, and the review of what came back. The method follows the one that worked on an earlier project: the agent owns its branch and its commits, proceeds without waiting unless a stop rule applies, and the design session speaks in paste-ready messages.

## Decision

1. **One instruction file per slice** in `docs/prompts/`, named after the slice (`S2.md`). It is a living document: sections are added or sharpened as the slice advances, one task per section. Each section opens with the opening message for Claude Code: what to read (`CLAUDE.md`, the rules, `docs/STATUS.md`, the ADRs and brief it needs, the requirement rows, the code it builds on, then the section itself), and the standing instruction: commit the prompt alone first, write a short plan into the session, and build without waiting for approval unless the "When to stop" rule in `CLAUDE.md` applies.
2. **A branch per task.** Claude Code works on `slice/<task>` (for example `slice/s2-d1`), commits as it goes with messages that say why, keeps `docs/STATUS.md` current, and finishes with the checks the task names, a report, and screenshots where there is a page. It never merges.
3. **Review in the design session.** The report, `git diff --stat` and the screenshots come here. The answer is one paste-ready message: "Accepted" with the answers to its questions and the merge instruction (switch to main, merge with a merge commit, run the full checks on main, delete the branch, confirm the checkout), or "Stop and adjust" with numbered corrections, or the next task's opening message.
4. **`CLAUDE.md` holds the standing rules**, kept short; `.claude/rules/backend.md` and `.claude/rules/frontend.md` hold the conventions of each side; `docs/STATUS.md` is the running state of the slice (done, in progress, next, open questions, later items); `docs/licences.md` records every font, image or dataset licence.
5. **The "When to stop" rule** is in `CLAUDE.md`: a decision with real alternatives that no ADR covers; a destructive action on data, a branch or a secret; a test the agent cannot make pass in two attempts; access it does not have; work outside the section's scope. Everything else proceeds.
6. **The definition of done is the same** (`docs/guides/definition-of-done.md`).

## Consequences

- The owner's work per task is three pastes: the opening message, the review answer, and the next opening message; plus looking at the pages.
- History is in git, written by the agent that did the work, with the prompt committed first so the intent sits next to the result.
- The design session's output is instructions and reviews, short and complete.
