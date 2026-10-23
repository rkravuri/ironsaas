import { useTranslation } from "react-i18next";
import { Link } from "react-router";
import { Alert, Anchor, Text } from "@mantine/core";
import { IconShieldLock } from "@tabler/icons-react";
import { useSession } from "@/auth/session-context";
import { enrolNewFactorPath } from "@/auth/mfa-problems";
import { formatDate } from "@/i18n/dates";

/**
 * During the grace a tenant may give (ADR-0047): one calm line in the page's flow saying by when the person must add a
 * second factor, and where. /me answers the date only while the grace runs; after it, the sign-in itself asks for
 * enrolment before any page.
 */
export function MfaEnrolBanner() {
  const { t } = useTranslation();
  const { me } = useSession();
  if (!me?.mfa_enrol_by) {
    return null;
  }
  return (
    <Alert
      role="status"
      variant="light"
      color="warning"
      mb="md"
      icon={<IconShieldLock size={18} aria-hidden="true" />}
      data-mfa-enrol-by
    >
      <Text size="sm">
        {t("mfa.banner.text", { when: formatDate(me.mfa_enrol_by) })}{" "}
        {/* The warning's own text colour, underlined: the link colour on the warning's background is too faint (axe). */}
        <Anchor component={Link} to={enrolNewFactorPath} size="sm" c="inherit" underline="always" fw={600}>
          {t("mfa.banner.link")}
        </Anchor>
      </Text>
    </Alert>
  );
}
