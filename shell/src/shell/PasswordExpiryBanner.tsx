import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Link } from "react-router";
import { Alert, Anchor, Text } from "@mantine/core";
import { IconClockExclamation } from "@tabler/icons-react";
import { usePasswordPolicy } from "@/auth/password-policy";
import { useSession } from "@/auth/session-context";

const day = 24 * 60 * 60 * 1000;

/**
 * The warning before a password reaches the tenant's maximum age (TF-012): one calm line in the page's flow during the
 * last days before it, saying when and where to change it, with an icon beside the words. When the age is off, /me
 * answers no date and nothing shows. At the date itself the sign-in asks for a new password.
 */
export function PasswordExpiryBanner() {
  const { t } = useTranslation();
  // The time the page was drawn: the warning is about days, so it need not move while the page is open.
  const [now] = useState(() => Date.now());
  const { me } = useSession();
  // Asked only when there is a date to warn about: every page carries the banner.
  const policy = usePasswordPolicy({ enabled: Boolean(me?.password_expires_at) });
  const expiresAt = me?.password_expires_at ? Date.parse(me.password_expires_at) : Number.NaN;
  if (Number.isNaN(expiresAt) || expiresAt - now > policy.warning_days * day) {
    return null;
  }
  const days = Math.max(0, Math.ceil((expiresAt - now) / day));
  return (
    <Alert
      role="status"
      variant="light"
      color="warning"
      mb="md"
      icon={<IconClockExclamation size={18} aria-hidden="true" />}
      data-password-expiry
    >
      <Text size="sm">
        {t("password.expiry.text", { count: days, when: new Date(expiresAt).toLocaleDateString() })}{" "}
        {/* The warning's own text colour, underlined: the link colour on the warning's background is too faint (axe). */}
        <Anchor component={Link} to="/me" size="sm" c="inherit" underline="always" fw={600}>
          {t("password.expiry.link")}
        </Anchor>
      </Text>
    </Alert>
  );
}
