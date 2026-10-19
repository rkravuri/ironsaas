import { IconUsers, IconId } from "@tabler/icons-react";
import type { ModuleManifest } from "@/modules/registry";
import { UsersPage } from "./pages/UsersPage";
import { UserRecordPage } from "./pages/UserRecordPage";
import { ProfilePage } from "./pages/ProfilePage";

/**
 * The Identity module's manifest: permissions are the API's names (TF-030), so the filter here matches the filter there.
 * The shell filters by the permissions held anywhere; the API keeps the rows of the reader's scope (ADR-0031).
 */
export const identityManifest: ModuleManifest = {
  name: "identity",
  titleKey: "modules.identity.title",
  routes: [
    { path: "/users", permission: "identity.users.read", element: <UsersPage />, titleKey: "modules.identity.users" },
    { path: "/users/:id", permission: "identity.users.read", element: <UserRecordPage />, titleKey: "modules.identity.users" },
    { path: "/me", permission: null, element: <ProfilePage />, titleKey: "modules.identity.profile" },
  ],
  navigation: [
    { labelKey: "modules.identity.users", path: "/users", permission: "identity.users.read", icon: <IconUsers size={16} /> },
    { labelKey: "modules.identity.profile", path: "/me", permission: null, icon: <IconId size={16} /> },
  ],
};
