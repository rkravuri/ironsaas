// The suite rule (ADR-0043): a module of one suite never imports from another suite's folder. A no-restricted-imports
// pattern cannot know which suite the importing file sits in, so this rule reads the suite from the file's own path
// and compares it with where each import resolves: "@/modules/suites/school/..." or "../../school/...".
import path from "node:path";

const suitesFolder = "src/modules/suites/";

/** The file's path from the shell's root with forward slashes, or undefined when it is outside modules/suites. */
function fromShellRoot(filename) {
  const normalized = filename.replaceAll("\\", "/");
  if (normalized.startsWith(suitesFolder)) {
    return normalized;
  }
  const at = normalized.indexOf(`/${suitesFolder}`);
  return at === -1 ? undefined : normalized.slice(at + 1);
}

/** The suite a path under modules/suites belongs to, "procurement" for "src/modules/suites/procurement/...". */
function suiteOf(file) {
  if (file === undefined || !file.startsWith(suitesFolder)) {
    return undefined;
  }
  const segments = file.slice(suitesFolder.length).split("/");
  return segments.length > 1 && segments[0] !== "" ? segments[0] : undefined;
}

/** Where an import source leads from this file, from the shell's root; undefined for a package. */
function resolve(file, source) {
  if (source.startsWith("@/")) {
    return path.posix.normalize(`src/${source.slice(2)}`);
  }
  if (source === "." || source === ".." || source.startsWith("./") || source.startsWith("../")) {
    return path.posix.normalize(path.posix.join(path.posix.dirname(file), source));
  }
  return undefined;
}

export const suiteImports = {
  meta: {
    type: "problem",
    docs: { description: "A module of one suite never imports from another suite (ADR-0043)." },
    schema: [],
    messages: {
      otherSuite:
        "A module of the {{own}} suite must not import from the {{other}} suite: a suite never references another suite (ADR-0043). What both need belongs in a business module.",
    },
  },
  create(context) {
    const file = fromShellRoot(context.filename);
    const own = suiteOf(file);
    if (own === undefined) {
      return {};
    }
    const check = (node) => {
      const source = node.source;
      if (source?.type !== "Literal" || typeof source.value !== "string") {
        return;
      }
      // A trailing slash lets the folder itself ("@/modules/suites/school") count as inside it.
      const target = resolve(file, source.value);
      const other = target === undefined ? undefined : suiteOf(`${target}/`);
      if (other !== undefined && other !== own) {
        context.report({ node: source, messageId: "otherSuite", data: { own, other } });
      }
    };
    return {
      ImportDeclaration: check,
      ExportNamedDeclaration: check,
      ExportAllDeclaration: check,
      ImportExpression: check,
    };
  },
};
