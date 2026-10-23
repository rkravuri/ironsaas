import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { Link, useLocation } from "react-router";
import { Alert, Anchor } from "@mantine/core";
import { onChangeHeld } from "@/api/client";

/**
 * A change the person made that waits for a second administrator (TF-068 AC1, ADR-0052), from any page and any kind of
 * control, a form or a button: the frame says so in the information colour, with a link to the change, so a page that
 * shows the answer as it shows a refusal still leaves the person knowing what happened. It goes when they leave the page.
 */
export function HeldChangeNotice() {
  const { t } = useTranslation();
  const { pathname } = useLocation();
  const [held, setHeld] = useState<{ id: string | undefined; on: string } | undefined>();
  const where = useRef(pathname);
  useEffect(() => {
    where.current = pathname;
  }, [pathname]);
  useEffect(() => onChangeHeld((error) => setHeld({ id: error.changeId, on: where.current })), []);
  if (held === undefined || held.on !== pathname) {
    return null;
  }
  return (
    <Alert
      color="info"
      role="status"
      mb="md"
      title={t("changes.held.title")}
      withCloseButton
      onClose={() => setHeld(undefined)}
      closeButtonLabel={t("dialog.close")}
      data-change-held
    >
      {t("changes.held.banner")}
      {held.id !== undefined && (
        <Anchor
          component={Link}
          to={`/console/changes/${held.id}`}
          display="block"
          mt={4}
          c="inherit"
          underline="always"
          fw={600}
        >
          {t("changes.held.open")}
        </Anchor>
      )}
    </Alert>
  );
}
