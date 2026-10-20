import { useQuery } from "@tanstack/react-query";
import { fetchAccess, type AccessTarget } from "@/api/identity";
import { useSession } from "./session-context";

export interface AccessAt {
  /** True while the answer is on its way; actions stay hidden until it comes. */
  pending: boolean;
  can: (permission: string) => boolean;
}

const none: readonly string[] = [];

/**
 * What the signed-in principal may do at one unit or record (GET /me/access, ADR-0031), for showing the actions that
 * will work there. Asked only when the principal holds the permission somewhere at all; the API checks every action again.
 */
export function useAccessAt(target: AccessTarget | undefined, permissions: readonly string[]): AccessAt {
  const session = useSession();
  const relevant = permissions.some((permission) => session.permissions.includes(permission));
  const query = useQuery({
    queryKey: ["me", "access", target ?? null],
    queryFn: ({ signal }) => fetchAccess(target as AccessTarget, signal),
    enabled: target !== undefined && relevant,
    staleTime: 30_000,
  });
  const granted = query.data?.permissions ?? none;
  return {
    pending: relevant && target !== undefined && query.isPending,
    can: (permission) => granted.includes(permission),
  };
}
