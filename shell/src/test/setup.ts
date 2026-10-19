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

// jsdom has no font loading API; Mantine's autosizing textarea listens for fonts that finish loading.
if (!("fonts" in document)) {
  Object.defineProperty(document, "fonts", { value: { addEventListener: () => {}, removeEventListener: () => {}, ready: Promise.resolve() } });
}
}
