import { describe, expect, it } from "vitest";
import {
  DEFAULT_EDITOR_PREFERENCES,
  shortcutConflict,
  shortcutTool,
} from "./editorPreferences";

describe("editor local tool shortcuts", () => {
  const settings = {
    ...DEFAULT_EDITOR_PREFERENCES,
    hidden: ["pen" as const],
    shortcuts: { pen: "p" },
  };
  const key = {
    key: "p",
    ctrlKey: false,
    metaKey: false,
    altKey: false,
    shiftKey: false,
  };
  it("keeps hidden tools discoverable by local shortcut while preserving system chords", () => {
    expect(shortcutTool(key, settings)).toBe("pen");
    expect(shortcutTool({ ...key, ctrlKey: true }, settings)).toBeUndefined();
    expect(shortcutTool({ ...key, metaKey: true }, settings)).toBeUndefined();
    expect(shortcutTool({ ...key, altKey: true }, settings)).toBeUndefined();
  });
  it("reports duplicate and reserved chord conflicts before activation", () => {
    expect(
      shortcutConflict({ ...settings, shortcuts: { pen: "p", text: "p" } }),
    ).toMatch(/冲突/);
    expect(
      shortcutConflict({ ...settings, shortcuts: { pen: "Ctrl+s" } }),
    ).toMatch(/系统编辑键/);
    expect(shortcutConflict(DEFAULT_EDITOR_PREFERENCES)).toBeUndefined();
  });
});
