import { solve, type LoginChallenge } from "./challenge-solver";

// The proof of work off the page's thread, so the form stays responsive while the browser counts (ADR-0053). The worker
// is a file of the shell's own origin, which the policy's worker-src allows.
const scope = self as unknown as {
  onmessage: ((event: MessageEvent<LoginChallenge>) => void) | null;
  postMessage: (number: number | null) => void;
};

scope.onmessage = (event) => {
  void solve(event.data).then((number) => scope.postMessage(number ?? null));
};
