# Iron.Kernel.Settings

Kernel module (ADR-0029). Schema `kernel_settings`. Settings that belong to the tenant: declared in code, set for the tenant, an organisation unit or a person, resolved by one service, audited, and handed to the console and the shell (TF-057 to TF-059, TF-221; ADR-0036). Unlike entitlements and flags they live in the unit's own database and do not travel through the store.

Placement test: settings know nothing about any business domain, so the module is Kernel.

| Folder | Holds |
|---|---|
| `Contracts/` | `ISettings` and `ISecretSettings`, `SettingContext` (a person, a unit or the tenant), `ResolvedSetting`, `SettingsPermissions`, `SettingsAuditEvents`, `SettingsErrors`, and `ShellSettings` (the shell's own preferences) |
| `Data/` | `SettingsDbContext`, `SettingValue` (one value per setting and place), `SettingChange` (append-only history), the design-time factory, generated `Migrations/` |
| `Services/` | `SettingsOptions`, `SettingsCache` and `SettingResolution` (the order), `SettingsRefresher` (the 30-second read and the alert), `SettingsService` (`ISettings`), `SecretSettings` and `SecretCache`, `SettingsWriter` (save and reset), `SettingsConsole` (the console's view), `SettingsMetrics`, `SettingsHealthCheck` |
| `Api/` | `SettingsEndpoints` (`/api/v1/settings`) and `SettingsContextPart` (the `settings` part of `/me/context`) |

## Declarations

A setting is a typed static definition in its module's Contracts (`Setting<T>` in the vocabulary), listed in its `IModule.Settings`: key, type (text, number, boolean, choice, JSON, secret reference), default, the rule a value keeps (a range, a length, a pattern with words, the choices, or a function with words), the scopes a value may be set at (tenant, unit, user), whether the shell may see it, and a description. The definition is the accessor: `await settings.GetAsync(IdentitySettings.SessionIdleMinutes, SettingContext.Tenant, ct)` answers an `int`, and a key nobody declared does not compile (TF-059 AC1). The default in code is the system level; an operator-wide override is not part of S2. `make catalogue` writes every setting into the catalogue and the shell's typed keys (`shell/src/api/setting-keys.ts`).

## Resolution

`ISettings` resolves for a context:

- a person (`SettingContext.ForPerson`): their own value, then their home unit and the units above it, nearest first, then the tenant, then the default; for the signed-in person, the unit they work for instead of their home unit (TF-222 AC3, ADR-0038);
- a unit (`SettingContext.ForUnit`, for a record's owning unit, TF-221): the unit and the units above it, then the tenant, then the default;
- the tenant (`SettingContext.Tenant`).

A level the declaration does not allow is skipped, and so is a stored value its rule no longer accepts. Each process keeps every value of the tenant in a cache read before the host serves and every 30 seconds (`Settings:RefreshSeconds`, at most 30), and at once after a change it saved, so a change reaches every process within 30 seconds without a restart (TF-059 AC2). The tree is asked only when a value is set at some unit for the key. A failed read keeps the last values; after five minutes of failures (`Settings:AlertAfterMinutes`) the alert is raised: an error line starting `ALERT: the settings have not refreshed`, the gauge `settings.stale` at 1 (meter `Iron.Settings`, beside `settings.age`, `settings.refresh_failures` and `settings.secret_reads`), and the readiness check `settings` Degraded (TF-059 AC4).

A unit that moves keeps its own values and takes the rest from its new parent: values are keyed by the unit's id. In a merge (ADR-0038), `UnitSettingsMerge` moves the values of the unit that goes to the unit that stays, drops those the unit that stays has its own value of (listed as conflicts in the preview), writes a reset and a set to the history, and refreshes the cache after the merge commits.

## Secret references

The value stored for a secret-reference setting is a path in the store, under the unit's own `tenants/<slug>/settings/`, which the unit's policy reads; never the secret. The API and the shell see the path only, and `/me/context` leaves secret references out. Code reads the secret through `ISecretSettings.ReadSecretAsync`, which fetches the key `value` at the path from the store and keeps it at most five minutes (`Settings:SecretCacheSeconds`, TF-059 AC3).

## The API

- `GET /api/v1/settings?scope=tenant` or `?scope=unit&unit=<id>`: every setting the place may hold, with the effective value there, where it comes from (the level, the unit with its path of names, who set it when), and whether it is set at this place. Needs `settings.values.read` at the place.
- `GET /api/v1/settings/{key}/history?scope=...`: every change at the place, newest first, with before, after, who and when.
- `PUT /api/v1/settings/{key}` with `{ "scope": "tenant" | "unit" | "user", "unit_id": ..., "value": ... }`, and `DELETE /api/v1/settings/{key}?scope=...` (reset: the value at the place is removed and the inherited one applies). A tenant's value needs `settings.values.manage` tenant-wide, a unit's at the unit or above it (TF-221 AC3); a person sets their own values without a permission, and the user scope is always the caller's.

A value that breaks the rule answers 400 `settings.value.invalid` with the rule in words on the field `value` (TF-057 AC2); a place the setting does not allow, `settings.scope.not_allowed`; an unknown key, 404 `settings.setting.unknown`. Every change writes the value, an append-only history row and the audit event `settings.value_changed` or `settings.value_reset` (key, scope, unit or user, before, after) in one transaction (TF-058 AC1).

## For the shell

The `settings` part of `GET /api/v1/me/context` has the effective value and level of every setting whose declaration lets the shell see it, for the signed-in person. The shell reads them with `useSetting(key)`, typed by the generated keys.

Model change: edit the entity, then `make ef-add MODULE=Settings NAME=<WhatChanged>`, `make format`, and commit `Data/Migrations`.
