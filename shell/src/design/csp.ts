/**
 * The style nonce of this page (ADR-0053). The shell's content security policy allows no inline style; the style
 * elements Mantine and AG Grid make carry the nonce the server gave this response, which Vite's placeholder meta tag
 * holds after nginx filled it in. A browser hides the attribute once the page is parsed, so it is read as a property.
 * In development and in the tests no policy applies: the nonce is empty, and an empty one changes nothing.
 */
export function styleNonce(): string {
  const nonce = document.querySelector<HTMLMetaElement>('meta[property="csp-nonce"]')?.nonce ?? "";
  return nonce === "CSP_NONCE" ? "" : nonce;
}
