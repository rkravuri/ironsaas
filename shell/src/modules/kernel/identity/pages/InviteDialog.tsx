import { useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { useNavigate } from "react-router";
import { useQuery } from "@tanstack/react-query";
import { Alert, Button, CopyButton, Group, Stack, Text, TextInput } from "@mantine/core";
import { IconCheck, IconCopy } from "@tabler/icons-react";
import { z } from "zod";
import { fetchUnits } from "@/api/authorization";
import { createUser, type CreatedUser } from "@/api/identity";
import { useAccessAt } from "@/auth/access";
import { useSession } from "@/auth/session-context";
import { Form, useZodForm } from "@/components/form";
import { FormDialog, LoadingState } from "@/components/panels";
import { UnitPicker } from "@/modules/kernel/console/pages/UnitPicker";

const manage = "identity.users.manage";

interface InviteValues extends Record<string, unknown> {
  displayName: string;
  loginId: string;
  homeUnitId: string;
}

/**
 * Invite a person (D4, TF-018): a name, the login identifier and the home unit on the tree, the unit the inviter works
 * for unless they choose another (ADR-0038; their home unit in the view of all their units). The unit must be one where
 * /me/access allows `identity.users.manage`; the API checks it again, and its field problems land on their fields. The
 * answer shows the invitation link once, to copy and send by hand until email delivery (S4); closing it opens the new
 * user's record.
 */
export function InviteDialog({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const session = useSession();
  const [created, setCreated] = useState<CreatedUser | undefined>();
  const canSeeUnits = session.permissions.includes("authz.units.read");
  const units = useQuery({
    queryKey: ["authz", "organisation-units"],
    queryFn: ({ signal }) => fetchUnits(signal),
    enabled: canSeeUnits,
  });
  const home = session.me?.home_unit ?? null;
  const working = (session.me?.all_units ? null : session.me?.working_unit) ?? home;
  const schema = useMemo(
    () =>
      z.object({
        displayName: z.string().trim().min(1, t("users.invite.nameRequired")).max(200, t("users.invite.nameTooLong")),
        loginId: z.string().trim().min(1, t("users.invite.loginRequired")).max(254, t("users.invite.loginTooLong")),
        homeUnitId: z.string().min(1, t("users.invite.unitRequired")),
      }),
    [t],
  );
  const form = useZodForm<InviteValues>({
    schema,
    initialValues: { displayName: "", loginId: "", homeUnitId: working?.id ?? "" },
  });
  const unit = form.getValues().homeUnitId;
  const access = useAccessAt(unit !== "" ? { unit } : undefined, [manage]);
  const refused = unit !== "" && !access.pending && !access.can(manage);
  const unitError =
    typeof form.errors.homeUnitId === "string"
      ? form.errors.homeUnitId
      : refused
        ? t("users.invite.unitRefused")
        : undefined;

  const submit = async (values: InviteValues) => {
    if (refused) {
      form.setFieldError("homeUnitId", t("users.invite.unitRefused"));
      return;
    }
    setCreated(
      await createUser({
        display_name: values.displayName.trim(),
        login_id: values.loginId.trim(),
        home_unit_id: values.homeUnitId,
      }),
    );
  };

  const openRecord = () => {
    if (created) {
      void navigate(`/users/${created.user.id}`);
    }
    onClose();
  };

  return (
    <FormDialog
      opened
      title={created ? t("users.invite.doneTitle", { name: created.user.display_name }) : t("users.invite.title")}
      onClose={created ? openRecord : onClose}
    >
      {created ? (
        <InvitationLink created={created} onDone={openRecord} />
      ) : (
        <Form
          form={form}
          onSubmit={submit}
          submitLabel={t("users.invite.submit")}
          secondary={{ label: t("dialog.cancel"), onClick: onClose }}
        >
          <TextInput label={t("users.displayName")} data-autofocus {...form.getInputProps("displayName")} />
          <TextInput
            label={t("users.loginId")}
            description={t("users.invite.loginHint")}
            autoComplete="off"
            {...form.getInputProps("loginId")}
          />
          {!canSeeUnits ? (
            <TextInput
              label={t("users.homeUnit")}
              value={working?.name ?? ""}
              readOnly
              description={t("users.invite.ownUnit")}
              error={unitError}
            />
          ) : units.isPending ? (
            <LoadingState height={120} />
          ) : (
            <UnitPicker
              label={t("users.homeUnit")}
              units={units.data?.data ?? []}
              value={unit}
              onChange={(id) => form.setFieldValue("homeUnitId", id)}
              error={unitError}
              disabled={(candidate) => candidate.status !== "active"}
            />
          )}
        </Form>
      )}
    </FormDialog>
  );
}

/** The link, once: to copy and send by hand. It is in this dialog's memory only, so it is gone when the dialog closes. */
function InvitationLink({ created, onDone }: { created: CreatedUser; onDone: () => void }) {
  const { t } = useTranslation();
  const field = useRef<HTMLInputElement>(null);
  const link = new URL(created.invitation_link, window.location.origin).toString();
  const expires = new Date(created.invitation_expires_at).toLocaleString();
  // The form that had focus is gone: the link takes it, selected, ready to copy.
  useEffect(() => {
    field.current?.focus();
    field.current?.select();
  }, []);
  return (
    <Stack data-invitation>
      <Text size="sm">{t("users.invite.created", { name: created.user.display_name })}</Text>
      <TextInput
        ref={field}
        label={t("users.invite.link")}
        value={link}
        readOnly
        onFocus={(event) => event.currentTarget.select()}
      />
      <CopyButton value={link}>
        {({ copied, copy }) => (
          <Group gap="sm">
            <Button
              variant="default"
              onClick={copy}
              leftSection={
                copied ? <IconCheck size={16} aria-hidden="true" /> : <IconCopy size={16} aria-hidden="true" />
              }
            >
              {copied ? t("users.invite.copied") : t("users.invite.copy")}
            </Button>
          </Group>
        )}
      </CopyButton>
      <Alert color="info">{t("users.invite.byHand", { when: expires })}</Alert>
      <Group justify="flex-end">
        <Button onClick={onDone}>{t("users.invite.openRecord")}</Button>
      </Group>
    </Stack>
  );
}
