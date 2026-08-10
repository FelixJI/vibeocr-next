# Classic → VibeOCR 行为对齐矩阵

本矩阵把 Classic 当作用户可观察行为参考，而不是 Qt 布局模板。`已对齐` 表示当前
VibeOCR 仓库内已有可执行实现与自动化契约；`局部对齐` 表示主流程可用，但仍缺少
Classic 已公开的行为；`上游阻断` 表示当前发布的 Backend/Protocol 没有对应写操作，
本仓不能用占位按钮或纯前端假状态伪装完成。

## 结论

| 工作流 | 状态 | 本次落地 | 尚缺行为 / 阻断原因 |
|---|---|---|---|
| 单图输入与识别 | 局部对齐 | 文件、拖放、剪贴板、截图、取消、结果展示；任务状态跨路由保留 | 当前输入即启动识别，尚无“先检查/编辑/配置，再开始”提交边界；编辑后的画布未重新送入 OCR；缺复制原图和完整预处理参数 |
| 截图与编辑 | 局部对齐 | 原生多屏选区、全局快捷键、托盘入口；Canvas 有选择、形状、文字、马赛克、模糊、裁剪、旋转、撤销/重做 | 缺 Classic 的截图后确认/复制/另存完整闭环；当前编辑结果不参与随后的识别输入 |
| 结果复制与导出 | 已对齐 | 复制纯文本；导出 Markdown、Word (`docx`) 与 Excel (`xlsx`)；宿主文件选择和覆盖确认 | 富文本表格剪贴板仍只保留为结果 HTML/导出路径，没有 Classic 的 HTML Clipboard MIME |
| 批量识别 | 局部对齐 | 多文件、拖放、去重、并发、排序、移除、真实取消、部分失败继续、分页窗口、全部 Markdown/Word/Excel 导出 | 缺选中项预览、完整结果检查和“当前项导出”；页面目前只投影 120 字摘要 |
| PDF | 上游阻断 | 打开单个 PDF、多选、缩略图分页、旋转、删除、页面 OCR、取消、保存 | OCR 文本仅保存在前端 ViewModel，`SavePdfAsync` 不写文字层；当前接口没有插页、重排、多个 PDF 合并、自动/横放/纵放摆正、文字层读写操作 |
| 二维码 | 上游阻断 | 生成、文件/剪贴板/拖放识别、取消、清空、保存、严格限定的 URL 打开 | `IQrCodeClient.GenerateAsync` 只接受 `data/format`；没有尺寸、纠错、前景/背景色、反色、Logo、标签位置；缺结果复制命令 |
| 设置与 Runtime | 上游阻断 | 主题、开机启动、全局快捷键、Runtime/驻留状态刷新、更新检查/下载/取消、诊断导出 | `IInferenceClient` 只有驻留状态读取；没有预热、TTL 写入、驻留释放、缓存清理、Backend 切换、依赖树重装或 Runtime profile 重建接口 |
| 应用壳与恢复 | 已对齐 | 单实例、托盘、全局截图快捷键、`--goto`、跨路由任务状态、WebView2 单次恢复、原生诊断恢复页 | 按产品决策不增加永久识别历史，也不在重启后恢复未完成任务 |

## 本 PR 的可验证契约

- `WorkbenchBridgeCodecTests` 覆盖封闭命令、导出格式、状态 envelope 与尺寸边界。
- `DesktopWorkbenchCommandHandlerTests` 和各 ViewModel 测试覆盖任务取消、陈旧完成抑制、
  批量部分失败继续、PDF/二维码状态和平台动作。
- Web 测试覆盖七路由、能力门控、分页窗口、PDF 多选、二维码 URL、安全资源 broker
  与编辑器撤销/重做。
- Playwright 固定覆盖 1280×800 浅色单图、1024×720 深色批量运行、1280×800
  浅色 PDF 检查三张视觉基线。

## 完成严格等价所需的外部 seam

严格完成上述三个“上游阻断”工作流，必须先发布兼容的 Backend/Protocol capability，
再由本仓消费；不能在 VibeOCR 的 React 页面中直接访问 Backend 或文件系统，也不能
扩张现有 bridge JSON 去传输图片/PDF 二进制。最小外部接口至少包括：

1. PDF session 的 insert/reorder/orient/text-layer read/write，并明确保存后的持久化语义。
2. 二维码生成 options（尺寸、纠错、颜色、反色、Logo、标签）及生成资产的保存契约。
3. Runtime residency/config mutation（preload、TTL、evict、cache clear、backend/profile、
   dependency reinstall）及可取消进度事件。

在这些接口真实存在并进入发布绑定前，相关页面应显示准确的只读能力边界，不得显示
无实现的操作或用“即将支持”替代验收。
