import "@testing-library/jest-dom/vitest";
import "@/i18n";
import { cleanup } from "@testing-library/react";
import { afterEach } from "vitest";

afterEach(() => {
  cleanup();
});

// jsdom has no matchMedia; Mantine asks for the colour scheme. (A test file in the node environment has no window.)
if (typeof window !== "undefined") {
  Object.defineProperty(window, "matchMedia", {
    writable: true,
    value: (query: string) => ({
      matches: false,
      media: query,
      onchange: null,
      addListener: () => {},
      removeListener: () => {},
      addEventListener: () => {},
      removeEventListener: () => {},
      dispatchEvent: () => false,
    }),
  });

  class ResizeObserverStub {
    observe() {}
    unobserve() {}
    disconnect() {}
  }
  Object.defineProperty(window, "ResizeObserver", { writable: true, value: ResizeObserverStub });

  // Node's BroadcastChannel reaches every worker thread of the run, so one test file's tabs would hear another file's:
  // a session ended in one file would sign out the pages of the next. Each file gets channels that reach its own.
  class FileChannel extends EventTarget {
    private static readonly open = new Map<string, Set<FileChannel>>();

    constructor(readonly name: string) {
      super();
      const peers = FileChannel.open.get(name) ?? new Set<FileChannel>();
      peers.add(this);
      FileChannel.open.set(name, peers);
    }

    postMessage(data: unknown): void {
      for (const peer of FileChannel.open.get(this.name) ?? []) {
        if (peer !== this) {
          setTimeout(() => peer.dispatchEvent(new MessageEvent("message", { data: structuredClone(data) })), 0);
        }
      }
    }

    close(): void {
      FileChannel.open.get(this.name)?.delete(this);
    }
  }
  Object.defineProperty(globalThis, "BroadcastChannel", { writable: true, configurable: true, value: FileChannel });

  // jsdom does not lay out, so it has no scrollIntoView; the search palette scrolls its chosen action into view.
  if (!("scrollIntoView" in Element.prototype)) {
    Object.defineProperty(Element.prototype, "scrollIntoView", { writable: true, value: () => {} });
  }

  // jsdom has no font loading API; Mantine's autosizing textarea listens for fonts that finish loading.
  if (!("fonts" in document)) {
    Object.defineProperty(document, "fonts", {
      value: { addEventListener: () => {}, removeEventListener: () => {}, ready: Promise.resolve() },
    });
  }
}
