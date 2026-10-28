# Threat model: the shell's public pages

- **Modules:** none
- **Endpoints:** none
- **Reviewed:** 2026-10-28 (S3 T10a)

The shell's server is nginx serving one static build to every tenant (ADR-0020). Its public pages call the anonymous
endpoints the sign-in's record covers; this record covers the pages themselves, an external interface of their own.

## What it protects

The people on the pages before they sign in (login, invitation, recovery, enrolment, the one-time sign-in link, the
company login's return, terms) and the shell's own code once it runs in their browser.

## Who can reach it

Anyone who has the tenant's host name, in any browser.

## Threats

| Category | Threat | Controls | Requirements |
|---|---|---|---|
| Spoofing | A look-alike site collecting passwords. | Passkeys are bound to the real origin; each page shows the tenant's host name. | TF-013 |
| Tampering | Script injected into a page. | A content security policy without `unsafe-inline`, styles under a nonce per page, no external host; HTML only through `RichTextView`; every API answer parsed by a schema. | TF-070, TF-071 |
| Tampering | The page framed by another site to trick a click. | `X-Frame-Options: DENY` and the policy's `frame-ancestors`. | TF-070 |
| Repudiation | Denying what the page did. | The server records each action; the pages keep nothing that counts as proof. | TF-060 |
| Information disclosure | A link's code reaching a server or a log. | Codes travel in the URL's fragment, which no server receives, and the page posts them in the body; no credential in browser storage (rule `iron-secret-in-browser-storage`); a strict referrer policy. | TF-017, TF-070 |
| Denial of service | The pages made unusable. | Static files; the login's proof of work is solved in a Web Worker, so the page stays responsive. | TF-072 |
| Elevation of privilege | An open redirect into a phishing page. | The browser leaves the site only through `leaveFor`, with an address the API gave (rule `iron-navigation-to-another-site`); inside the shell the router moves. | TF-070 |

## What remains

- Since T9a the shell's server answers 404 for the API's paths and a missing file; nothing else is accepted.
- Chromium's `report-to` reports do not reach the laptop's ingress (its certificate); Firefox's and the browser
  test's reports do.
