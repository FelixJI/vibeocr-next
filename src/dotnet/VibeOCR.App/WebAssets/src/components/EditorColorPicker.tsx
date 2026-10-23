import {
  Button,
  Popover,
  PopoverSurface,
  PopoverTrigger,
  Slider,
} from "@fluentui/react-components";
import { useState } from "react";

/** 页面内颜色面板由 Fluent 翻转定位，避免原生颜色窗口越出截图视口。 */
export function EditorColorPicker({
  label,
  value,
  onChange,
}: {
  readonly label: string;
  readonly value: string;
  readonly onChange: (value: string) => void;
}) {
  const [draft, setDraft] = useState<string>();
  const channels = [1, 3, 5].map((offset) =>
    parseInt(value.slice(offset, offset + 2), 16),
  );
  return (
    <Popover
      positioning={{ position: "below", align: "start", offset: 8 }}
      onOpenChange={() => setDraft(undefined)}
    >
      <PopoverTrigger disableButtonEnhancement>
        <Button
          aria-label={label}
          title={label}
          size="small"
          style={{
            background: value,
            minWidth: 24,
            width: 24,
            height: 24,
            padding: 0,
          }}
        />
      </PopoverTrigger>
      <PopoverSurface
        role="dialog"
        aria-label={label}
        className="editor-color-popup"
      >
        <label>
          十六进制颜色
          <input
            aria-label="十六进制颜色"
            value={draft ?? value}
            maxLength={7}
            onChange={(event) => {
              const next = event.target.value;
              setDraft(next);
              if (/^#[0-9a-f]{6}$/i.test(next)) onChange(next.toLowerCase());
            }}
            onBlur={() => setDraft(undefined)}
          />
        </label>
        {["红", "绿", "蓝"].map((name, index) => (
          <label key={name}>
            {name} {channels[index]}
            <Slider
              aria-label={name}
              min={0}
              max={255}
              value={channels[index]}
              onChange={(_, data) => {
                setDraft(undefined);
                onChange(
                  "#" +
                    channels
                      .map((channel, at) =>
                        (at === index ? data.value : channel)
                          .toString(16)
                          .padStart(2, "0"),
                      )
                      .join(""),
                );
              }}
            />
          </label>
        ))}
      </PopoverSurface>
    </Popover>
  );
}
