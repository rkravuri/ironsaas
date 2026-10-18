import { Component, type ErrorInfo, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { isRouteErrorResponse, useRouteError } from "react-router";
import { Alert, Button, Code, Container, Stack, Text, Title } from "@mantine/core";
import { ApiError } from "@/api/client";

interface ErrorViewProps {
  error: unknown;
  onRetry?: () => void;
}

/** Every error page shows the correlation id when there is one (TF-071): the same id is in the API's logs and traces. */
export function ErrorView({ error, onRetry }: ErrorViewProps) {
  const { t } = useTranslation();
  let title = t("errors.title");
  let message = t("errors.unexpected");
  let correlationId: string | undefined;

  if (error instanceof ApiError) {
    message = error.message;
    correlationId = error.correlationId;
    if (error.isForbidden) {
      title = t("errors.forbiddenTitle");
      message = t("errors.forbidden");
    }
  } else if (isRouteErrorResponse(error)) {
    if (error.status === 404) {
      title = t("errors.notFoundTitle");
      message = t("errors.notFound");
    } else {
      message = error.statusText || message;
    }
  } else if (error instanceof Error && import.meta.env.DEV) {
    message = error.message;
  }

  return (
    <Container size="sm" py="xl">
      <Stack>
        <Title order={2}>{title}</Title>
        <Alert color="red" role="alert">
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

/** The router's errorElement: render errors and loader errors of a route land here. */
export function RouteErrorPage() {
  const error = useRouteError();
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
