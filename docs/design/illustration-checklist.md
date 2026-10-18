# Illustration review checklist

Every drawing or pattern in a pull request is checked against this list before it is merged (ADR-0024 item 2, ADR-0026). The reviewer answers each line in the pull request; one "no" sends the drawing back.

- [ ] **One hand.** Outlines are 2 px with round caps and joins, detail is 1 px, no outline sits around an outline. The drawing is made with the `Drawing` wrapper and the `Tint`, `Fine` and `Ground` helpers in `shell/src/design/illustrations/Drawing.tsx`, not with copies of them.
- [ ] **Fills.** None, except one or two shapes per drawing in the accent through `Tint`, and the hatch for shading. Never a gradient.
- [ ] **Original.** Drawn here, in code. It imitates no known artist, character, mascot or brand, and copies nothing from a library or a stock set.
- [ ] **Subjects.** The world of the business: people at work, documents, boxes, trucks, plants, coins, keys, clocks. Objects a little larger than life, people a little simplified. Exaggeration goes to posture and objects, never to features that mark a person's origin, body or gender.
- [ ] **People.** A person is drawn only with the `Figure` primitive (`figure()` in `shell/src/design/illustrations/hand.ts`, rendered by `Figure` in `Drawing.tsx`): a round head with a cap line and no face, a closed rounded torso in the accent tint, arms and legs that bend once at the elbow and the knee, short strokes for feet, the same proportions every time. The posture carries the meaning; a new posture is a new pose in `hand.ts`, never a drawing of its own.
- [ ] **Accessible name or hidden.** `role="img"` with a short `aria-label` from the catalogue where the drawing carries meaning; `decorative` (hidden) where the text beside it already says the same thing.
- [ ] **Both schemes.** Looked at in light and in dark in the gallery. The ink and the accent come from the variables; no colour by value.
- [ ] **Reduced motion.** Anything that draws itself in or loops stands still when the viewer asked for less motion; checked in the gallery with the setting on.
- [ ] **Patterns.** `aria-hidden`, never behind text or data; a texture sits at 6 to 8 percent, and the text on the surface over it keeps 4.5:1.
- [ ] **Proven.** The gallery shows it in both schemes, the illustrations test passes, the scan is clean.
