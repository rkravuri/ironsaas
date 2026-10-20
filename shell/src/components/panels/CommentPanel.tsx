import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Button, Group, Paper, Stack, Text, Textarea, Title } from "@mantine/core";

export interface Comment {
  id: string;
  author: string;
  at: string;
  text: string;
}

export interface CommentPanelProps {
  comments: Comment[];
  /** Absent until the comments slice: the composer then explains itself. */
  onAdd?: (text: string) => Promise<void>;
}

/** The conversation on a record (TF-171): the comments, and a composer where the page allows it. */
export function CommentPanel({ comments, onAdd }: CommentPanelProps) {
  const { t } = useTranslation();
  const [text, setText] = useState("");
  const [busy, setBusy] = useState(false);
  const add = async () => {
    if (!onAdd || text.trim() === "") {
      return;
    }
    setBusy(true);
    try {
      await onAdd(text.trim());
      setText("");
    } finally {
      setBusy(false);
    }
  };
  return (
    <Stack gap="sm" data-panel="comments">
      <Title order={4}>{t("comments.title")}</Title>
      {comments.length === 0 ? (
        <Text size="sm" c="dimmed">
          {t("comments.empty")}
        </Text>
      ) : (
        comments.map((comment) => (
          <Paper key={comment.id} withBorder p="sm">
            <Text size="xs" c="dimmed">
              {comment.author}, {new Date(comment.at).toLocaleString()}
            </Text>
            <Text size="sm">{comment.text}</Text>
          </Paper>
        ))
      )}
      <Textarea
        label={t("comments.label")}
        value={text}
        onChange={(event) => setText(event.currentTarget.value)}
        disabled={onAdd === undefined}
        rows={3}
      />
      <Group justify="space-between">
        <Text size="xs" c="dimmed">
          {onAdd === undefined ? t("comments.later") : ""}
        </Text>
        <Button
          size="xs"
          onClick={() => void add()}
          disabled={onAdd === undefined || text.trim() === ""}
          loading={busy}
        >
          {t("comments.add")}
        </Button>
      </Group>
    </Stack>
  );
}
