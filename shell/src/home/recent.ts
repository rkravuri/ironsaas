import { useEffect } from "react";
import { useLocation } from "react-router";
import { useSession } from "@/auth/session-context";

/** One page opened recently: where, which route it was (for its title and its permission), and a record's own name. */
export interface RecentPage {
  path: string;
  titleKey: string;
  module: string;
  /** The record's name, when the page is one record: "Ada Lovelace" reads better than "Users" twice. */
  label?: string;
  at: number;
}

export const recentLimit = 5;

/** Per viewer and per person: a per-viewer convenience kept in this browser, never sent anywhere (ADR-0033). */
export const recentStorageKey = (principal: string) => `iron.recent.${principal}`;

const isRecentPage = (value: unknown): value is RecentPage => {
  const page = value as Partial<RecentPage> | null;
  return typeof page?.path === "string" && typeof page.titleKey === "string" && typeof page.module === "string" && typeof page.at === "number";
};

/** The pages, newest first. A blocked or damaged storage means none, nothing worse. */
export function readRecent(principal: string): RecentPage[] {
  try {
    const parsed: unknown = JSON.parse(window.localStorage.getItem(recentStorageKey(principal)) ?? "[]");
    return Array.isArray(parsed) ? parsed.filter(isRecentPage).slice(0, recentLimit) : [];
  } catch {
    return [];
  }
}

function store(principal: string, pages: RecentPage[]): void {
  try {
    window.localStorage.setItem(recentStorageKey(principal), JSON.stringify(pages.slice(0, recentLimit)));
  } catch {
    // Storage may refuse; the list is a convenience.
  }
}

/** A page was opened: it goes to the top, once, keeping the name a record page gave it before. */
export function rememberPage(principal: string, page: Omit<RecentPage, "label">): RecentPage[] {
  const pages = readRecent(principal);
  const before = pages.find((candidate) => candidate.path === page.path);
  const next = [{ ...page, ...(before?.label !== undefined ? { label: before.label } : {}) }, ...pages.filter((candidate) => candidate.path !== page.path)];
  store(principal, next);
  return next;
}

/** A record page names itself once it has loaded. */
export function nameRecent(principal: string, path: string, label: string): void {
  const pages = readRecent(principal);
  if (pages.some((page) => page.path === path && page.label !== label)) {
    store(
      principal,
      pages.map((page) => (page.path === path ? { ...page, label } : page)),
    );
  }
}

/** A record page gives its recent entry the record's name. */
export function useRecentName(label: string | undefined): void {
  const { pathname } = useLocation();
  const principal = useSession().me?.id;
  useEffect(() => {
    if (principal !== undefined && label !== undefined && label !== "") {
      nameRecent(principal, pathname, label);
    }
  }, [principal, pathname, label]);
}
