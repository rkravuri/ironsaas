/**
 * The login's proof of work (TF-072 AC4, ADR-0053), ALTCHA's form: the server sends a salt and the SHA-256 of the salt
 * followed by a secret number no larger than a maximum; the browser finds the number by trying each in turn and sends
 * it back with the challenge it signed. Nothing leaves the browser but the answer, and no other host is asked. Pure, so
 * the worker and the tests share it.
 */
export interface LoginChallenge {
  algorithm: string;
  challenge: string;
  max_number: number;
  salt: string;
  signature: string;
}

const encoder = new TextEncoder();

/** The hex SHA-256 of the salt followed by the number, as the server computes it. */
export async function hash(salt: string, number: number): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", encoder.encode(`${salt}${number}`));
  return Array.from(new Uint8Array(digest), (byte) => byte.toString(16).padStart(2, "0")).join("");
}

/** The number whose hash is the challenge, tried from 0 to the challenge's maximum; undefined when there is none. */
export async function solve(challenge: LoginChallenge): Promise<number | undefined> {
  for (let number = 0; number <= challenge.max_number; number++) {
    if ((await hash(challenge.salt, number)) === challenge.challenge) {
      return number;
    }
  }
  return undefined;
}

/** The answer the sign-in sends: the challenge with its number, as JSON in base64. */
export function payload(challenge: LoginChallenge, number: number): string {
  return btoa(
    JSON.stringify({
      algorithm: challenge.algorithm,
      challenge: challenge.challenge,
      number,
      salt: challenge.salt,
      signature: challenge.signature,
    }),
  );
}
