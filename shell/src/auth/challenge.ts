import { fetchLoginChallenge } from "@/api/identity";
import { payload, solve, type LoginChallenge } from "./challenge-solver";

/** Solves in a worker where the browser has one, else on the page (the tests' browser has none). */
function solveAway(challenge: LoginChallenge): Promise<number | undefined> {
  if (typeof Worker === "undefined") {
    return solve(challenge);
  }
  return new Promise((resolve, reject) => {
    const worker = new Worker(new URL("./challenge.worker.ts", import.meta.url), { type: "module" });
    worker.onmessage = (event: MessageEvent<number | null>) => {
      worker.terminate();
      resolve(event.data ?? undefined);
    };
    worker.onerror = (event) => {
      worker.terminate();
      reject(new Error(event.message));
    };
    worker.postMessage(challenge);
  });
}

/**
 * The check a sign-in needs after repeated failures (TF-072 AC4, ADR-0053): a challenge from the API, solved here, and
 * the answer the next sign-in sends. Each answer works once.
 */
export async function proveNotAProgram(): Promise<string> {
  const challenge = await fetchLoginChallenge();
  const number = await solveAway(challenge);
  if (number === undefined) {
    throw new Error("The challenge has no answer.");
  }
  return payload(challenge, number);
}
