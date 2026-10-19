import { useCallback, useRef, useSyncExternalStore } from "react";
import {
  OpenFeature,
  OpenFeatureEventEmitter,
  ProviderEvents,
  StandardResolutionReasons,
  type JsonArray,
  type JsonObject,
  type JsonValue,
  type Provider,
  type ResolutionDetails,
} from "@openfeature/web-sdk";
import type { FlagValue } from "@/api/flags";

type Known = { value: unknown; reason: string };

/**
 * The shell's OpenFeature provider (TF-054, ADR-0035): it answers from the flags /me/context evaluated for this person
 * in the API, and tells its clients when a new answer arrives, so components re-render within the context's refresh.
 * A flag it has no value for, or one of another type, answers the caller's default: the shell works on defaults when
 * the flags fail to load (TF-054 AC3). It never decides anything the API would not decide again (TF-054 AC2).
 */
export class ContextFlagProvider implements Provider {
  readonly metadata = { name: "iron-context" } as const;
  readonly runsOn = "client" as const;
  readonly events = new OpenFeatureEventEmitter();
  private values = new Map<string, Known>();

  /** A new answer of /me/context, or none (signed out, or the call failed): the defaults then apply. */
  update(values: readonly FlagValue[]): void {
    this.values = new Map(values.map((flag) => [flag.key, { value: flag.value, reason: flag.reason }]));
    this.events.emit(ProviderEvents.ConfigurationChanged, { flagsChanged: values.map((flag) => flag.key) });
  }

  resolveBooleanEvaluation(flagKey: string, defaultValue: boolean): ResolutionDetails<boolean> {
    return this.resolve(flagKey, defaultValue, (value): value is boolean => typeof value === "boolean");
  }

  resolveStringEvaluation(flagKey: string, defaultValue: string): ResolutionDetails<string> {
    return this.resolve(flagKey, defaultValue, (value): value is string => typeof value === "string");
  }

  resolveNumberEvaluation(flagKey: string, defaultValue: number): ResolutionDetails<number> {
    return this.resolve(flagKey, defaultValue, (value): value is number => typeof value === "number");
  }

  resolveObjectEvaluation<T extends JsonValue>(flagKey: string, defaultValue: T): ResolutionDetails<T> {
    return this.resolve(flagKey, defaultValue, (value): value is T => value !== null && typeof value === "object");
  }

  private resolve<T>(flagKey: string, defaultValue: T, fits: (value: unknown) => value is T): ResolutionDetails<T> {
    const known = this.values.get(flagKey);
    if (known === undefined || !fits(known.value)) {
      return { value: defaultValue, reason: StandardResolutionReasons.DEFAULT };
    }
    return { value: known.value, reason: known.reason.toUpperCase(), variant: "context" };
  }
}

/** The one provider of the shell, set once when the module loads. */
export const flagProvider = new ContextFlagProvider();
OpenFeature.setProvider(flagProvider);
const client = OpenFeature.getClient();

/** A boolean, a string, a number, or a JSON object or array: what a flag can be (ADR-0035), never null. */
type FlagDefault = Exclude<JsonValue, null>;

function evaluate<T extends FlagDefault>(key: string, defaultValue: T): T {
  switch (typeof defaultValue) {
    case "boolean":
      return client.getBooleanValue(key, defaultValue) as T;
    case "string":
      return client.getStringValue(key, defaultValue) as T;
    case "number":
      return client.getNumberValue(key, defaultValue) as T;
    default:
      return client.getObjectValue(key, defaultValue as JsonValue) as T;
  }
}

function subscribe(onChange: () => void): () => void {
  client.addHandler(ProviderEvents.ConfigurationChanged, onChange);
  client.addHandler(ProviderEvents.Ready, onChange);
  return () => {
    client.removeHandler(ProviderEvents.ConfigurationChanged, onChange);
    client.removeHandler(ProviderEvents.Ready, onChange);
  };
}

/**
 * The one hook for flags (TF-054): the flag's value for the signed-in person through OpenFeature, or the default when
 * nothing is known. The value's type is the default's: a boolean, a string, a number, or a JSON object. Use it to show
 * or hide, never to decide what a person may do: the API checks every action again.
 */
export function useFlag(key: string, defaultValue: boolean): boolean;
export function useFlag(key: string, defaultValue: string): string;
export function useFlag(key: string, defaultValue: number): number;
export function useFlag<T extends JsonObject | JsonArray>(key: string, defaultValue: T): T;
export function useFlag<T extends FlagDefault>(key: string, defaultValue: T): T {
  const last = useRef<{ json: string; value: T } | undefined>(undefined);
  const snapshot = useCallback(() => {
    const value = evaluate(key, defaultValue);
    if (typeof value !== "object" || value === null) {
      return value;
    }
    // An object comes back new from every evaluation; the same JSON keeps the same object, so React does not loop.
    const json = JSON.stringify(value);
    if (last.current?.json !== json) {
      last.current = { json, value };
    }
    return last.current.value;
  }, [key, defaultValue]);
  return useSyncExternalStore(subscribe, snapshot, snapshot);
}
