import { runInNewContext } from "node:vm";
import { afterEach, describe, expect, it } from "vitest";
// The script as text, through Vite: the browser runs it as a classic script before React, never as a module.
import source from "../../public/scheme.js?raw";

interface Viewer {
  dark: boolean;
  stored?: string | null;
  storageThrows?: boolean;
}

function schemeFor({ dark, stored = null, storageThrows = false }: Viewer): string | null {
  const html = document.documentElement;
  html.removeAttribute("data-mantine-color-scheme");
  const windowStub = {
    localStorage: {
      getItem: (): string | null => {
        if (storageThrows) {
          throw new Error("blocked");
        }
        return stored;
      },
    },
    matchMedia: (query: string) => ({ matches: dark && query.includes("dark") }),
  };
  runInNewContext(source, { window: windowStub, document });
  return html.getAttribute("data-mantine-color-scheme");
}

afterEach(() => {
  document.documentElement.removeAttribute("data-mantine-color-scheme");
});

describe("the scheme script", () => {
  it("follows the operating system when the viewer has not chosen [ADR-0025]", () => {
    expect(schemeFor({ dark: true })).toBe("dark");
    expect(schemeFor({ dark: false })).toBe("light");
    expect(schemeFor({ dark: true, stored: "auto" })).toBe("dark");
  });

  it("follows the viewer's stored choice over the operating system [ADR-0025]", () => {
    expect(schemeFor({ dark: true, stored: "light" })).toBe("light");
    expect(schemeFor({ dark: false, stored: "dark" })).toBe("dark");
  });

  it("still answers when the storage refuses [ADR-0025]", () => {
    expect(schemeFor({ dark: true, storageThrows: true })).toBe("dark");
  });
});
