// What a test proves (ADR-0044, TF-218): the title of every Vitest and Playwright test ends with the bracketed list of
// the requirement criteria it proves ("... [TF-033 AC2]"), a whole requirement when it proves every criterion
// ("[TF-033]"), the decisions it proves ("[ADR-0029]"), or "[untraced]" alone when it proves nothing a document states.
// make trace reads the list from the source and the results from the JUnit files, and refuses an identifier that does
// not exist; this rule refuses a title without the list, or with one out of form, where the developer writes it.

// The one grammar, as tools/trace.py and the .NET analyzer (IRON0006) read it.
const identifier = /^(?:[A-Z]{2,3}-[0-9]{3}(?: AC[1-9][0-9]*)?|ADR-[0-9]{4})$/;
const trailingList = /\[([^[\]]+)\]\s*$/;
const untraced = "untraced";

// it.skip(...), test.only(...), test.fixme(...); each, skipIf and runIf take a table or a condition and return the test
// function, so the title is in the next call: it.each(table)("title", ...).
const modifiers = new Set(["skip", "only", "fixme", "fail", "fails", "concurrent", "sequential", "todo"]);
const factories = new Set(["each", "skipIf", "runIf"]);

/** ["it", "skip"] for it.skip, or undefined when the callee is not a member chain on an identifier. */
function chain(node) {
  const names = [];
  let current = node;
  while (current.type === "MemberExpression" && !current.computed && current.property.type === "Identifier") {
    names.unshift(current.property.name);
    current = current.object;
  }
  return current.type === "Identifier" ? [current.name, ...names] : undefined;
}

/** Whether a call declares a test, and whether its first argument may be something other than a title. */
function testCall(callee) {
  if (callee.type === "Identifier") {
    return { isTest: callee.name === "it" || callee.name === "test", conditional: false };
  }
  if (callee.type === "MemberExpression") {
    const names = chain(callee);
    const isTest =
      names !== undefined &&
      (names[0] === "it" || names[0] === "test") &&
      names.slice(1).every((name) => modifiers.has(name));
    // Playwright's test.skip(condition, "reason") inside a test is a call, not a test.
    return { isTest, conditional: true };
  }
  if (callee.type === "CallExpression" && callee.callee.type === "MemberExpression") {
    const names = chain(callee.callee);
    const isTest =
      names !== undefined &&
      (names[0] === "it" || names[0] === "test") &&
      factories.has(names[names.length - 1]) &&
      names.slice(1, -1).every((name) => modifiers.has(name));
    return { isTest, conditional: false };
  }
  return { isTest: false, conditional: false };
}

/** The title's fixed tail, where the list must stand: a string's value, or a template's last fixed part. */
function tail(title) {
  if (title.type === "Literal" && typeof title.value === "string") {
    return title.value;
  }
  if (title.type === "TemplateLiteral") {
    return title.quasis[title.quasis.length - 1]?.value.cooked ?? "";
  }
  return undefined;
}

export const testProves = {
  meta: {
    type: "problem",
    docs: { description: "A test's title ends with what it proves (ADR-0044)." },
    schema: [],
    messages: {
      missing:
        'A test names what it proves at the end of its title: "... [TF-033 AC2]" for the requirement criteria it proves, "[ADR-0029]" for a decision, or "[untraced]" when it proves neither (ADR-0044).',
      malformed:
        "'{{value}}' is not a requirement criterion (TF-033 AC2), a requirement (TF-033) or a decision (ADR-0029) (ADR-0044).",
      untracedAlone:
        "[untraced] stands alone: a test that proves something names it, and only one that proves nothing a document states is untraced (ADR-0044).",
      notLiteral:
        "A test's title is a string or a template literal, so that make trace can read what it proves (ADR-0044).",
    },
  },
  create(context) {
    return {
      CallExpression(node) {
        const { isTest, conditional } = testCall(node.callee);
        if (!isTest) {
          return;
        }
        const title = node.arguments[0];
        const text = title === undefined ? undefined : tail(title);
        if (text === undefined) {
          if (!conditional && title !== undefined) {
            context.report({ node: title, messageId: "notLiteral" });
          }
          return;
        }
        const list = trailingList.exec(text);
        if (list === null) {
          context.report({ node: title, messageId: "missing" });
          return;
        }
        const values = list[1].split(",").map((value) => value.trim());
        if (values.includes(untraced)) {
          if (values.length > 1) {
            context.report({ node: title, messageId: "untracedAlone" });
          }
          return;
        }
        for (const value of values) {
          if (!identifier.test(value)) {
            context.report({ node: title, messageId: "malformed", data: { value } });
          }
        }
      },
    };
  },
};
