// Sets the colour scheme before the first paint, so a dark reload never flashes light (ADR-0025).
// External on purpose: the Content Security Policy allows scripts from this origin only, never inline ones.
// It reads the viewer's choice from the key the shell's colour scheme manager writes (src/design/scheme.ts) and
// resolves "auto" as MantineProvider does on mount.
(function () {
  var stored = null;
  try {
    stored = window.localStorage.getItem("iron.scheme");
  } catch {
    stored = null;
  }
  var dark = typeof window.matchMedia === "function" && window.matchMedia("(prefers-color-scheme: dark)").matches;
  var scheme = stored === "light" || stored === "dark" ? stored : dark ? "dark" : "light";
  document.documentElement.setAttribute("data-mantine-color-scheme", scheme);
  document.documentElement.style.colorScheme = scheme;
})();
