/** The set the brief asks for (section 3): four empty states, three errors, the login, the home page, success, loading. */
export const illustrationNames = [
  "nothingYet",
  "nothingFound",
  "noAccess",
  "allDone",
  "lost",
  "broken",
  "forbidden",
  "welcome",
  "home",
  "success",
  "loading",
] as const;
export type IllustrationName = (typeof illustrationNames)[number];
