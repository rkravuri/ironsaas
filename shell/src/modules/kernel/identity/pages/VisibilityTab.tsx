import { useTranslation } from "react-i18next";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Alert, Text } from "@mantine/core";
import { ApiError } from "@/api/client";
import type { UserRecord } from "@/api/identity";
import { fetchHolderTags, setHolderTags } from "@/api/visibility";
import { LoadingState } from "@/components/panels";
import { TagsPanel } from "@/components/visibility";

/** Reading the visibility tags, which the Visibility tab of a user needs (TF-241, ADR-0057). */
export const readVisibilityTags = "visibility.tags.read";

/**
 * A person's or a service account's visibility tags (TF-241 rule 3, ADR-0057): holding none, they see every record their
 * roles and units allow; holding tags, a tagged record only when they share one of its tags. An administrator with
 * visibility.assignments.manage at the person's unit changes them, giving and taking away only tags they hold
 * themselves, unless they hold none. Each change is in the person's History and security activity.
 */
export function VisibilityTab({ user }: { user: UserRecord }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const key = ["visibility", "holder-tags", user.id] as const;
  const tags = useQuery({ queryKey: key, queryFn: ({ signal }) => fetchHolderTags(user.id, signal) });
  if (tags.isPending) {
    return <LoadingState height={96} />;
  }
  if (tags.isError) {
    return (
      <Alert color="danger" role="alert">
        {tags.error instanceof ApiError ? tags.error.message : t("errors.unexpected")}
        {tags.error instanceof ApiError && tags.error.correlationId !== undefined && (
          <Text size="xs" c="dimmed" mt={4}>
            {t("errors.correlation", { id: tags.error.correlationId })}
          </Text>
        )}
      </Alert>
    );
  }
  return (
    <TagsPanel
      value={tags.data}
      label={t("visibility.person.label")}
      noneWords={t("visibility.person.none", { name: user.display_name })}
      hint={t("visibility.person.hint", { name: user.display_name })}
      dialogTitle={t("visibility.person.title", { name: user.display_name })}
      onChange={async (codes) => {
        queryClient.setQueryData(key, await setHolderTags(user.id, codes));
        await queryClient.invalidateQueries({ queryKey: ["audit"] });
      }}
    />
  );
}
