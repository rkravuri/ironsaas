import { useMemo, useState, type FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { useSearchParams } from "react-router";
import { useQuery } from "@tanstack/react-query";
import { Alert, Box, Button, Flex, Group, List, Paper, Select, Stack, Text, TextInput, Title } from "@mantine/core";
import { IconCircleCheck, IconCircleDashed, IconCircleMinus, IconCircleX } from "@tabler/icons-react";
import { ApiError } from "@/api/client";
import {
  explainAccess,
  fetchExplainableActions,
  fetchUnits,
  type AccessExplanation,
  type ExplainQuestion,
  type ExplainResult,
} from "@/api/authorization";
import { useSession } from "@/auth/session-context";
import { EmptyState, LoadingState } from "@/components/panels";
import { ListPage } from "@/components/templates";
import { ModuleMark } from "@/design/patterns";
import { checkWords, grantWords } from "./explain-words";
import { PersonPicker } from "./PersonPicker";
import { UnitPicker } from "./UnitPicker";

const resultIcons: Record<ExplainResult, typeof IconCircleCheck> = {
  passed: IconCircleCheck,
  failed: IconCircleX,
  not_reached: IconCircleDashed,
  not_applicable: IconCircleMinus,
};

/**
 * Why a person can or cannot perform an action (TF-039, ADR-0039): choose the person, the action by its words, and
 * the unit or record it needs; the answer is the API's checks in order, each a sentence with its mark and a word,
 * never colour alone. The user record's Access tab opens it with the person chosen.
 */
export function ExplainPage() {
  const { t } = useTranslation();
  const session = useSession();
  const [params] = useSearchParams();
  const presetId = params.get("principal") ?? "";
  const presetName = params.get("name") ?? "";
  const [person, setPerson] = useState(presetId);
  const [actingFor, setActingFor] = useState<string | null>(null);
  const [actionName, setActionName] = useState<string | null>(null);
  const [values, setValues] = useState<Record<string, string>>({});
  const [question, setQuestion] = useState<ExplainQuestion | undefined>();
  const canSeeUnits = session.permissions.includes("authz.units.read");
  const actions = useQuery({
    queryKey: ["authz", "access", "actions"],
    queryFn: ({ signal }) => fetchExplainableActions(signal),
  });
  const units = useQuery({
    queryKey: ["authz", "organisation-units"],
    queryFn: ({ signal }) => fetchUnits(signal),
    enabled: canSeeUnits,
  });
  const answer = useQuery({
    queryKey: ["authz", "access", "explain", question],
    queryFn: ({ signal }) =>
      question ? explainAccess(question, signal) : Promise.reject(new Error("No question yet.")),
    enabled: question !== undefined,
    retry: false,
  });
  const action = actions.data?.data.find((candidate) => candidate.name === actionName);
  const missing = action?.route_values.some((value) => (values[value.name] ?? "") === "") ?? true;
  const grouped = useMemo(() => {
    const byModule = new Map<string, { value: string; label: string }[]>();
    for (const candidate of actions.data?.data ?? []) {
      const group = t(`explain.modules.${candidate.module}`, { defaultValue: candidate.module });
      byModule.set(group, [...(byModule.get(group) ?? []), { value: candidate.name, label: candidate.summary }]);
    }
    return [...byModule.entries()].map(([group, items]) => ({ group, items }));
  }, [actions.data, t]);

  const ask = (event: FormEvent) => {
    event.preventDefault();
    if (person !== "" && action && !missing) {
      setQuestion({ principal: person, action: action.name, values, actingFor: actingFor ?? undefined });
    }
  };

  return (
    <ListPage
      title={t("explain.title")}
      description={t("explain.description")}
      drawing={<ModuleMark name="console" accent="console" size={56} />}
      density={false}
    >
      <Flex direction={{ base: "column", md: "row" }} gap="md" align="flex-start">
        <Paper withBorder p="md" w={{ base: "100%", md: 400 }} style={{ flexShrink: 0 }}>
          <form onSubmit={ask} noValidate>
            <Stack>
              <PersonPicker
                label={t("explain.person")}
                value={person === "" ? null : person}
                onChange={(value) => setPerson(value ?? "")}
                initial={
                  presetId !== "" ? { id: presetId, display_name: presetName || presetId, home_unit: null } : undefined
                }
                data-autofocus
              />
              <Select
                label={t("explain.action")}
                description={t("explain.actionHint")}
                data={grouped}
                searchable
                value={actionName}
                onChange={(value) => {
                  setActionName(value);
                  setValues({});
                }}
                nothingFoundMessage={actions.isPending ? t("explain.loadingActions") : t("explain.noActions")}
              />
              {action?.route_values.map((value) =>
                value.kind === "unit" && canSeeUnits && units.data ? (
                  <UnitPicker
                    key={value.name}
                    label={t("explain.unit")}
                    units={units.data.data}
                    value={values[value.name] ?? ""}
                    onChange={(id) => setValues((current) => ({ ...current, [value.name]: id }))}
                  />
                ) : (
                  <TextInput
                    key={value.name}
                    label={
                      value.kind === "record"
                        ? t("explain.record", { type: value.record_type ?? "" })
                        : value.kind === "unit"
                          ? t("explain.unitId")
                          : t("explain.value", { name: value.name })
                    }
                    value={values[value.name] ?? ""}
                    onChange={(event) => {
                      const typed = event.currentTarget.value;
                      setValues((current) => ({ ...current, [value.name]: typed.trim() }));
                    }}
                  />
                ),
              )}
              <PersonPicker
                label={t("explain.actingFor")}
                description={t("explain.actingForHint")}
                value={actingFor}
                onChange={setActingFor}
                exclude={person === "" ? [] : [person]}
              />
              <Group justify="flex-end">
                <Button type="submit" disabled={person === "" || !action || missing}>
                  {t("explain.ask")}
                </Button>
              </Group>
            </Stack>
          </form>
        </Paper>
        <Paper withBorder p="md" style={{ flex: 1, minWidth: 0 }} aria-live="polite">
          {question === undefined ? (
            <EmptyState drawing="nothingYet" size="sm" title={t("explain.emptyTitle")} text={t("explain.emptyText")} />
          ) : answer.isPending ? (
            <LoadingState height={240} />
          ) : answer.isError ? (
            <Alert color="danger" role="alert" title={t("explain.failed")}>
              {answer.error instanceof ApiError ? answer.error.message : t("errors.unexpected")}
              {answer.error instanceof ApiError && answer.error.correlationId !== undefined && (
                <Text size="xs" c="dimmed" mt={4}>
                  {t("errors.correlation", { id: answer.error.correlationId })}
                </Text>
              )}
            </Alert>
          ) : (
            <Explanation explanation={answer.data} />
          )}
        </Paper>
      </Flex>
    </ListPage>
  );
}

/** The answer: the outcome in one sentence, then each check in the order a request meets them. */
export function Explanation({ explanation }: { explanation: AccessExplanation }) {
  const { t } = useTranslation();
  const name = explanation.principal.name;
  const failed = explanation.checks.find((check) => check.result === "failed");
  return (
    <Stack gap="md" data-explanation={explanation.allowed ? "allowed" : "denied"}>
      <Stack gap={4}>
        <Title order={3} size="h4">
          {explanation.allowed
            ? t("explain.outcome.allowed", { name, action: explanation.action.summary })
            : t("explain.outcome.denied", { name, action: explanation.action.summary, status: explanation.status })}
        </Title>
        {failed && (
          <Text size="sm">{t("explain.outcome.firstFailed", { check: t(`explain.checks.${failed.check}`) })}</Text>
        )}
        {/* The checks are the endpoint's; a handler may still refuse for its own reasons (ADR-0039), so an allowance says so. */}
        {explanation.allowed && <Text size="sm">{t("explain.outcome.covers")}</Text>}
        {explanation.acting_for && (
          <Text size="sm">{t("explain.outcome.acting", { name, delegator: explanation.acting_for.name })}</Text>
        )}
      </Stack>
      <Box component="ol" m={0} p={0} style={{ listStyle: "none" }} aria-label={t("explain.checksLabel")}>
        {explanation.checks.map((check) => {
          const Icon = resultIcons[check.result];
          return (
            <Box
              component="li"
              key={check.check}
              py="xs"
              style={{ borderTop: "1px solid var(--mantine-color-default-border)" }}
              data-check={check.check}
              data-result={check.result}
            >
              <Group gap="sm" wrap="nowrap" align="flex-start">
                <Icon size={20} aria-hidden="true" style={{ flexShrink: 0, marginTop: 2 }} />
                <Stack gap={2} style={{ minWidth: 0 }}>
                  <Text size="sm" fw={600}>
                    {t(`explain.checks.${check.check}`)}: {t(`explain.results.${check.result}`)}
                  </Text>
                  <Text
                    size="sm"
                    {...(check.result === "not_reached" || check.result === "not_applicable" ? { c: "dimmed" } : {})}
                  >
                    {checkWords(t, check, explanation)}
                  </Text>
                  {check.granted_by.length > 0 && (
                    <List size="sm" spacing={2} aria-label={t("explain.grantedBy")}>
                      {check.granted_by.map((grant) => (
                        <List.Item key={grant.assignment_id}>{grantWords(t, grant)}</List.Item>
                      ))}
                    </List>
                  )}
                </Stack>
              </Group>
            </Box>
          );
        })}
      </Box>
    </Stack>
  );
}
