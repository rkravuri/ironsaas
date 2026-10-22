import { z } from "zod";
import { listSchema, request } from "./client";

/** The session's times (TF-021): when it started, its absolute limit, and when it ends without activity. */
export const SessionSchema = z.object({
  id: z.string(),
  started_at: z.string().nullable().optional(),
  expires_at: z.string(),
  idle_expires_at: z.string(),
});

/** The home unit as /me names it (TF-034); the tree has the rest. */
export const HomeUnitSchema = z.object({ id: z.string(), name: z.string(), code: z.string() });

const DutyNameSchema = z.object({ code: z.string(), name: z.string() });

/** Whom the session acts for (ADR-0037): their rights for these duties, until then. */
export const ActingForSchema = z.object({
  id: z.string(),
  name: z.string(),
  duties: z.array(DutyNameSchema),
  until: z.string(),
});
export type ActingFor = z.infer<typeof ActingForSchema>;

/** An active delegation as /me shows it, either way. */
export const ActiveDelegationSchema = z.object({
  delegator_id: z.string(),
  delegator_name: z.string(),
  delegate_id: z.string(),
  delegate_name: z.string(),
  duties: z.array(DutyNameSchema),
  until: z.string(),
});
export type ActiveDelegation = z.infer<typeof ActiveDelegationSchema>;

/** One unit on a path from the root. */
export const UnitStepSchema = z.object({ id: z.string(), name: z.string() });

/** A unit the person may work for (ADR-0038): its depth, and its path from the root with the unit last. */
export const WorkingUnitChoiceSchema = z.object({
  id: z.string(),
  name: z.string(),
  code: z.string(),
  depth: z.number(),
  path: z.array(UnitStepSchema),
});
export type WorkingUnitChoice = z.infer<typeof WorkingUnitChoiceSchema>;

/**
 * GET /api/v1/me: who is signed in, what they may do, their home unit, and when the session ends. `permissions` are
 * held for the whole tenant; `permissions_anywhere` at any scope, which the navigation follows (ADR-0031). While the
 * session acts for someone, both are that person's for the delegated duties (ADR-0037), and `acting_for` names them;
 * `delegations` are the active ones the person gives and holds. `working_unit` is the unit new records and settings
 * follow, and lists keep to with the units below it, unless `all_units` is the consolidated view; `working_units` are
 * the units the person may choose (ADR-0038). A token has no working unit.
 */
export const MeSchema = z.object({
  id: z.string(),
  login_id: z.string(),
  display_name: z.string(),
  type: z.string(),
  permissions: z.array(z.string()),
  permissions_anywhere: z.array(z.string()),
  session: SessionSchema,
  home_unit: HomeUnitSchema.nullable().optional(),
  acting_for: ActingForSchema.nullable().optional(),
  delegations: z
    .object({ given: z.array(ActiveDelegationSchema), received: z.array(ActiveDelegationSchema) })
    .optional(),
  working_unit: HomeUnitSchema.nullable().optional(),
  all_units: z.boolean().optional(),
  working_units: z.object({ units: z.array(WorkingUnitChoiceSchema), all_units_available: z.boolean() }).optional(),
  /** When the password reaches the tenant's maximum age (TF-012); null while the age is off. */
  password_expires_at: z.string().nullable().optional(),
  /** The end of the grace a tenant may give before a second factor is required of the person (ADR-0047); null otherwise. */
  mfa_enrol_by: z.string().nullable().optional(),
  /** The tenant's policy now requires a second factor the person has not got: everything but enrolment waits for it. */
  mfa_enrolment_required: z.boolean().optional(),
  /** The company login the session's sign-in came through (ADR-0048), so signing out may offer to sign out there too. */
  signed_in_with: z.object({ provider_id: z.string(), name: z.string() }).nullable().optional(),
});
export type Me = z.infer<typeof MeSchema>;

/** One unit to work for, or all of them. */
export type WorkFor = { unitId: string } | { allUnits: true };

export const UserSchema = z.object({
  id: z.string(),
  login_id: z.string(),
  display_name: z.string(),
  type: z.string(),
  status: z.string(),
  email: z.string().nullable(),
  home_unit_id: z.string().nullable().optional(),
  last_login_at: z.string().nullable(),
  created_at: z.string(),
  updated_at: z.string(),
  version: z.number(),
});
export type User = z.infer<typeof UserSchema>;
export const UserListSchema = listSchema(UserSchema);
export type UserList = z.infer<typeof UserListSchema>;

