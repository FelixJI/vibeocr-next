# Flameshot Material 图标适配评估

核对日期：2026-10-15。结论：常规操作适配较好，整套替换适配一般；保持已确认的 Tabler 主体、IconPark 重新框选/平移及专用马赛克/模糊图形。本轮只评估 Material，没有替换已定稿资产。

## 依据与版本边界

- [Flameshot 官方说明](https://flameshot.org/docs/overview/overview/)将按钮图标归于 Google Material Design Icons，许可为 Apache 2.0；应用代码的 GPLv3 是另一层许可。
- 本次逐项查看 [Flameshot 固定源码版本的图标目录](https://github.com/flameshot-org/flameshot/tree/e4357e38c36491a58db346c91a4559fc6dd98968/data/img/material/black)，而非仅凭图标库名称猜测形状。该项目可能包含适配或扩充，不宣称每份 SVG 与 Google 上游逐字一致。
- [Google 图标仓库](https://github.com/google/material-design-icons)区分旧 Material Icons 与新 Material Symbols；旧体系自 2022 年起不再更新。Symbols 的可变字重与光学尺寸不能直接归于 Flameshot 当前资源。

## 适配判断

| 范围 | 观察 | 判断 |
| --- | --- | --- |
| 复制、保存、钉图、撤销 | 轮廓熟悉，部分为实心，24px 下较厚实 | 高；可作为单项候选，但并无足够收益推翻整套定稿 |
| 画笔、荧光笔、文字、序号 | 笔头与文字语义清楚 | 高；需同时检查亮/暗主题、禁用与选中状态 |
| 矩形、椭圆、箭头 | 基本几何容易辨认 | 高；图形方向不同不是功能差异 |
| 重新框选、平移 | SelectionTool 使用 square-outline；与矩形过近。cursor-move 是四向箭头 | 中；已选 IconPark 的选区控制点与手形更符合用户要求 |
| 马赛克 | pixelate.svg 为逐渐变化的圆点阵列 | 低；用户已否决该视觉语义，保留交错像素块 |
| 独立模糊 | PixelateTool 含模糊模式；该资源子集未发现独立模糊入口图标 | 低；不能通过“同一工具支持模糊”推断它有合适的独立图标 |
| OCR 文字/表格/公式、排除区衍生操作、长截图 | Flameshot 当前按钮资源子集不能完整覆盖本产品语义 | 不完整；并非断言整个 Google 图标库没有候选 |

## 接入成本与决定

静态 SVG 可沿用本项目已有资产生成器和原生/Web 共用路径，无需字体文件或新增运行时依赖。实心轮廓的粗细不能简单当成 Tabler 的 stroke-width 调整；盲目加描边可能堵塞内部留白。

若后续确需更厚的图形，应单独比较 [Material Symbols](https://developers.google.com/fonts/docs/material_symbols) 的 20/24px、500/600 字重静态资源，再由用户确认具体替换项。新资产保留来源及 Apache 2.0 许可，不复制 Flameshot 应用代码。

当前实施继续遵循：全部工具/操作在上排，下排只放当前属性；24px 图标、适当加粗；马赛克/模糊常驻短标签；重新框选和平移保留 IconPark；不因未找到图标而隐藏表格或公式等功能。阶段安排见[截图与编辑能力对齐清单](screenshot-editor-roadmap.md)。
