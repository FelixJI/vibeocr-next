# Classic → VibeOCR Next 行为对照

Classic 的对照事实来自 [GitHub 当前源码](https://github.com/FelixJI/vibeocr-classic/tree/b816274809276699aa20b1b79193e0672710198c)，不使用旧迁移文档替代实现。2026-10-14 核对默认分支 `main` 为 `b816274809276699aa20b1b79193e0672710198c`，仓库当前未归档。Next 的 Runtime、内部契约和桌面源码已在本仓；缺少功能应在本仓按 capability 与语言边界实现，不再等待旧 Backend/Protocol 发版。

## 本次核对范围

| 工作流 | 对照要求 | Next 的实现边界与验证入口 |
|---|---|---|
| 布局 | 标题下无随窗口高度扩大的空白 | Web 工作区顶部对齐。`workspace-density.spec.ts` 验证不同高度的标题布局。 |
| 截图工具栏 | 工具含义清楚；样式设置不把操作条撑出窗口 | 原生选区工具栏与 Web 编辑器分别承担选区动作和局部编辑；编辑工具、颜色/线宽等设置分排。`ScreenshotSessionWorkbenchTests` 与截图场景 Playwright 测试覆盖交接及布局。 |
| 文字结果 | 低置信度提示、行框编辑、原始/每行/一段/智能分段 | 识别结果由 C# 宿主管理；展示和行编辑共用 Classic 阅读顺序到源块的索引映射，原始格式保留上游原文。Web 通过 bridge 提交编辑，复制和导出消费同一权威文本。`StructuredResultWorkbenchTests` 与 `StructuredResult.test.tsx` 覆盖结果交互。 |
| 公式与表格 | 公式显示与源文本选择；表格整表/局部选择与复制 | 复用 KaTeX、结构化表格和原生 HTML/TSV 剪贴板路径；Web 不直接访问系统剪贴板或 Runtime。结构化结果测试覆盖展示、范围与 bridge 命令。 |
| Word / Excel 导出 | 识别内容可导出为真实文档；失败、取消与过期结果有明确行为 | 宿主提供文件选择与授权，Runtime documents 负责格式转换；沿用已有导出能力，避免新增第二套文档生成链路。 |

标题栏快速拖动已由用户从当前目标移除，仍未视为已修复；标题下留白已独立修复。

表中描述的是本次实现与验收范围，不表示所有 Classic 细节严格等价。自动化结果以本次实际执行记录为准；Office 剪贴板格式契约通过也不能替代真实 Office 应用的粘贴验收。

## 源码对照依据

以下链接均固定到本次核对的 Classic GitHub 提交，行为结论来自源码：

- [文本排版](https://github.com/FelixJI/vibeocr-classic/blob/b816274809276699aa20b1b79193e0672710198c/apps/vibeocr-pyside/src/vibeocr/classic/text_layout.py)：阅读顺序、合并与智能分段。
- [结果窗口](https://github.com/FelixJI/vibeocr-classic/blob/b816274809276699aa20b1b79193e0672710198c/apps/vibeocr-pyside/src/vibeocr/classic/widgets/result_view_widget.py)：公式渲染、文字选择、局部表格复制与 HTML 剪贴板。
- [图片校对](https://github.com/FelixJI/vibeocr-classic/blob/b816274809276699aa20b1b79193e0672710198c/apps/vibeocr-pyside/src/vibeocr/classic/widgets/preview_widget.py)：低置信度框与双击编辑。
- [编辑工具栏](https://github.com/FelixJI/vibeocr-classic/blob/b816274809276699aa20b1b79193e0672710198c/apps/vibeocr-pyside/src/vibeocr/classic/widgets/editor/edit_toolbar.py)与[图标](https://github.com/FelixJI/vibeocr-classic/blob/b816274809276699aa20b1b79193e0672710198c/apps/vibeocr-pyside/src/vibeocr/classic/ui/toolbar_icons.py)：动作与属性控制。

2026-10-14 使用真实 Word/Excel 补验生产剪贴板：所选 2×2 区域粘贴后保持单元格位置，多行文字留在同一单元格，识别出的公式样文本不执行；真实 Runtime 导出的 DOCX/XLSX 可打开，空行和字面量尖括号保留。HTML 换行使用 `mso-data-placement:same-cell`，防止 Excel 把单元格内换行拆成新表格行。该实测不代表所有 Office 版本和复杂表格组合均已验证。

## 已有能力，避免重复实施

- Classic 迁入的 Python `TextBlockProcessor` 与 `TextBlockOptions` 没有生产调用方，已随宿主排版接线移除；排版规则仅由 C# 生成，Web 展示，Runtime 导出消费所选文本。
- 单图/截图使用会话与修订绑定，编辑图像经过导出后进入 OCR；旋转、裁剪或会话替换后旧文字层失效。相关入口为 `WorkbenchImageOwner`，契约见 `ScreenshotSessionWorkbenchTests` 与 `ImageEditIsolationWorkbenchTests`。
- PDF 已有插页、重排、横纵摆正、文本层读写及保存路径，并非旧矩阵所述的外部接口阻断。对应 `PdfViewModel`、`PdfPageOperationsTests`、`PdfTextLayerTests`。
- 公开文本二维码已由 C# 本地生成；解码仍由 Runtime 承担。对应 `LocalQrCodeClientTests` 与 `LocalQrCodeGeneratorTests`。
- 环境安装、切换、清理和修复已有宿主命令与 Runtime 事务边界。`WorkbenchSettingsOwner` 负责装配，不在 React 重复推导依赖或设备就绪状态。
- 单实例、托盘、跨产品互斥、任务取消、更新及恢复沿用现有实现。保留已有数据迁移与跨产品互斥，仓库状态变化不改变这些兼容契约。

## 持续保留的边界

低置信度仅在上游提供有效分数时判断，缺失分数不能伪装成 0% 或 100%。编辑、复制和导出必须拒绝已替换的结果；同文本出现多次时不能靠全文首次匹配定位。结构化表格复制保留 HTML 与 TSV 两种表达，公式同时保留可选择的源文本。没有 capability 的操作继续隐藏、禁用或明确回退，不显示假成功。
