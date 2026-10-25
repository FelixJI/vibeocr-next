import { expect, type Page } from "@playwright/test";
import type { HostCommand } from "../src/bridge/client";

/** 每次上传对应独立租约；断言必须通过实际 bridge 命令的 token 找到像素。 */
export async function recordAnnotationUploads(
  page: Page,
  scope = "recognition",
) {
  const uploads = new Map<
    string,
    { resourceUri: string; contentType: string; body: Buffer }
  >();
  const discarded = new Set<string>();
  await page.route("**/__annotation", async (route) => {
    expect(route.request().method()).toBe("POST");
    const resourceUri = `https://app.vibeocr/__annotation/${(uploads.size + 1).toString(16).padStart(32, "0")}`;
    uploads.set(resourceUri, {
      resourceUri,
      contentType: route.request().headers()["content-type"] ?? "",
      body: route.request().postDataBuffer() ?? Buffer.alloc(0),
    });
    await route.fulfill({
      status: 201,
      contentType: "application/json",
      body: JSON.stringify({ resourceUri }),
    });
  });
  await page.route("**/__annotation/*", async (route) => {
    expect(route.request().method()).toBe("DELETE");
    await route.fulfill({ status: 204 });
    discarded.add(new URL(route.request().url()).pathname);
  });
  const commands = (
    action: string,
    targetScope = scope,
  ): Promise<HostCommand[]> =>
    page.evaluate(
      ({ action, scope }) =>
        (
          window as Window & { __testHost: { commands: HostCommand[] } }
        ).__testHost.commands.filter(
          (command) => command.scope === scope && command.action === action,
        ),
      { action, scope: targetScope },
    );
  return {
    count: () => uploads.size,
    commands,
    async submitted(action: string, index = 0, targetScope = scope) {
      await expect
        .poll(async () => (await commands(action, targetScope)).length)
        .toBeGreaterThan(index);
      const command = (await commands(action, targetScope))[index]!;
      const upload = uploads.get(String(command.arguments.resourceUri));
      expect(upload, `${action} 必须消费自己的上传租约`).toBeDefined();
      return { ...upload!, command };
    },
    async forRevision(action: string, revision: number) {
      const matching = async () =>
        (await commands(action)).filter(
          (command) => command.arguments.revision === revision,
        );
      await expect
        .poll(async () => (await matching()).length)
        .toBeGreaterThan(0);
      const command = (await matching())[0]!;
      const upload = uploads.get(String(command.arguments.resourceUri));
      expect(upload, `${action} 必须消费当前修订的上传租约`).toBeDefined();
      return { ...upload!, command };
    },
    async currentText() {
      const revision =
        (await commands("notifyScreenshotRevision")).at(-1)?.arguments
          .revision ?? 0;
      const upload = await this.forRevision(
        "prepareScreenshotTextLayer",
        Number(revision),
      );
      await this.waitForDiscard(upload.resourceUri);
      return upload;
    },
    async waitForDiscard(resourceUri: string) {
      await expect
        .poll(() => discarded.has(new URL(resourceUri).pathname))
        .toBe(true);
    },
  };
}
