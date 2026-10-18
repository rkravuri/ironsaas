# ADR-0026: Illustrations and patterns: the generator, the hand and the review rule

Date: 2026-10-18
Status: Accepted

## Context

ADR-0024 decided one drawn world (item 2) and ornament from geometry (item 3), and the brief (sections 3 and 4) says what the hand and the subjects are. The slice plan asks D2 to record the illustration review rule as a decision. The drawings are code in the repository, so the rule must be checkable in a pull request and, where it can be, by a test.

## Decision

1. **The hand lives in code.** `shell/src/design/illustrations/hand.ts` holds the numbers (stroke 2, detail 1, a 160 by 120 box, the accent tint at 16 percent) and `Drawing.tsx` applies them: every illustration is a component that draws inside the `Drawing` frame with the `Tint`, `Fine` and `Ground` helpers. The frame gives `role="img"` with the catalogue's label, or `aria-hidden` when the drawing is decoration beside its own words, and one of three named widths (sm 120, md 200, lg 280 pixels) so pages place drawings consistently. A test mounts every drawing and checks the name, the hidden form, the sizes and the hand.
2. **Colours come from the variables.** The ink is `--iron-ink` and the accent `--iron-accent-primary` by default; a moment may pass another accent. Fills are the accent at low opacity, so one drawing works on both papers without a second version.
3. **The generator is pure and memoised.** `patterns/mandala.ts` turns symmetry (6, 8 or 12), rings (2 to 6), stroke, density and a seed into path data, deterministically, through a stable hash of the seed and a small seeded stream; the same inputs hand back the same object from a bounded cache, and the component memoises the call. A tenant's mark seeds from its hostname, so every tenant has something of its own with no configuration; TF-173 adds its colours later.
4. **Motion draws, never spins.** A pattern or a loader draws itself in along its stroke (`pathLength` 1 and one dash offset keyframe) over the `draw` duration token; the component skips the animation when the viewer asked for less motion, and the global rule cuts it besides.
5. **The review rule.** `docs/design/illustration-checklist.md` is the gate: one hand, the fills, originality, the subject rules, the accessible name or hidden, both schemes, reduced motion, patterns never behind text, and the proof in the gallery, the test and the scan. The definition of done points at it; a pull request with a drawing answers it line by line.

## Consequences

- A new drawing is one component in the brief's hand, with its label in the catalogue and its tile in the gallery; the test and the scan catch a missing name, a wrong stroke or a contrast fault before review.
- Every tenant looks like itself on the login page and in the header from the first provisioning; nothing else about tenant branding exists yet.
- Options not taken: an icon or illustration library (not original, not one hand); SVG files as assets (no tokens, no accessible name, a second colour per scheme); hand-written shade tuples for patterns (the generator keeps them one family).
