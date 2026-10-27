import type { RichTextHtml } from "@/api/rich-text";

/**
 * The one place the shell renders HTML (TF-071 AC5, ADR-0053): rich text from the API, which the server cleaned by its
 * allow-list as it was written. The lint rule iron/inner-html refuses every other way of putting HTML into a page.
 */
export function RichTextView({ html, label }: { html: RichTextHtml; label?: string }) {
  return <div className="iron-rich-text" aria-label={label} dangerouslySetInnerHTML={{ __html: html }} />;
}
