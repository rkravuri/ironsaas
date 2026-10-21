/**
 * Passkeys through the browser's WebAuthn API (ADR-0047), without a library: the API's options carry their bytes in
 * base64url, the browser wants ArrayBuffers, and the answer goes back as the JSON a credential's toJSON() gives. The
 * secret never leaves the authenticator; what travels is the public key at creation and a signature at each use.
 */

type Json = Record<string, unknown>;

export function passkeysSupported(): boolean {
  return (
    typeof window !== "undefined" &&
    "PublicKeyCredential" in window &&
    typeof navigator.credentials?.create === "function"
  );
}

/** The person cancelled, the time ran out, or the authenticator refused: nothing was created or signed. */
export function isPasskeyCancelled(error: unknown): boolean {
  return error instanceof DOMException && (error.name === "NotAllowedError" || error.name === "AbortError");
}

export function toBytes(base64url: string): ArrayBuffer {
  const base64 = base64url.replace(/-/g, "+").replace(/_/g, "/");
  const padded = base64 + "=".repeat((4 - (base64.length % 4)) % 4);
  const text = atob(padded);
  const bytes = new Uint8Array(text.length);
  for (let i = 0; i < text.length; i++) {
    bytes[i] = text.charCodeAt(i);
  }
  return bytes.buffer;
}

export function toBase64Url(buffer: ArrayBuffer | ArrayBufferView): string {
  const bytes =
    buffer instanceof ArrayBuffer
      ? new Uint8Array(buffer)
      : new Uint8Array(buffer.buffer, buffer.byteOffset, buffer.byteLength);
  let text = "";
  for (const byte of bytes) {
    text += String.fromCharCode(byte);
  }
  return btoa(text).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

interface Descriptor {
  id: string;
  type?: string;
  transports?: string[];
}

const descriptors = (list: unknown): PublicKeyCredentialDescriptor[] =>
  Array.isArray(list)
    ? (list as Descriptor[]).map((item) => ({
        id: toBytes(item.id),
        type: "public-key",
        ...(item.transports ? { transports: item.transports as AuthenticatorTransport[] } : {}),
      }))
    : [];

/** Creates a passkey with the API's creation options; answers the credential as JSON for the API. */
export async function createPasskey(options: Json): Promise<Json> {
  const user = options.user as Json;
  const publicKey = {
    ...options,
    challenge: toBytes(options.challenge as string),
    user: { ...user, id: toBytes(user.id as string) },
    excludeCredentials: descriptors(options.excludeCredentials),
  } as unknown as PublicKeyCredentialCreationOptions;
  const credential = (await navigator.credentials.create({ publicKey })) as PublicKeyCredential | null;
  if (!credential) {
    throw new DOMException("No passkey was created.", "NotAllowedError");
  }
  const response = credential.response as AuthenticatorAttestationResponse;
  return {
    id: credential.id,
    rawId: toBase64Url(credential.rawId),
    type: credential.type,
    response: {
      clientDataJSON: toBase64Url(response.clientDataJSON),
      attestationObject: toBase64Url(response.attestationObject),
      transports: typeof response.getTransports === "function" ? response.getTransports() : [],
    },
    clientExtensionResults: credential.getClientExtensionResults(),
  };
}

/** Signs with a passkey for the API's assertion options; answers the assertion as JSON for the API. */
export async function signWithPasskey(options: Json): Promise<Json> {
  const publicKey = {
    ...options,
    challenge: toBytes(options.challenge as string),
    allowCredentials: descriptors(options.allowCredentials),
  } as unknown as PublicKeyCredentialRequestOptions;
  const credential = (await navigator.credentials.get({ publicKey })) as PublicKeyCredential | null;
  if (!credential) {
    throw new DOMException("No passkey answered.", "NotAllowedError");
  }
  const response = credential.response as AuthenticatorAssertionResponse;
  return {
    id: credential.id,
    rawId: toBase64Url(credential.rawId),
    type: credential.type,
    response: {
      clientDataJSON: toBase64Url(response.clientDataJSON),
      authenticatorData: toBase64Url(response.authenticatorData),
      signature: toBase64Url(response.signature),
      userHandle: response.userHandle ? toBase64Url(response.userHandle) : null,
    },
    clientExtensionResults: credential.getClientExtensionResults(),
  };
}
