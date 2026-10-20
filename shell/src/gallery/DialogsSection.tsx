import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Button, Group, Stack, Text, TextInput, Title } from "@mantine/core";
import { z } from "zod";
import { Form, useZodForm } from "@/components/form";
import { AlertDialog, ConfirmDialog, FormDialog } from "@/components/panels";

const schema = z.object({ name: z.string().min(1, "Enter a name.") });

/** The three dialogs: a question with one answer, a form that keeps its one primary action, and an alert that interrupts. */
export function DialogsSection() {
  const { t } = useTranslation();
  const [confirm, setConfirm] = useState(false);
  const [rename, setRename] = useState(false);
  const [alert, setAlert] = useState(false);
  const form = useZodForm({ schema, initialValues: { name: "" } });
  return (
    <section aria-labelledby="gallery-dialogs">
      <Stack gap="lg">
        <Title order={2} id="gallery-dialogs">
          {t("gallery.sections.dialogs")}
        </Title>
        <Text>{t("gallery.dialogs.intro")}</Text>
        <Group>
          <Button variant="default" onClick={() => setConfirm(true)}>
            {t("gallery.dialogs.openConfirm")}
          </Button>
          <Button variant="default" onClick={() => setRename(true)}>
            {t("gallery.dialogs.openForm")}
          </Button>
          <Button variant="default" onClick={() => setAlert(true)}>
            {t("gallery.dialogs.openAlert")}
          </Button>
        </Group>
        <AlertDialog
          opened={alert}
          title={t("session.warning.title")}
          message={t("session.warning.message", { time: "14:30" })}
          actions={
            <>
              <Button variant="default" onClick={() => setAlert(false)}>
                {t("app.signOut")}
              </Button>
              <Button data-autofocus onClick={() => setAlert(false)}>
                {t("session.warning.stay")}
              </Button>
            </>
          }
        />
        <ConfirmDialog
          opened={confirm}
          title={t("gallery.dialogs.confirmTitle")}
          message={t("gallery.dialogs.confirmMessage")}
          confirmLabel={t("gallery.dialogs.confirmLabel")}
          danger
          onConfirm={() => setConfirm(false)}
          onCancel={() => setConfirm(false)}
        />
        <FormDialog opened={rename} title={t("gallery.dialogs.formTitle")} onClose={() => setRename(false)}>
          <Form
            form={form}
            onSubmit={() => {
              setRename(false);
              return Promise.resolve();
            }}
            submitLabel={t("gallery.form.save")}
            secondary={{ label: t("gallery.form.cancel"), onClick: () => setRename(false) }}
          >
            <TextInput label={t("gallery.dialogs.name")} data-autofocus {...form.getInputProps("name")} />
          </Form>
        </FormDialog>
      </Stack>
    </section>
  );
}
