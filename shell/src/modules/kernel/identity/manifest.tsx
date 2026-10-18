import { IconUsers, IconId } from "@tabler/icons-react";
import type { ModuleManifest } from "@/modules/registry";
import { UsersPage } from "./pages/UsersPage";
import { ProfilePage } from "./pages/ProfilePage";

/** The Identity module's manifest: permissions are the API's names (TF-030), so the filter here matches the filter there. */
export const identityManifest: ModuleManifest = {
  name: "identity",
  titleKey: "modules.identity.title",
  routes: [
    { path: "/users", permission: "identity.users.read", element: <UsersPage />, titleKey: "modules.identity.users" },
    { path: "/me", permission: null, element: <ProfilePage />, titleKey: "modules.identity.profile" },
  ],
  navigation: [
    { labelKey: "modules.identity.users", path: "/users", permission: "identity.users.read", icon: <IconUsers size={16} /> },
    { labelKey: "modules.identity.profile", path: "/me", permission: null, icon: <IconId size={16} /> },
  ],
};
