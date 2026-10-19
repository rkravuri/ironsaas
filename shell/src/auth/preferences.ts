import { useCallback, useEffect, useRef } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { useMantineColorScheme } from "@mantine/core";
import type { SettingKey } from "@/api/setting-keys";
import { saveSetting } from "@/api/settings";
import { densities, setDensity, useDensity, type Density } from "@/design/density";
import { schemeChoices, type SchemeChoice } from "@/design/scheme";
import { contextQueryKey } from "./entitlements";
import { useSession } from "./session-context";

const isScheme = (value: unknown): value is SchemeChoice => typeof value === "string" && (schemeChoices as readonly string[]).includes(value);
const isDensity = (value: unknown): value is Density => typeof value === "string" && (densities as readonly string[]).includes(value);

/**
 * A person's colour scheme and density follow them to every browser they sign in on (ADR-0036). The browser keeps its
 * copy in localStorage, which `public/scheme.js` and the density read before the first paint, so a reload never
 * flashes; the server's value is the person's. After sign-in the person's own value wins and is written to the copy;
 * a person with none yet takes up the choice this browser had. From then on a change on either side reaches the other:
 * one made here is saved, one made in another browser arrives with /me/context and is applied.
 */
export function usePreferenceSync(): void {
  const { colorScheme, setColorScheme } = useMantineColorScheme();
  const density = useDensity();
  const applyScheme = useCallback((value: SchemeChoice) => setColorScheme(value), [setColorScheme]);
  useSyncedPreference("shell.colour_scheme", colorScheme, applyScheme, isScheme);
  useSyncedPreference("shell.density", density, setDensity, isDensity);
}

function useSyncedPreference<T extends string>(key: SettingKey, local: T, apply: (value: T) => void, accepts: (value: unknown) => value is T): void {
  const session = useSession();
  const queryClient = useQueryClient();
  const entry = session.settings.get(key);
  const known = session.status === "signed-in" && session.settingsKnown;
  // What the last look saw on each side, so a change can be told apart from the other side's.
  const last = useRef<{ server: T; local: T } | undefined>(undefined);

  useEffect(() => {
    if (!known || entry === undefined || !accepts(entry.value)) {
      last.current = undefined;
      return;
    }

    const server = entry.value;
    const previous = last.current;
    last.current = { server, local };
    const save = () => {
      // A preference that fails to save stays in this browser; the next change tries again.
      void saveSetting(key, { scope: "user" }, local)
        .then(() => queryClient.invalidateQueries({ queryKey: contextQueryKey }))
        .catch(() => undefined);
    };

    if (previous === undefined) {
      if (entry.level === "user") {
        if (server !== local) {
          apply(server);
        }
      } else if (local !== server) {
        save();
      }
      return;
    }

    if (server !== previous.server) {
      if (server !== local) {
        apply(server);
      }
      return;
    }

    if (local !== previous.local && local !== server) {
      save();
    }
  }, [known, entry, local, key, apply, accepts, queryClient]);
}
