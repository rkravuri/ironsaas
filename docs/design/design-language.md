# The design language

The brief that keeps every page in one hand (ADR-0024). Read it before drawing, styling or laying out anything in the shell or in a product. It says what the work should feel like, the rules that make it usable, the materials, and how to judge a page.

## 1. What it should feel like

A well-made object. Think of the care in a good fountain pen, a hand-bound ledger, a drawn map: precise where it must be, warm where it can be, quiet most of the time, and with one detail on every page that shows a person made it. Users of enterprise software expect to be bored; this one should make them look twice and then get out of their way.

Three words to test a page against: **clear**, **warm**, **considered**. Clear: the task is obvious and nothing competes with it. Warm: a drawing, a word, a colour that treats the user as a person. Considered: nothing is there by default; every element earned its place.

## 2. Calm centre, living edges

Every page has two zones.

- **The centre** is the work: tables, forms, records, numbers. It is dense, aligned, consistent, and almost without ornament. The same action sits in the same place on every page. Grids have a density switch. Nothing animates while someone types.
- **The edges and the moments** carry the art: the login page, the home page, the first screen of a module, section openers, empty states, loading states, success and error pages, onboarding, the header's corner, the margins of a dashboard. Here a drawing can be large, a pattern can breathe, a colour can be bold.

The test: cover the centre with your hand and the page should still look designed; cover the edges and it should still work perfectly.

## 3. The drawn world

Illustrations are original line drawings in one consistent hand:

- **Stroke:** one weight (2 px at 100%), round caps and joins, no outlines of outlines. A second, thinner stroke (1 px) for detail only.
- **Fill:** mostly none. Flat fills in the accent colours at low saturation for one or two shapes per drawing; a halftone or hatch pattern (from the pattern generator) for shading, never gradients.
- **Subjects:** the world of the business: people at work drawn with warmth and a little humour, documents, boxes, trucks, plants, coins, keys, clocks; objects slightly larger than life, people slightly simplified. Diversity of people is deliberate and unremarkable.
- **Caricature, carefully:** exaggeration goes to posture and objects, not to features that mark a person's origin, body or gender. Avatars of real users are stylised portraits the user chooses from a generated set (seeded by their id), never derived from a photo. No drawing imitates a known artist, character, mascot or brand.
- **Where:** empty states (one drawing per meaning: nothing yet, nothing found, no access, all done), errors (lost, broken, forbidden), onboarding, section openers, the login and home pages, success moments, and a small spot drawing in the corner of a module's first page.
- **How they are made:** SVG written by hand or by Claude Code in `shell/src/design/illustrations/`, each a React component taking the current ink and accent colours, with `role="img"` and a short `aria-label`, or `aria-hidden` when decorative. Each is reviewed against this brief before it is merged.

## 4. Ornament from geometry

Mandala and pattern work comes from one parametric generator (`shell/src/design/patterns/`): symmetry (6, 8, 12), rings, stroke, density, and a seed. Uses:

- A tenant mark: a unique pattern generated from the tenant's slug, shown on the login page and in the header corner, so every tenant has something of its own without a designer.
- Dividers and section markers: a short strip of pattern at low contrast.
- Texture behind a moment (login, home), at 5 to 10 percent contrast, never behind text or data.
- Loading: the pattern draws itself in; progress has a shape.

Rules: patterns are decorative (`aria-hidden`), they respect reduced motion, and they never touch the centre zone.

## 5. Colour

- **Ink and paper.** One ink colour (near-black with a hint of blue) for text and strokes; one paper colour (warm off-white) for surfaces; both have a dark-scheme counterpart. Pure black and pure white are not used.
- **Accents.** A small set, each with a name and a meaning: one per module for identity (used in the module's opener, its navigation mark and its pattern), and the status colours: success, warning, danger, information. Status is always colour plus a word or an icon.
- **Tenant colours** (TF-173) replace the primary accent within the contrast rules; the system refuses a pair under 4.5:1.
- **Quantity.** A page in the centre zone uses ink, paper, one accent and the status colours, and nothing else. The moments may use the full palette.

The tokens live in `shell/src/design/tokens.ts` and in the Mantine theme; nothing in a page names a colour by value.

## 6. Type

- A reading family for everything in the centre (a humanist sans with good numerals, tabular figures in grids).
- A display family for the moments: titles of the login and home pages, section openers, large numbers on dashboards. It has character; it is not used below 24 px.
- A scale of six sizes; line lengths kept under 75 characters in prose; generous space in the moments, tight in the work.

## 7. Motion

Transitions of 150 to 250 ms that explain where something came from (a panel slides from the edge it belongs to, a row expands in place). Loading states draw rather than spin. Nothing moves while the user types or reads a table. `prefers-reduced-motion` turns every transition into a cut.

## 8. Usability rules that override everything above

- WCAG 2.1 AA: contrast, labels, focus order, keyboard operation, screen-reader names (TF-172). The gallery is scanned in CI; a violation fails the build.
- Keyboard first: every action reachable; visible focus; the grid selects rows and triggers actions by keyboard.
- Consistency: the same action, the same place, the same word, on every page.
- Density: a switch for the grid and the record pages; default comfortable, compact for power users.
- Responsive: desktop and tablet widths without horizontal page scrolling (TF-174).
- Forms: labels above fields, one column unless fields are short and related, inline validation after the first blur, server errors mapped to fields, the primary action at the end, never two primary actions.
- Words: plain, short, in the second person, no jargon, no exclamation marks; errors say what happened and what to do.

## 9. How to judge a page

Before a page is done, answer these in the pull request:

1. What is the task of this page, and is it the first thing the eye lands on?
2. Where is the one detail that shows care, and is it in a moment, not in the work?
3. Cover the edges: does the page still work? Cover the centre: does it still look designed?
4. Keyboard only: can everything be done?
5. Is any colour carrying meaning alone?
6. Would this page be at home next to every other page?

## 10. The materials and where they live

| Material | Where | Owner |
|---|---|---|
| Tokens (colour, type, space, radius, motion) | `shell/src/design/tokens.ts`, the Mantine theme | the design system |
| Illustrations | `shell/src/design/illustrations/` | the design system; products add their own in the same hand |
| Patterns and the tenant mark | `shell/src/design/patterns/` | the design system |
| The illustration checklist | `docs/design/illustration-checklist.md`, answered in every pull request with a drawing | the design system |
| Icons | Tabler icons, one weight, plus the module marks drawn in-house | the design system |
| Page templates | `shell/src/components/templates/`: list, record with tabs, wizard, dashboard, console | the design system |
| Panels | `shell/src/components/`: record header, approval, attachments, history, comments, dialogs, empty and loading states | the design system |
| The gallery | `shell/src/gallery/`, a route in development builds, scanned by the accessibility check | the design system |
