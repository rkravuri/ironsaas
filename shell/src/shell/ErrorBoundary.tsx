import { Component, useEffect, type ErrorInfo, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { isRouteErrorResponse, useRouteError } from "react-router";
import { Alert, Button, Code, Container, Stack, Text, Title } from "@mantine/core";
import { ApiError } from "@/api/client";
import { Broken, Forbidden, Lost } from "@/design/illustrations";

interface ErrorViewProps {
  error: unknown;
  onRetry?: () => void;
}

/**
 * Every error page shows the correlation id when there is one (TF-071): the same id is in the API's logs and traces.
 * The drawing is decoration beside the title that says the same, so it is hidden from assistive technology.
 */
export function ErrorView({ error, onRetry }: ErrorViewProps) {
  const { t } = useTranslation();
  let title = t("errors.title");
  let message = t("errors.unexpected");
  let correlationId: string | undefined;
  let drawing = <Broken decorative size="md" />;

  if (error instanceof ApiError) {
    message = error.message;
    correlationId = error.correlationId;
    if (error.isForbidden && error.errorCode?.startsWith("entitlements.")) {
      // The tenant's subscription, not the person's permission (ADR-0034): say so, and who can change it.
      title = t("errors.notEntitledTitle");
      message = t("errors.notEntitled");
      drawing = <Forbidden decorative size="md" />;
    } else if (error.isForbidden) {
      title = t("errors.forbiddenTitle");
      message = t("errors.forbidden");
      drawing = <Forbidden decorative size="md" />;
    }
  } else if (isRouteErrorResponse(error)) {
    if (error.status === 404) {
      title = t("errors.notFoundTitle");
      message = t("errors.notFound");
      drawing = <Lost decorative size="md" />;
    } else {
      message = error.statusText || message;
    }
  } else if (error instanceof Error && import.meta.env.DEV) {
    message = error.message;
  }

  return (
    <Container size="sm" py="xl">
      <Stack>
        {drawing}
        <Title order={2}>{title}</Title>
        <Alert color="danger" role="alert">
          <Text>{message}</Text>
          {correlationId && (
            <Text size="sm" mt="xs">
              {t("errors.correlation", { id: "" })}
              <Code>{correlationId}</Code>
            </Text>
          )}
        </Alert>
        {onRetry && (
          <Button variant="light" onClick={onRetry} w="fit-content">
            {t("errors.retry")}
          </Button>
        )}
      </Stack>
    </Container>
  );
}

/** The router's errorElement: render errors and loader errors of a route land here, and in the console for whoever is looking. */
export function RouteErrorPage() {
  const error = useRouteError();
  useEffect(() => {
    console.error("A page failed to render.", error);
  }, [error]);
  return <ErrorView error={error} onRetry={() => window.location.reload()} />;
}

interface ErrorBoundaryProps {
  children: ReactNode;
}

interface ErrorBoundaryState {
  error: unknown;
}

/** The last net under the whole app, outside the router. */
export class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  state: ErrorBoundaryState = { error: undefined };

  static getDerivedStateFromError(error: unknown): ErrorBoundaryState {
    return { error };
  }

  componentDidCatch(error: unknown, info: ErrorInfo): void {
    console.error("The shell failed to render.", error, info.componentStack);
  }

  render(): ReactNode {
    if (this.state.error !== undefined) {
      return <ErrorView error={this.state.error} onRetry={() => this.setState({ error: undefined })} />;
    }
    return this.props.children;
  }
}
