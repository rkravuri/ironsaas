# Iron.Kernel.Flags

Kernel module (ADR-0029). Schema `kernel_flags`. The unit's side of feature flags: the rules read from the store, kept for a restart, evaluated through OpenFeature, checked in the API, and handed to the shell (TF-050, TF-051, TF-054, TF-056; ADR-0035). The control plane's side (rules, their history, kill switches, publishing, the report) is in `Iron.Kernel.Tenancy`.

Placement test: flags know nothing about any business domain, so the module is Kernel.

| Folder | Holds |
|---|---|
| `Contracts/` | `IFlagClients` (an OpenFeature client with the request's evaluation context), `FlagContextKeys`, `FlagRulesDocument` with `FlagRuleEntry`, `FlagScope`, `FlagCondition` and `RolloutSubject` (the store's format, written by the control plane), `FlagAttributes`, `ExampleFlags`, `FlagsAuditEvents` |
| `Data/` | `FlagsDbContext`, `LastKnownFlagRules` (one row: both paths' documents and when the store last confirmed them), the design-time factory, generated `Migrations/` |
| `Services/` | `FlagsOptions`, `IFlagRuleSource` (`StoreFlagRuleSource`, `ConfigurationFlagRuleSource`), `FlagRulesCache`, `FlagRefresher` (the read loop, the last known rules, the alert), `FlagRules` and `StableHash` (the evaluation order), `IronFlagProvider` (the OpenFeature provider), `OpenFeatureBinding`, `FlagSubjects` and `FlagClients` (the evaluation context), `FlagGate` (the API's filters), `FlagsMetrics`, `FlagsHealthCheck` |
| `Api/` | `FlagsContextPart`: the `flags` part of `GET /api/v1/me/context` |

## Declarations

A flag is declared in its module's code (`IModule.Flags`): key, type (boolean, string, number, JSON), kind (release, experiment, operational, permission-like), purpose, owner, planned removal date (required for release and experiment flags) and default. A declaration without an owner does not construct, so neither the build nor the catalogue accepts it. The family `kill.<path>` is reserved: the kernel declares the kill switches (`KillSwitches` in the vocabulary: email, webhooks, sms, ai, push, imports), every host has them, and a module that declares one is refused. This module declares two harmless examples, `flags.example_release` and `flags.example_tone`. `make catalogue` writes every flag into the catalogue the control plane carries.

## How a unit knows

- The control plane writes the global and region rules of every flag to `platform/flags`, one write for the whole platform, and a tenant's tenant, unit and user rules to `tenants/<slug>/flags`, with the tenant's region and plan. The unit's policy reads both.
- `FlagRefresher` reads both every 30 seconds (`Flags:RefreshSeconds`, at most 60) and once before the host serves, after binding the provider. New rules replace the old in memory and in `kernel_flags.last_known`.
- **Last known before defaults.** A failed read keeps the rules the unit has, however old, so a kill switch that is on stays on through an outage and a restart during it. Only a unit that never knew any rules evaluates on the code defaults, and that is the alert: an error line starting `ALERT: no flag rules are known` (once, then hourly), the gauge `flags.unknown` at 1 (meter `Iron.Flags`, beside `flags.age`, `flags.refresh_failures` and `flags.refusals`), and the readiness check `flags` Degraded. A path with nothing at it is an answer: no rules there.
- A host without a store (the control plane, the fleet, `make run-api`) reads `Flags:Rules` (global rules: `Flag`, `Value` as JSON, `Reason`), with `Flags:Source=configuration`.

## How a flag is evaluated

The most specific scope with a matching rule wins: user, then unit (the principal's home unit before the units above it), then tenant, region, global. Within a scope the rules are tried in their order; the first whose target and condition match decides. A condition is a percentage of tenants or of users by a stable hash (SHA-256 of the flag key and the subject's id, 10,000 buckets: the same tenant or person always lands the same way), or an attribute among values (`principal_type`, `plan`, `region`). No match: the declaration's default. A rule whose value is not of the flag's type is skipped.

The evaluation context (`FlagSubjects`) carries the tenant, its region and plan, the principal's id (the targeting key) and type, and the home unit with the units above it.

## OpenFeature

The provider is chosen by `Flags:Provider`; `iron` (`IronFlagProvider`, over the rules) is the only one today. An unknown provider, a `Flags:Source` that is neither `store` nor `configuration`, or `store` without a configured store, stops the host at start with a message that says which; so does a provider that OpenFeature reports as not ready after binding (`OpenFeatureBinding`). Each host binds its provider under a domain of its own in OpenFeature's API. Product code injects `IFlagClients` and asks OpenFeature's client: `GetBooleanValueAsync`, `GetStringValueAsync`, `GetDoubleValueAsync`, `GetObjectValueAsync`. An undeclared flag answers FLAG_NOT_FOUND with the caller's default; a wrong type TYPE_MISMATCH.

## In the API

`.RequireFlagOff("kill.imports")` and `.RequireFlag("area.flag")` (Iron.Kernel.Api) are checked after authentication and the entitlements and before the permission. A kill switch that is on answers 503 `flags.killed` with `Retry-After: 30`, the flag and the operator's reason; a flag that is off answers 404 `flags.disabled`. Every refusal is logged, counted and audited as `flags.access_denied`. Role import requires `kill.imports` off.

## For the shell

`GET /api/v1/me/context` has a `flags` part: `known` (false while on the code defaults), `confirmed_at`, every flag of this build evaluated for the person (`key`, `type`, `value`, `reason`), and, for an administrator only (a principal who holds a `.manage` permission for the whole tenant), the kill switches that are on with their path, since when and the operator's reason. The shell evaluates through `@openfeature/web-sdk` over these values with one hook, `useFlag(key, default)` (`shell/src/auth/flags.ts`), and shows the kill switches in a banner above the page; it never decides security by a flag.

Model change: edit the entity, then `make ef-add MODULE=Flags NAME=<WhatChanged>`, `make format`, and commit `Data/Migrations`.
