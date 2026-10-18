# ADR-0024: The design language

Date: 2026-10-18
Status: Accepted

## Context

TF-171 asks for a component library on Mantine, TF-172 for WCAG 2.1 AA, TF-173 for tenant theming within contrast rules. The owner's direction goes further: enterprise software is bland, and this one must not be. Every page should look like a work of care, with line drawings, illustrations, icons, colour and ornament used with judgement, and it must stay simple to use for people who spend their day in it. The design language is decided now, before the S2 screens exist, so that they are built on it rather than restyled later.

## Decision

1. **Calm centre, living edges.** The working area of a page, where people read, enter and decide, is calm, dense and consistent; the art lives at the edges and in the moments: the login, the home page, section openers, empty states, loading, success, errors, onboarding, and the frames of the layout. Ornament never competes with data.
2. **One drawn world.** Illustrations are original line drawings in one hand: a consistent stroke, a restrained palette, a little humour, people and objects of the business world drawn with warmth. They are SVG, drawn in code, versioned in the repository, and never traced from or imitating a known artist, character or brand.
3. **Ornament from geometry.** Mandala and pattern work is generated from parameters (symmetry, rings, strokes) in the repository's own generator, so every tenant, module or page can have its own pattern that is still one family. Patterns are used as texture, dividers and markers, at low contrast, never behind text.
4. **Colour with intent.** A small system palette: one ink, one paper, a set of accents each with a meaning (module identity, status, interaction), and tenant colours within the contrast rules of TF-173. Status is never colour alone (TF-172).
5. **Type as the voice.** One text family for reading and one display family for the moments, with a scale of a few sizes; generous spacing in the moments, tight in the work.
6. **Motion that explains.** Short transitions that show where things came from and went; nothing decorative in motion; reduced-motion preference honoured.
7. **Usability is not negotiable.** Keyboard first, visible focus, labels on everything, AA contrast, a density switch for the grid, the same placement of the same action on every page. The automated accessibility scan runs on the component gallery in CI (TF-172).
8. **Products get the language through the library**, not by styling: page templates (list, record, wizard, dashboard, console), the panels of TF-171, the illustration and pattern libraries, and the theme tokens. The lint rule of ADR-0021 extends to raw colours and ad-hoc styling in product code.

## Consequences

- The design system is a stream of S2 with its own tasks, ahead of the screens that use it.
- Drawing in code means Claude Code can produce and revise the art; the brief in `docs/design/design-language.md` is what keeps the hand consistent.
- Every tenant can look like itself within the rules; the platform never looks like a spreadsheet.
