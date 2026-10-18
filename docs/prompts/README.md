# Instruction files for Claude Code

One file per slice (ADR-0023). Each section is one task and opens with the opening message that is pasted into Claude Code, followed by the instructions the task follows and the checklist the review uses. `CLAUDE.md` at the repository root holds the standing rules and the "When to stop" rule; `.claude/rules/` holds the conventions of each side; `docs/STATUS.md` is the running state.

How a session goes:

1. Fresh Claude Code session in `~/foundation` inside WSL, clean tree on `main`. Paste the section's opening message.
2. Claude Code makes the branch, commits the prompt, writes a short plan into the session, builds, tests, documents, keeps STATUS current, and stops with a report and screenshots.
3. The report, the diff summary and the screenshots go to the design session. The answer is one paste-ready message: accept and merge, or stop and adjust with numbered corrections.
4. When merged, the task's row in STATUS and the slice plan is marked done, and the next section's opening message is pasted.

The sections are sharpened as the slice advances; the next task's section is complete, later ones are outlines.