/**
 * GET /api/v1/users/{id}: one user with the home unit by name, and until when their login is locked (TF-012), null when
 * it is not; a user outside the reader's scope answers 404.
 */
export const UserRecordSchema = UserSchema.omit({ home_unit_id: true }).extend({
  home_unit: HomeUnitSchema.nullable(),
  locked_until: z.string().nullable().optional(),
  /** How many second factors the person has (ADR-0047). */
  factors: z.number().optional(),
  /** Whether the reader holds every permission the person holds: the rule for a recovery link and a factor reset (ADR-0046). */
  takeover_allowed: z.boolean().optional(),
  /** Marked by an administrator to sign in only through a company login (TF-014 AC2). */
  sso_only: z.boolean().optional(),
  /** Signs in only through a company login because a provider they are linked to says so for everyone it links. */
  sso_only_by_provider: z.boolean().optional(),
  /** The company logins the person is linked to (ADR-0048). */
  sso_links: z
    .array(
      z.object({
        provider_id: z.string(),
        provider_name: z.string(),
        email: z.string(),
        linked_at: z.string(),
        last_sign_in_at: z.string(),
      }),
    )
    .optional(),
});
export type UserRecord = z.infer<typeof UserRecordSchema>;

/** GET /api/v1/me/access: the permissions held at one unit or record. */
export const AccessSchema = z.object({ scope: z.string(), permissions: z.array(z.string()) });
export type Access = z.infer<typeof AccessSchema>;

/**
 * Where to ask about access: a unit, or a unit with every unit below it (what changing an assignment to a unit and
 * below needs, ADR-0031), or a record with the unit it belongs to when the page knows it.
 */
export type AccessTarget =
  { unit: string; includeChildren?: boolean } | { recordType: string; recordId: string; unit?: string };

export const fetchMe = (signal?: AbortSignal) =>
  request("/api/v1/me", { schema: MeSchema, ...(signal ? { signal } : {}) });

/** Act for someone whose delegation to you is active; answers /me as it is now. */
export const actFor = (principalId: string) =>
  request("/api/v1/me/acting-for", { method: "POST", body: { principal_id: principalId }, schema: MeSchema });

/** Act as yourself again; answers /me. */
export const stopActing = () => request("/api/v1/me/acting-for", { method: "DELETE", schema: MeSchema });

/** Work for one of your units, or all of them (ADR-0038); answers /me. */
export const workFor = (choice: WorkFor) =>
  request("/api/v1/me/working-unit", {
    method: "POST",
    body: "allUnits" in choice ? { all_units: true } : { unit_id: choice.unitId },
    schema: MeSchema,
  });

/** A person as the delegate picker offers them (ADR-0037): a display name, a home unit's name, and the id to choose them by. */
export const DirectoryEntrySchema = z.object({
  id: z.string(),
  display_name: z.string(),
  home_unit: z.string().nullable(),
});
export type DirectoryEntry = z.infer<typeof DirectoryEntrySchema>;
export const DirectoryPageSchema = listSchema(DirectoryEntrySchema);

/** How many people the picker offers at once; more matches ask for more of the name. */
export const directoryPageSize = 20;

/** The first page of the active people whose name or login contains the search (two characters at least). */
export const searchDirectory = (search: string, signal?: AbortSignal) =>
  request(`/api/v1/directory?search=${encodeURIComponent(search)}&page_size=${directoryPageSize}`, {
    schema: DirectoryPageSchema,
    ...(signal ? { signal } : {}),
  });

export function accessQuery(target: AccessTarget): string {
  const params = new URLSearchParams();
  if ("recordType" in target) {
    params.set("record_type", target.recordType);
    params.set("record_id", target.recordId);
  }
  if (target.unit !== undefined) {
    params.set("unit", target.unit);
  }
  if ("includeChildren" in target && target.includeChildren === true) {
    params.set("include_children", "true");
  }
  return params.toString();
}

export const fetchAccess = (target: AccessTarget, signal?: AbortSignal) =>
  request(`/api/v1/me/access?${accessQuery(target)}`, { schema: AccessSchema, ...(signal ? { signal } : {}) });

export const logout = () => request("/auth/logout", { method: "POST" });

