import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { Link, useLocation } from "react-router";
import { Alert, Anchor, Box, Container, Group, Paper, Stack, Text, Title } from "@mantine/core";
import { ApiError } from "@/api/client";
import { signInWithLink } from "@/api/identity";
import { LoadingState } from "@/components/panels";
import { Welcome } from "@/design/illustrations";
import { TenantMark } from "@/design/patterns";
import { leaveFor } from "./leave";

type PageState = { kind: "signing-in" } | { kind: "error"; message: string; correlationId: string | undefined };

/**
 * The page behind a one-time link from the operator's path: /sign-in-link#{code} (ADR-0049), an impersonation the tenant
 * allows or a break-glass account's. The code is in the fragment, which the browser never sends to a server, and goes to
 * the API in the body, once: a link works once, within 15 minutes. Signed in, the whole shell starts again as the new
 * principal, so nothing of an earlier session stays in memory. It wears the sign-in page's frame: the drawing, the
 * tenant's mark beside the title and as a texture behind the card (T5's review).
 */
export function SignInLinkPage() {
  const { t } = useTranslation();
  const location = useLocation();
  const code = location.hash.slice(1);
  const hostname = window.location.hostname;
  const [state, setState] = useState<PageState>(() =>
    code === ""
      ? { kind: "error", message: t("signInLink.invalid"), correlationId: undefined }
      : { kind: "signing-in" },
  );
  // The link works once: a second call, such as development's double effect, would find it used.
  const sent = useRef(false);

  useEffect(() => {
    if (sent.current || code === "") {
      return;
    }
    sent.current = true;
    signInWithLink(code)
      .then(() => leaveFor("/"))
      .catch((error: unknown) =>
        setState({
          kind: "error",
          message:
            error instanceof ApiError && error.errorCode === "identity.sign_in_link.invalid"
              ? t("signInLink.invalid")
              : error instanceof ApiError
                ? error.message
                : t("errors.unexpected"),
          correlationId: error instanceof ApiError ? error.correlationId : undefined,
        }),
      );
  }, [code, t]);

  return (
    <Container size={420} py="xl">
      <Stack align="center" gap="xs" mb="md">
        <Welcome decorative size="md" />
        <Group gap="sm" justify="center" wrap="nowrap">
          <TenantMark hostname={hostname} size={32} />
          <Title order={2} variant="display">
            {t("signInLink.title")}
          </Title>
        </Group>
        <Text c="dimmed" size="sm" ta="center">
          {t("app.tenant", { hostname })}
        </Text>
      </Stack>
      {/* The mark as a texture: behind the opaque card at 7 percent, so it never sits under text (brief section 4). */}
      <Box pos="relative">
        <TenantMark
          hostname={hostname}
          size={400}
          opacity={0.07}
          style={{ position: "absolute", left: "50%", top: 0, transform: "translateX(-50%)", pointerEvents: "none" }}
        />
        <Paper withBorder shadow="sm" p="lg" pos="relative">
          {state.kind === "signing-in" ? (
            <Stack align="center" gap="sm">
              <LoadingState height={80} />
              <Text size="sm" role="status">
                {t("signInLink.signingIn")}
              </Text>
            </Stack>
          ) : (
            <Stack>
              <Alert color="danger" role="alert">
                {state.message}
                {state.correlationId && (
                  <Text size="xs" c="dimmed" mt={4}>
                    {t("errors.correlation", { id: state.correlationId })}
                  </Text>
                )}
              </Alert>
              <Anchor component={Link} to="/login">
                {t("login.title")}
              </Anchor>
            </Stack>
          )}
        </Paper>
      </Box>
    </Container>
  );
}
