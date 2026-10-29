import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Badge, Button, Checkbox, Group, Paper, Stack, Text } from "@mantine/core";
import { IconTags } from "@tabler/icons-react";
import { z } from "zod";
import { fetchRecordTags, setRecordTags, type HeldTags, type TagSummary } from "@/api/visibility";
import { Form, useZodForm } from "@/components/form";
import { FormDialog, LoadingState } from "@/components/panels";

/**
 * A business record's visibility tags (TF-241, ADR-0057), for any record page that carries them: give it the record type
 * and id. A person who holds tags sees the record when they share one of its tags; one who holds none sees it whatever
 * its tags. The record's own policy decides who may change them, and rule 5 which: the reader puts on, or takes off,
 * only tags they hold, unless they hold none.
 */
export function RecordTagsControl({ recordType, recordId }: { recordType: string; recordId: string }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const key = useMemo(() => ["visibility", "record-tags", recordType, recordId] as const, [recordType, recordId]);
  const tags = useQuery({ queryKey: key, queryFn: ({ signal }) => fetchRecordTags(recordType, recordId, signal) });
  if (tags.isPending) {
    return <LoadingState height={64} />;
  }
  if (tags.isError) {
    return (
      <Text size="sm" c="dimmed">
        {t("visibility.control.failed")}
      </Text>
    );
  }
  return (
    <TagsPanel
      value={tags.data}
      label={t("visibility.control.label")}
      noneWords={t("visibility.control.none")}
      hint={t("visibility.control.hint")}
      dialogTitle={t("visibility.control.title")}
      onChange={async (codes) => {
        queryClient.setQueryData(key, await setRecordTags(recordType, recordId, codes));
      }}
    />
  );
}

/** A tag in words: its name and code, and "retired" when it is, never a colour alone. */
export function TagBadge({ tag }: { tag: TagSummary }) {
  const { t } = useTranslation();
  return (
    <Badge
      variant={tag.active ? "light" : "outline"}
      color="console"
      tt="none"
      data-tag={tag.code}
      data-retired={tag.active ? undefined : true}
    >
      {tag.active
        ? t("visibility.tag.words", { name: tag.name, code: tag.code })
        : t("visibility.tag.retiredWords", { name: tag.name, code: tag.code })}
    </Badge>
  );
}

/**
 * Tags held or carried, in words, with a button to change them for a reader who may. The person's tab and the record
 * control both use it; the gallery shows it with sample data.
 */
export function TagsPanel({
  value,
  label,
  noneWords,
  hint,
  dialogTitle,
  onChange,
}: {
  value: HeldTags;
  label: string;
  noneWords: string;
  hint: string;
  dialogTitle: string;
  onChange: (codes: string[]) => Promise<void>;
}) {
  const { t } = useTranslation();
  const [changing, setChanging] = useState(false);
  return (
    <Stack gap={6} data-visibility-tags>
      <Group gap="xs" wrap="nowrap">
        <IconTags size={18} aria-hidden="true" />
        <Text size="sm" fw={500}>
          {label}
        </Text>
      </Group>
      {value.tags.length === 0 ? (
        <Text size="sm">{noneWords}</Text>
      ) : (
        <Group gap="xs" role="list" aria-label={label}>
          {value.tags.map((tag) => (
            <span role="listitem" key={tag.code}>
              <TagBadge tag={tag} />
            </span>
          ))}
        </Group>
      )}
      {value.may_change && (
        <Group>
          <Button variant="default" size="xs" onClick={() => setChanging(true)}>
            {t("visibility.control.change")}
          </Button>
        </Group>
      )}
      {changing && (
        <TagsDialog
          value={value}
          title={dialogTitle}
          hint={hint}
          onClose={() => setChanging(false)}
          onSave={async (codes) => {
            await onChange(codes);
            setChanging(false);
          }}
        />
      )}
    </Stack>
  );
}

interface TagValues extends Record<string, unknown> {
  codes: string[];
}

/**
 * The tags to keep and to add, as checkboxes: the tags carried now, and the tags the reader may put. Taking off a tag
 * the reader does not hold is refused by the API, which names the tag; adding is offered only for the tags they may put.
 */
function TagsDialog({
  value,
  title,
  hint,
  onClose,
  onSave,
}: {
  value: HeldTags;
  title: string;
  hint: string;
  onClose: () => void;
  onSave: (codes: string[]) => Promise<void>;
}) {
  const { t } = useTranslation();
  const options = useMemo(() => {
    const carried = new Set(value.tags.map((tag) => tag.code));
    return [...value.tags, ...value.choices.filter((tag) => !carried.has(tag.code))].sort((a, b) =>
      a.code.localeCompare(b.code),
    );
  }, [value]);
  const form = useZodForm<TagValues>({
    schema: z.object({ codes: z.array(z.string()) }),
    initialValues: { codes: value.tags.map((tag) => tag.code) },
  });

  return (
    <FormDialog opened title={title} onClose={onClose}>
      <Form
        form={form}
        onSubmit={(values) => onSave(values.codes)}
        submitLabel={t("visibility.control.save")}
        secondary={{ label: t("dialog.cancel"), onClick: onClose }}
      >
        <Text size="sm">{hint}</Text>
        {options.length === 0 ? (
          <Text size="sm" c="dimmed">
            {t("visibility.control.noChoices")}
          </Text>
        ) : (
          <Checkbox.Group label={t("visibility.control.tags")} {...form.getInputProps("codes")}>
            <Paper withBorder p="xs" mt={6} mah={300} style={{ overflow: "auto" }}>
              <Stack gap={8}>
                {options.map((tag, index) => (
                  <Checkbox
                    key={tag.code}
                    value={tag.code}
                    label={
                      tag.active
                        ? t("visibility.tag.words", { name: tag.name, code: tag.code })
                        : t("visibility.tag.retiredWords", { name: tag.name, code: tag.code })
                    }
                    data-autofocus={index === 0 ? true : undefined}
                  />
                ))}
              </Stack>
            </Paper>
          </Checkbox.Group>
        )}
      </Form>
    </FormDialog>
  );
}