/** The token from the link's fragment, in the body: never in a path an access log or a trace keeps (ADR-0046). */
export const acceptInvitation = (token: string, password: string, displayName: string | undefined) =>
  request("/auth/invitations/accept", {
    method: "POST",
    body: { token, password, display_name: displayName },
  });

export const fetchUsers = (query: string, signal?: AbortSignal) =>
  request(`/api/v1/users${query ? `?${query}` : ""}`, { schema: UserListSchema, ...(signal ? { signal } : {}) });

export const fetchUser = (id: string, signal?: AbortSignal) =>
  request(`/api/v1/users/${encodeURIComponent(id)}`, { schema: UserRecordSchema, ...(signal ? { signal } : {}) });

/** POST /api/v1/users: the user created and the invitation link to pass on by hand until email delivery (S4). */
export const CreatedUserSchema = z.object({
  user: UserSchema,
  invitation_link: z.string(),
  invitation_expires_at: z.string(),
});
export type CreatedUser = z.infer<typeof CreatedUserSchema>;

export interface NewUser {
  login_id: string;
  display_name: string;
  home_unit_id: string;
}

export const createUser = (body: NewUser) =>
  request("/api/v1/users", { method: "POST", body, schema: CreatedUserSchema });

/** GET /auth/password-policy: the tenant's password rules (TF-012), anonymous, for every form that sets a password. */
export const PasswordPolicySchema = z.object({
  minimum_length: z.number(),
  maximum_length: z.number(),
  common_check: z.boolean(),
  history_depth: z.number(),
  maximum_age_days: z.number(),
  warning_days: z.number(),
});
export type PasswordPolicy = z.infer<typeof PasswordPolicySchema>;

export const fetchPasswordPolicy = (signal?: AbortSignal) =>
  request("/auth/password-policy", { schema: PasswordPolicySchema, ...(signal ? { signal } : {}) });

/** Sets a new password with a recovery link (TF-017): once, within 15 minutes; every session of the person ends. */
export const recoverPassword = (token: string, password: string) =>
  request("/auth/recovery", { method: "POST", body: { token, password } });

/** Changes your own password with the current one; the other sessions end unless you say not to (TF-017 AC3). */
export const changePassword = (currentPassword: string, newPassword: string, endOtherSessions: boolean) =>
  request("/api/v1/me/password", {
    method: "POST",
    body: { current_password: currentPassword, new_password: newPassword, end_other_sessions: endOtherSessions },
  });

/** One of your security events (TF-019): what, when, by whom (yourself, an administrator, the operator, the system), why. */
export const SecurityEventSchema = z.object({
  id: z.string(),
  kind: z.string(),
  occurred_at: z.string(),
  actor: z.string(),
  actor_name: z.string().nullable(),
  reason: z.string().nullable(),
});
export type SecurityEvent = z.infer<typeof SecurityEventSchema>;
export const SecurityEventListSchema = listSchema(SecurityEventSchema);

/** How many security events My profile shows at once, newest first. */
export const securityEventsPageSize = 50;

export const fetchSecurityEvents = (signal?: AbortSignal) =>
  request(`/api/v1/me/security-events?page_size=${securityEventsPageSize}`, {
    schema: SecurityEventListSchema,
    ...(signal ? { signal } : {}),
  });

/** A recovery link an administrator issued, shown once: its path and when it stops working. */
export const RecoveryLinkSchema = z.object({ recovery_link: z.string(), expires_at: z.string() });
export type RecoveryLink = z.infer<typeof RecoveryLinkSchema>;

/** A person reactivated, and a new invitation's link when they never set a password. */
export const ReactivatedUserSchema = z.object({
  user: UserSchema,
  invitation_link: z.string().nullable(),
  invitation_expires_at: z.string().nullable(),
});
export type ReactivatedUser = z.infer<typeof ReactivatedUserSchema>;

const userAction = (id: string, action: string) => `/api/v1/users/${encodeURIComponent(id)}/${action}`;

export const unlockUser = (id: string) =>
  request(userAction(id, "unlock"), { method: "POST", schema: UserRecordSchema });

export const issueRecoveryLink = (id: string) =>
  request(userAction(id, "recovery-link"), { method: "POST", schema: RecoveryLinkSchema });

export const deactivateUser = (id: string) =>
  request(userAction(id, "deactivate"), { method: "POST", schema: UserRecordSchema });

