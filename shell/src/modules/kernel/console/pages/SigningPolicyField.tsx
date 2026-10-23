import { useTranslation } from "react-i18next";
import { useQuery } from "@tanstack/react-query";
import { Alert, Checkbox, Fieldset, Group, Stack, Switch, Text } from "@mantine/core";
import { complianceQueryKey, fetchSignableTypes, type SignableType, type SigningPolicy } from "@/api/compliance";
import { LoadingState } from "@/components/panels";

/** The setting's value: a policy per document type, as the API reads it (ADR-0051). */
export type PolicyMap = Record<string, Partial<SigningPolicy>>;

const effective = (type: SignableType, value: PolicyMap): SigningPolicy => ({
  required: value[type.record_type]?.required ?? type.default.required,
  meanings: value[type.record_type]?.meanings ?? type.default.meanings,
  reauthenticate: value[type.record_type]?.reauthenticate ?? type.default.reauthenticate,
});

/**
 * The signing policy in Settings (TF-063, ADR-0051): for each document type the modules let people sign, whether a
 * signature is required, the meanings it may have, and whether signing asks for the password again. An always-signed
 * type, the audit-trail review, is shown as it stays.
 */
export function SigningPolicyField({ value, onChange }: { value: PolicyMap; onChange: (value: PolicyMap) => void }) {
  const { t } = useTranslation();
  const types = useQuery({
    queryKey: [...complianceQueryKey, "types"],
    queryFn: ({ signal }) => fetchSignableTypes(signal),
  });
  if (types.isPending) {
    return <LoadingState height={120} />;
  }
  if (types.isError) {
    return (
      <Alert color="danger" role="alert">
        {t("signingPolicy.failed")}
      </Alert>
    );
  }

  const set = (type: SignableType, change: Partial<SigningPolicy>) =>
    onChange({ ...value, [type.record_type]: { ...effective(type, value), ...change } });

  return (
    <Stack gap="md" data-signing-policy>
      <Text size="sm">{t("signingPolicy.intro")}</Text>
      {types.data.data.map((type) => {
        const policy = effective(type, value);
        return (
          <Fieldset key={type.record_type} legend={type.name} data-signable={type.record_type}>
            <Stack gap="sm">
              {type.always_signed && (
                <Text size="sm" c="dimmed">
                  {t("signingPolicy.alwaysSigned")}
                </Text>
              )}
              <Switch
                label={t("signingPolicy.required")}
                checked={type.always_signed || policy.required}
                disabled={type.always_signed}
                onChange={(event) => set(type, { required: event.currentTarget.checked })}
              />
              <Checkbox.Group
                label={t("signingPolicy.meanings")}
                value={policy.meanings}
                onChange={(meanings) => meanings.length > 0 && set(type, { meanings })}
              >
                <Group gap="md" mt={4}>
                  {type.meanings.map((meaning) => (
                    <Checkbox
                      key={meaning}
                      value={meaning}
                      label={t(`signatures.meanings.${meaning}`, { defaultValue: meaning })}
                    />
                  ))}
                </Group>
              </Checkbox.Group>
              <Switch
                label={t("signingPolicy.reauthenticate")}
                description={t("signingPolicy.reauthenticateHint")}
                checked={type.always_signed || policy.reauthenticate}
                disabled={type.always_signed}
                onChange={(event) => set(type, { reauthenticate: event.currentTarget.checked })}
              />
            </Stack>
          </Fieldset>
        );
      })}
    </Stack>
  );
}
