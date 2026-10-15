import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { AppActions, AppViewState } from "../app/types";
import { ImageEditPage } from "./ImageEditPage";
import { RecognitionPage } from "./RecognitionPage";

afterEach(cleanup);

const actions: AppActions = {
  run: vi.fn().mockResolvedValue(true),
  navigate: vi.fn(),
  setTheme: vi.fn(),
};
for (const scope of ["imageEdit", "recognition"] as const) {
  describe(`${scope} native import failures`, () => {
    it.each([
      [
        "workbench.error.imageImportOpenFirst",
        "请先打开图片，再导入文字对象。",
      ],
      [
        "oversize",
        "导入内容超出大小或尺寸上限；本批未导入，已有画布保持不变。",
      ],
      [
        "invalid",
        "无法导入内容；请检查格式、尺寸和访问权限，已有画布保持不变。",
      ],
    ])(
      "renders %s from host state in the live status region",
      (importError, message) => {
        const viewState: AppViewState = {
          connected: true,
          revision: 1,
          route: scope,
          theme: "light",
          capabilities: [],
          runtimeLabel: "test",
          features: {
            [scope]: {
              isBusy: false,
              statusCode: "recognition.ready",
              importError,
            },
          },
        };
        const Page = scope === "imageEdit" ? ImageEditPage : RecognitionPage;
        const { unmount } = render(
          <Page actions={actions} viewState={viewState} />,
        );
        expect(screen.getByText(message)).toHaveAttribute(
          "aria-live",
          "polite",
        );
        unmount();
      },
    );
  });
}