export const reactivateUser = (id: string) =>
  request(userAction(id, "reactivate"), { method: "POST", schema: ReactivatedUserSchema });

/** A company login as the sign-in page offers it: a button with its name (ADR-0048). */
export const SsoProviderButtonSchema = z.object({ id: z.string(), name: z.string() });
export type SsoProviderButton = z.infer<typeof SsoProviderButtonSchema>;

// Second factors and step-up (S3 T3, TF-013, TF-248, ADR-0047).

/** What a sign-in goes on to after a right password: the second factor, or enrolment; the ways open, and until when. */
export const SignInStepSchema = z.object({
  kind: z.enum(["second_factor", "enrolment"]),
  methods: z.array(z.string()),
  expires_at: z.string(),
});
export type SignInStep = z.infer<typeof SignInStepSchema>;

/**
 * A sign-in's answer: the person and the session once everything is proved, or the step that comes next with neither.
 * `enrol_new_factor` follows a sign-in by recovery code; `recovery_codes` follow the first enrolment, shown once.
 */
export const SignInAnswerSchema = z.object({
  // The shell reads the person and the session from /me after a sign-in; the answer only has to say there is one.
  principal: z.unknown().optional(),
  session: z.unknown().optional(),
  step: SignInStepSchema.nullable().optional(),
  enrol_new_factor: z.boolean().optional(),
  recovery_codes: z.array(z.string()).nullable().optional(),
  /** Where a sign-in through a company login was going (ADR-0048). */
  return_to: z.string().nullable().optional(),
  /** A re-authentication through a company login confirmed the session's person: no new session (TF-248 AC6). */
  reauthenticated: z.boolean().optional(),
});
export type SignInAnswer = z.infer<typeof SignInAnswerSchema>;

/** A second factor as the API takes it: one of a code, a recovery code, or a passkey's answer. */
export type SecondFactorProof = { code: string } | { recovery_code: string } | { passkey: unknown };

/** The options of a WebAuthn ceremony, as the browser's API takes them once their bytes are decoded. */
export const WebAuthnOptionsSchema = z.object({ options: z.record(z.string(), z.unknown()) });

export const AuthenticatorAppSetupSchema = z.object({ secret: z.string(), uri: z.string(), expires_at: z.string() });
export type AuthenticatorAppSetup = z.infer<typeof AuthenticatorAppSetupSchema>;

export const FactorSchema = z.object({
  id: z.string(),
  kind: z.string(),
  name: z.string(),
  created_at: z.string(),
  last_used_at: z.string().nullable(),
});
export type Factor = z.infer<typeof FactorSchema>;

/** My profile's factors, the recovery codes left, and what the organisation's policy asks of the person. */
export const FactorsSchema = z.object({
  factors: z.array(FactorSchema),
  recovery_codes_left: z.number(),
  required: z.boolean(),
  allowed: z.array(z.string()),
  enrol_by: z.string().nullable(),
});
export type Factors = z.infer<typeof FactorsSchema>;

export const EnrolledSchema = z.object({ factor: FactorSchema, recovery_codes: z.array(z.string()).nullable() });
export type Enrolled = z.infer<typeof EnrolledSchema>;

export const ReauthenticationSchema = z.object({
  second_factor: z.boolean(),
  methods: z.array(z.string()),
  passkey_options: z.record(z.string(), z.unknown()).nullable().optional(),
  /** False for a person who signs in only through a company login: they confirm by signing in through it again (ADR-0048). */
  password: z.boolean().optional(),
  provider: z.object({ id: z.string(), name: z.string() }).nullable().optional(),
});
export type Reauthentication = z.infer<typeof ReauthenticationSchema>;

export const signIn = (loginId: string, password: string) =>
  request("/auth/login", { method: "POST", body: { login_id: loginId, password }, schema: SignInAnswerSchema });

export const fetchSignInOptions = (signal?: AbortSignal) =>
  request("/auth/sign-in-options", {
    schema: z.object({
      passkey_sign_in: z.boolean(),
      providers: z.array(SsoProviderButtonSchema).optional(),
    }),
    ...(signal ? { signal } : {}),
  });

export const proveSecondFactor = (proof: SecondFactorProof) =>
  request("/auth/second-factor", { method: "POST", body: proof, schema: SignInAnswerSchema });

export const secondFactorPasskeyOptions = () =>
  request("/auth/second-factor/passkey-options", { method: "POST", schema: WebAuthnOptionsSchema });

