import { z } from "zod";
import { listSchema, request } from "./client";

export const SessionSchema = z.object({
  id: z.string(),
  expires_at: z.string(),
  idle_expires_at: z.string(),
});

/** GET /api/v1/me: who is signed in, what they may do, and when the session ends. */
export const MeSchema = z.object({
  id: z.string(),
  login_id: z.string(),
  display_name: z.string(),
  type: z.string(),
  permissions: z.array(z.string()),
  session: SessionSchema,
});
export type Me = z.infer<typeof MeSchema>;

export const UserSchema = z.object({
  id: z.string(),
  login_id: z.string(),
  display_name: z.string(),
  type: z.string(),
  status: z.string(),
  email: z.string().nullable(),
  last_login_at: z.string().nullable(),
  created_at: z.string(),
  updated_at: z.string(),
  version: z.number(),
});
export type User = z.infer<typeof UserSchema>;
export const UserListSchema = listSchema(UserSchema);
export type UserList = z.infer<typeof UserListSchema>;

export const fetchMe = (signal?: AbortSignal) => request("/api/v1/me", { schema: MeSchema, ...(signal ? { signal } : {}) });

export const login = (loginId: string, password: string) =>
  request("/auth/login", { method: "POST", body: { login_id: loginId, password } });

export const logout = () => request("/auth/logout", { method: "POST" });

export const acceptInvitation = (token: string, password: string, displayName: string | undefined) =>
  request(`/auth/invitations/${encodeURIComponent(token)}/accept`, {
    method: "POST",
    body: { password, display_name: displayName },
  });

export const fetchUsers = (query: string, signal?: AbortSignal) =>
  request(`/api/v1/users${query ? `?${query}` : ""}`, { schema: UserListSchema, ...(signal ? { signal } : {}) });
