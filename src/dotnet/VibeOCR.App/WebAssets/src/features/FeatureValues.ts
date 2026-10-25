import { useEffect, useState } from "react";

import type { AppActions, AppViewState } from "../app/types";

export interface FeatureProps {
  readonly viewState: AppViewState;
  readonly actions: AppActions;
}

export function feature(
  viewState: AppViewState,
  scope: string,
): Readonly<Record<string, unknown>> {
  const value = viewState.features[scope];
  return value !== null && typeof value === "object" && !Array.isArray(value)
    ? (value as Readonly<Record<string, unknown>>)
    : {};
}

export function booleanValue(value: unknown): boolean {
  return value === true;
}

export interface ResourceReference {
  readonly url: string;
  readonly mediaType: string;
  readonly byteLength: number;
}

export function resource(value: unknown): ResourceReference | undefined {
  if (value === null || typeof value !== "object" || Array.isArray(value))
    return undefined;
  const candidate = value as Partial<ResourceReference>;
  return typeof candidate.url === "string" &&
    typeof candidate.mediaType === "string" &&
    typeof candidate.byteLength === "number"
    ? (candidate as ResourceReference)
    : undefined;
}

export function numberValue(value: unknown): number {
  return typeof value === "number" && Number.isFinite(value) ? value : 0;
}

export function stringValue(value: unknown): string | undefined {
  return typeof value === "string" && value.length > 0 ? value : undefined;
}

export function availabilityLabel(availability: string): string {
  const labels: Readonly<Record<string, string>> = {
    ready: "可用",
    preparation_required: "需准备依赖",
    unavailable: "不可用",
  };
  return labels[availability] ?? availability;
}

export function statusLabel(value: unknown, fallback: string): string {
  const labels: Readonly<Record<string, string>> = {
    "recognition.running": "正在识别",
    "recognition.completed": "识别完成",
    "recognition.ready": "等待输入",
    "recognition.session": "已捕获截图；可编辑标注、复制保存或显式识别当前图",
    "recognition.expired": "内容已修改，旧识别结果已失效；请重新识别当前图",
    "recognition.failed": "识别失败，请检查运行时状态后重试",
    "recognition.cancelled": "识别已取消，可重新选择输入",
    "recognition.modeRetired":
      "所选识别模式已退役，请在设置或任务面板重新选择可用模式。",
    "recognition.modeUnavailable":
      "所选识别模式在当前环境不可用；请检查模型、设备与运行环境",
    "recognition.captureRefused.missingFixedRegion":
      "尚未设置固定截图区域；请打开截图选项设置后重试",
    "recognition.captureRefused.regionUnavailable":
      "固定/上次截图区域当前不可用（布局变化或越界）；请重新框选或在截图选项中调整",
    "recognition.captureRefused.windowUnavailable":
      "目标活动窗口已不可用（消失或最小化）；请在其置顶后重试",
    "recognition.captureRefused.monitorUnavailable":
      "所选显示器当前不可用或未选择；请从截图模式菜单重新选择",
    "recognition.captureRefused.layoutChanged":
      "截图期间显示器布局发生变化；请重新截图",
    "recognition.captureRefused.tooLarge":
      "截图区域超出 256 MiB 上限；请缩小目标后重试",
    "recognition.exported": "结果已导出",
    "recognition.exportCancelled": "已取消导出，识别结果仍保留",
    "recognition.exportedIncomplete":
      "文件已保存，但部分图片缺失或该格式无法容纳图片；请检查原始结果",
    "pdf.open": "PDF 会话已建立",
    "pdf.page.none": "未处理",
    "pdf.page.processing": "处理中",
    "pdf.page.done": "已完成",
    "pdf.page.failed": "失败",
    "pdf.empty": "尚未建立 PDF 会话",
    "pdf.failed": "PDF 操作失败，请检查文件或重试",
    "pdf.backendUnavailable": "识别服务暂不可用，请检查运行时状态后重试",
    "pdf.outOfMemory": "内存或显存不足，请减少页数或关闭其他任务后重试",
    "pdf.cancelled": "PDF 操作已取消",
    "qrcode.decoded": "识别完成",
    "qrcode.ready": "等待输入",
    "qrcode.running": "正在处理二维码…",
    "qrcode.failed": "图片识别失败，请检查输入图片后重试",
    "qrcode.generateFailed": "二维码生成失败，请重试",
    "qrcode.invalidInput": "内容不符合所选编码格式，请检查下方提示。",
    "qrcode.copied": "已复制当前预览图片",
    "qrcode.noCodes": "当前预览中未识别到支持的二维码或条码",
    "qrcode.decodeUnavailable": "图片识别暂不可用，请稍后重试",
    "qrcode.cancelled": "二维码处理已取消",
    "settings.ready": "运行环境设置已同步",
    "settings.restartRequired": "更改将在重启后生效",
    "update.available": "发现可用更新",
    "update.current": "当前已是最新版本",
  };
  return typeof value === "string" ? (labels[value] ?? fallback) : fallback;
}

export function stringValues(value: unknown): readonly string[] {
  return Array.isArray(value) &&
    value.every((entry) => typeof entry === "string")
    ? value
    : [];
}

export function useResourceText(
  reference: ResourceReference | undefined,
): string {
  // 文本与来源 URL 绑定：A→B/undefined 替换后的首帧即返回空串，
  // 迟到/逆序响应只写入匹配自身 URL 的槽位，不覆写新资源。
  const [entry, setEntry] = useState<{ url: string; text: string }>();
  useEffect(() => {
    if (!reference) return undefined;
    const cancellation = new AbortController();
    void fetch(reference.url, {
      cache: "no-store",
      credentials: "omit",
      signal: cancellation.signal,
    })
      .then((response) => {
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        return response.text();
      })
      .then((text) => {
        if (!cancellation.signal.aborted)
          setEntry({ url: reference.url, text });
      })
      .catch((error: unknown) => {
        if (error instanceof DOMException && error.name === "AbortError")
          return;
        if (!cancellation.signal.aborted)
          setEntry({
            url: reference.url,
            text: "结果资源已失效，请重新识别。",
          });
      });
    return () => cancellation.abort();
  }, [reference]);
  return entry && reference && entry.url === reference.url ? entry.text : "";
}