export const passkeySignInOptions = () =>
  request("/auth/passkey/options", { method: "POST", schema: WebAuthnOptionsSchema });

export const passkeySignIn = (credential: unknown) =>
  request("/auth/passkey", { method: "POST", body: { credential }, schema: SignInAnswerSchema });

/** Enrolment at sign-in, before any page: the calls carry the sign-in's cookie, never a token in the body. */
export const enrolmentApp = () =>
  request("/auth/enrolment/authenticator-app", { method: "POST", schema: AuthenticatorAppSetupSchema });

export const confirmEnrolmentApp = (code: string, name: string | undefined) =>
  request("/auth/enrolment/authenticator-app/confirm", {
    method: "POST",
    body: { code, name },
    schema: SignInAnswerSchema,
  });

export const enrolmentPasskeyOptions = () =>
  request("/auth/enrolment/passkey/options", { method: "POST", schema: WebAuthnOptionsSchema });

export const enrolmentPasskey = (name: string | undefined, credential: unknown) =>
  request("/auth/enrolment/passkey", { method: "POST", body: { name, credential }, schema: SignInAnswerSchema });

export const factorsQueryKey = ["identity", "me", "factors"] as const;

export const fetchFactors = (signal?: AbortSignal) =>
  request("/api/v1/me/factors", { schema: FactorsSchema, ...(signal ? { signal } : {}) });

export const addApp = () =>
  request("/api/v1/me/factors/authenticator-app", { method: "POST", schema: AuthenticatorAppSetupSchema });

export const confirmApp = (code: string, name: string | undefined) =>
  request("/api/v1/me/factors/authenticator-app/confirm", {
    method: "POST",
    body: { code, name },
    schema: EnrolledSchema,
  });

export const addPasskeyOptions = () =>
  request("/api/v1/me/factors/passkey/options", { method: "POST", schema: WebAuthnOptionsSchema });

export const addPasskey = (name: string | undefined, credential: unknown) =>
  request("/api/v1/me/factors/passkey", { method: "POST", body: { name, credential }, schema: EnrolledSchema });

export const renameFactor = (id: string, name: string) =>
  request(`/api/v1/me/factors/${encodeURIComponent(id)}`, { method: "PATCH", body: { name }, schema: FactorSchema });

export const removeFactor = (id: string) =>
  request(`/api/v1/me/factors/${encodeURIComponent(id)}`, { method: "DELETE" });

export const regenerateRecoveryCodes = () =>
  request("/api/v1/me/recovery-codes", { method: "POST", schema: z.object({ codes: z.array(z.string()) }) });

export const reauthenticationOptions = () =>
  request("/api/v1/me/reauthentication", { method: "POST", schema: ReauthenticationSchema });

export const reauthenticate = (password: string, proof: SecondFactorProof | undefined) =>
  request("/api/v1/me/reauthenticate", { method: "POST", body: { password, ...(proof ?? {}) } });

/** An administrator removes a person's factors, with the reason their security activity keeps (TF-013 AC3). */
export const resetFactors = (id: string, reason: string) =>
  request(userAction(id, "factors/reset"), { method: "POST", body: { reason }, schema: UserRecordSchema });

// Single sign-on (S3 T4, TF-014, ADR-0048).

/** Marks a person to sign in only through a company login, or lets them use a password again. */
export const setSsoOnly = (id: string, ssoOnly: boolean) =>
  request(userAction(id, "sso-only"), { method: "PUT", body: { sso_only: ssoOnly }, schema: UserRecordSchema });

/**
 * Confirming it is you through your company login (TF-248 AC6): a fresh sign-in there, with the page to come back to,
 * and your second factor here, when you have one, given first.
 */
export const reauthenticateThroughProvider = (returnTo: string, proof: SecondFactorProof | undefined) =>
  request("/api/v1/me/reauthentication/sso", {
    method: "POST",
    body: { return_to: returnTo, ...(proof ?? {}) },
    schema: z.object({ authorization_url: z.string(), expires_at: z.string() }),
  });

/** The company login of an email's domain: the same answer for every email of a domain. */
export const discoverProvider = (email: string) =>
  request("/auth/sso/discover", {
    method: "POST",
    body: { email },
    schema: z.object({ provider: SsoProviderButtonSchema.nullable() }),
  });

