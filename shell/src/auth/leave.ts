/**
 * Sends the browser to another site: a company login's sign-in or sign-out page (ADR-0048). A whole-page navigation,
 * which the content security policy allows, unlike a request from the page. One function, so a test replaces it.
 */
export function leaveFor(url: string): void {
  window.location.assign(url);
}
