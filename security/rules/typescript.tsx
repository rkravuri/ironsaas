// The test of typescript.yaml beside it (ADR-0055): each "ruleid" line names the rule the next line must raise, each
// "ok" line a rule the next line must not. make security runs the scanner's test mode over this folder. Not built.

export function code(text: string) {
  // ruleid: iron-code-from-text
  eval(text);
  // ruleid: iron-code-from-text
  const made = new Function("a", text);
  // ruleid: iron-code-from-text
  setTimeout("go()", 10);
  // ok: iron-code-from-text
  setTimeout(() => made(), 10);
}

export function navigate(returnTo: string) {
  // ruleid: iron-navigation-to-another-site
  window.location.assign(returnTo);
  // ruleid: iron-navigation-to-another-site
  window.location.href = returnTo;
  // ruleid: iron-navigation-to-another-site
  window.open(returnTo, "_blank");
  // ok: iron-navigation-to-another-site
  window.location.assign("/login");
  // ok: iron-navigation-to-another-site
  window.location.reload();
}

export function messages(frame: Window, data: unknown) {
  // ruleid: iron-message-to-any-origin
  frame.postMessage(data, "*");
  // ok: iron-message-to-any-origin
  frame.postMessage(data, "https://acme.example");
}

export function storage(accessToken: string, pages: string[]) {
  // ruleid: iron-secret-in-browser-storage
  window.localStorage.setItem("iron.token", accessToken);
  // ruleid: iron-secret-in-browser-storage
  sessionStorage.setItem("pending", accessToken);
  // ok: iron-secret-in-browser-storage
  window.localStorage.setItem("iron.recent", JSON.stringify(pages));
}

export function cookies(value: string) {
  // ruleid: iron-cookie-from-script
  document.cookie = value;
  // ok: iron-cookie-from-script
  return document.cookie.length;
}

export async function calls(path: string) {
  // ruleid: iron-request-outside-the-client
  await fetch(path);
  // ruleid: iron-request-outside-the-client
  const old = new XMLHttpRequest();
  // ok: iron-request-outside-the-client
  return old.readyState;
}
