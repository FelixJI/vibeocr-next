import { Button } from "@fluentui/react-components";
import { useEffect, useRef, useState } from "react";
export function LineEditor({
  label,
  initialText,
  submitButtonText = "保存",
  onSubmit,
  onCancel,
}: {
  readonly label: string;
  readonly initialText: string;
  readonly submitButtonText?: string;
  readonly onSubmit: (newText: string) => Promise<boolean>;
  readonly onCancel: () => void;
}) {
  const [draft, setDraft] = useState(initialText);
  const [pending, setPending] = useState(false);
  const [failed, setFailed] = useState(false);
  const textareaRef = useRef<HTMLTextAreaElement>(null);
  useEffect(() => {
    textareaRef.current?.focus();
    textareaRef.current?.select();
  }, []);
  const save = async () => {
    if (pending) return;
    if (draft === initialText) {
      onCancel();
      return;
    }
    setPending(true);
    setFailed(false);
    try {
      // 提交期间不可取消/重复提交；拒绝或 false 都保留草稿可重试。
      const applied = await onSubmit(draft);
      if (!applied) setFailed(true);
    } catch {
      setFailed(true);
    } finally {
      setPending(false);
    }
  };
  return (
    <span
      className="line-editor"
      onKeyDown={(event) => {
        if (event.key === "Escape" && !pending) {
          event.stopPropagation();
          onCancel();
        } else if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) {
          event.preventDefault();
          void save();
        }
      }}
    >
      <textarea
        ref={textareaRef}
        aria-label={label}
        value={draft}
        disabled={pending}
        onChange={(event) => setDraft(event.target.value)}
        rows={Math.min(6, Math.max(2, draft.split("\n").length))}
      />
      <span className="result-line-actions">
        <Button size="small" disabled={pending} onClick={() => void save()}>
          {submitButtonText}
        </Button>
        <Button
          size="small"
          appearance="secondary"
          disabled={pending}
          onClick={onCancel}
        >
          取消
        </Button>
      </span>
      {failed && (
        <span role="alert" className="form-note">
          保存失败：结果已更新或该行内容已变化，请重试。
        </span>
      )}
    </span>
  );
}
