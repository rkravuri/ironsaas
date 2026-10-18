# Instruction files for Claude Code

One file per slice (ADR-0023). Each section is one task and opens with the prompt that is pasted into Claude Code, followed by the instructions the task follows and the checklist the review uses. `CLAUDE.md` at the repository root holds the standing rules; a section never repeats them.

How a session goes:

1. Paste the section's prompt into Claude Code in the repository.
2. Claude Code reads the section, the ADRs it names and the code, builds, tests, writes the documentation and reports.
3. The report and the diff are reviewed in the design session; fixes and enhancements become the next prompt, in the same section under "Follow-ups".
4. When the section's "done when" holds on the laptop, the task's row in the slice plan is marked done.

The sections are sharpened as the slice advances; a section for a task three steps ahead is an outline, the next task's section is complete.
