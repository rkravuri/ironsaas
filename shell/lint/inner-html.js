// The inner HTML rule (TF-071 AC5, ADR-0053): React encodes what it renders, and the one way around that is the rich-text
// component, which renders HTML the server cleaned by its allow-list. Everywhere else the rule refuses each way of putting
// HTML into the page: the dangerouslySetInnerHTML prop, in JSX or in a props object, an assignment to innerHTML or
// outerHTML, insertAdjacentHTML, and document.write.

/** The name of a member expression's property when it is written plainly or as a string: a.innerHTML, a["innerHTML"]. */
function propertyName(node) {
  if (node?.type !== "MemberExpression") {
    return undefined;
  }
  if (!node.computed && node.property.type === "Identifier") {
    return node.property.name;
  }
  return node.property.type === "Literal" && typeof node.property.value === "string" ? node.property.value : undefined;
}

export const innerHtml = {
  meta: {
    type: "problem",
    docs: { description: "HTML reaches the page only through the rich-text component (ADR-0053)." },
    schema: [],
    messages: {
      innerHtml:
        "{{what}} puts HTML into the page past React's encoding. Render text as text, or rich text from the API through RichTextView, the one component that renders HTML the server cleaned (ADR-0053).",
    },
  },
  create(context) {
    const report = (node, what) => context.report({ node, messageId: "innerHtml", data: { what } });
    return {
      JSXAttribute(node) {
        if (node.name.type === "JSXIdentifier" && node.name.name === "dangerouslySetInnerHTML") {
          report(node, "dangerouslySetInnerHTML");
        }
      },
      Property(node) {
        const key =
          node.key.type === "Identifier" ? node.key.name : node.key.type === "Literal" ? node.key.value : undefined;
        if (key === "dangerouslySetInnerHTML") {
          report(node, "dangerouslySetInnerHTML");
        }
      },
      AssignmentExpression(node) {
        const name = propertyName(node.left);
        if (name === "innerHTML" || name === "outerHTML") {
          report(node, name);
        }
      },
      CallExpression(node) {
        const name = propertyName(node.callee);
        if (name === "insertAdjacentHTML") {
          report(node, name);
        } else if (
          (name === "write" || name === "writeln") &&
          node.callee.object.type === "Identifier" &&
          node.callee.object.name === "document"
        ) {
          report(node, `document.${name}`);
        }
      },
    };
  },
};