/** Starts a sign-in through a company login: where to send the browser; the sign-in cookie holds the rest. */
export const startSso = (providerId: string, returnTo?: string, loginHint?: string) =>
  request("/auth/sso/start", {
    method: "POST",
    body: { provider_id: providerId, return_to: returnTo ?? null, login_hint: loginHint ?? null },
    schema: z.object({ authorization_url: z.string(), expires_at: z.string() }),
  });

/** What the company login sent the browser back with, as the callback page read it from the address. */
export interface SsoCallback {
  code: string | null;
  state: string | null;
  error: string | null;
  error_description: string | null;
}

export const completeSso = (callback: SsoCallback) =>
  request("/auth/sso/callback", { method: "POST", body: callback, schema: SignInAnswerSchema });

/** Where to sign out at a company login, when it publishes an end-session endpoint. */
export const ssoSignOutUrl = (providerId: string) =>
  request(`/auth/sso/${encodeURIComponent(providerId)}/sign-out`, { schema: z.object({ url: z.string() }) });

export const SsoRoleMappingSchema = z.object({
  group: z.string(),
  role_code: z.string(),
  unit_id: z.string().nullable(),
  include_children: z.boolean(),
});
export type SsoRoleMapping = z.infer<typeof SsoRoleMappingSchema>;

/** A provider as the console shows it: everything but its client secret, which only the store holds. */
export const SsoProviderSchema = z.object({
  id: z.string(),
  name: z.string(),
  preset: z.string(),
  discovery_url: z.string(),
  client_id: z.string(),
  allowed_domains: z.array(z.string()),
  email_claim: z.string(),
  name_claim: z.string(),
  groups_claim: z.string(),
  just_in_time: z.boolean(),
  default_role_code: z.string().nullable(),
  default_home_unit_id: z.string().nullable(),
  sso_only: z.boolean(),
  trust_provider_mfa: z.boolean(),
  role_mappings: z.array(SsoRoleMappingSchema),
  created_at: z.string(),
  updated_at: z.string(),
  version: z.number(),
});
export type SsoProvider = z.infer<typeof SsoProviderSchema>;
export const SsoProviderListSchema = listSchema(SsoProviderSchema);

/** A provider's settings as the console sends them; a null client secret on a change keeps the one in the store. */
export interface SsoProviderInput {
  name: string;
  preset: string;
  discovery_url: string;
  client_id: string;
  client_secret: string | null;
  allowed_domains: string[];
  email_claim: string;
  name_claim: string;
  groups_claim: string;
  just_in_time: boolean;
  default_role_code: string | null;
  default_home_unit_id: string | null;
  sso_only: boolean;
  trust_provider_mfa: boolean;
  role_mappings: SsoRoleMapping[];
}

export const ssoProvidersQueryKey = ["identity", "sso", "providers"] as const;

export const fetchSsoProviders = (query: string, signal?: AbortSignal) =>
  request(`/api/v1/sso/providers${query ? `?${query}` : ""}`, {
    schema: SsoProviderListSchema,
    ...(signal ? { signal } : {}),
  });

export const fetchSsoProvider = (id: string, signal?: AbortSignal) =>
  request(`/api/v1/sso/providers/${encodeURIComponent(id)}`, {
    schema: SsoProviderSchema,
    ...(signal ? { signal } : {}),
  });

export const createSsoProvider = (input: SsoProviderInput) =>
  request("/api/v1/sso/providers", { method: "POST", body: input, schema: SsoProviderSchema });

export const updateSsoProvider = (id: string, input: SsoProviderInput) =>
  request(`/api/v1/sso/providers/${encodeURIComponent(id)}`, { method: "PUT", body: input, schema: SsoProviderSchema });

export const deleteSsoProvider = (id: string) =>
  request(`/api/v1/sso/providers/${encodeURIComponent(id)}`, { method: "DELETE" });

/** One check of "Test connection", in words. */
export const SsoTestItemSchema = z.object({
  check: z.string(),
  status: z.enum(["passed", "warning", "failed"]),
  message: z.string(),
});
export type SsoTestItem = z.infer<typeof SsoTestItemSchema>;

export const testSsoProvider = (discoveryUrl: string) =>
  request("/api/v1/sso/test", {
    method: "POST",
    body: { discovery_url: discoveryUrl },
    schema: z.object({ passed: z.boolean(), items: z.array(SsoTestItemSchema) }),
  });
