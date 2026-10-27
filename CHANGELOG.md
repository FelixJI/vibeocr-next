# Changelog

## 0.8.3

### Features

- **ocr:** 随包提供 Paddle 推理并完善截图与环境管理 (#285) (47a9526)

### Bug Fixes

- **bootstrapper:** 按实际依赖检测 .NET Runtime (#283) (15adeab)

## 0.8.2

### Bug Fixes

- **bootstrapper:** 按实际依赖检测 .NET Runtime (#283) (15adeab)

## 0.8.1

### Features

- **ocr:** 退役原生 Paddle 与公式视觉推理入口 (#280) (765a67f)
- **paddle:** 将表格与版面解析迁移到 ONNX Runtime (#279) (ec9ec2b)
- **ocr:** 贯通 Paddle ONNX 三类设备与模型档位 (#276) (6276026)
- **recognition:** 接入 RapidTable 与设备加速并简化截图工具栏 (#269) (eab903b)
- **workbench:** 优化截图编辑与预览并拆分图像转换 (#265) (747f1b6)

### Bug Fixes

- **ci:** 按内容修订校验自动取字并移除重复构建 (#282) (a6b0ae0)
- **runtime:** 固定环境目录并阻止过期发布计划 (#263) (442b466)

### Dependencies

- **web:** 升级 Vite 修复开发服务器安全漏洞 (#266) (10b650c)
- **runtime:** 统一安装器和运行环境依赖版本 (#264) (b3afd9a)

## 0.8.0

### Features

- **editor:** 完善排版输出预设与工具栏定制 (#256) (b42aab9)
- **pins:** 完善贴图编辑与截图历史恢复 (#255) (fe5fee1)
- **editor:** 增加聚光灯、放大镜与水印工具 (#254) (58063dc)
- **project:** 支持可编辑工程保存与恢复 (#253) (bbbcaa7)
- **editor:** 增强遮挡修补与重复内容处理 (7daeadc)
- **capture:** 完善四向长截图与预览纠偏 (#246) (9aa07c7)
- **editor:** 支持系统图片与文本导入为独立对象 (#252) (0f0ccee)
- **ocr:** 为完成画面提供显式识别模式 (#251) (4986a68)
- **editor:** 增加对象多选图层分组与变换 (#249) (4772a4f)
- **editor:** 增强文字序号与说明标注 (#247) (eec926a)
- **editor:** 完善几何标注控制点与工具属性 (#244) (690954f)
- **capture:** 统一截图模式与窗口恢复 (#245) (9052712)
- **editor:** 统一截图工具栏并完善透明度、原图校对与环境路径 (#243) (7dad8a3)

## 0.7.4

### Features

- **workbench:** 完善截图校对与识别结果交互 (#225) (2620462)

## 0.7.3

### Features

- **workbench:** 补齐 Windows 快捷键与主题联动 (#223) (f6a85f5)
- **codes:** 将二维码与条码解码迁入本地宿主 (#219) (fa9d4e1)
- **pdf:** 补齐多文档保存与批量导出 (#211) (6e3f351)
- **pdf:** 添加高清文字层检查与原子校正 (#210) (ed230e3)
- **pdf:** 补齐页面方向处理与插页重排 (#209) (e0fc486)
- **pdf:** 贯通当前文档文字层处理与搜索层保存 (27a2a29)
- **environments:** 增加可恢复的环境与依赖清理 (#206) (566b57c)
- **environments:** 展示真实安装进度与实时日志 (#205) (6fb7648)
- **settings:** 简化运行环境选择与安装确认 (#204) (e72a6af)
- **screenshot:** 增加稳定选区动作与显式识别入口 (#192) (38ecc6c)
- **settings:** 持久化默认识别模式并统一任务继承 (#189) (a4b031f)

### Bug Fixes

- **capture:** 用像素首帧握手收口截图窗口生命周期 (#222) (39a27e3)
- **workbench:** 完善截图交互与 MinerU 文档识别配置 (#203) (9cc72d9)
- **workbench:** 完善截图工具栏并隔离图片会话与启动状态 (#193) (bb78c97)
- **editor:** 统一实时画布与截图输出语义 (#190) (f30b650)

### Performance

- **workbench:** 收口功能归属并限制多窗口状态投递和加载 (#221) (d7934ed)
- **runtime:** 按显式计算需求准备唯一运行环境连接 (#220) (389ca1f)
- **runtime:** 使用随包 uv 统一环境安装执行器 (#218) (f138302)
- **editor:** 按帧合并绘制并清理取消状态 (#191) (028ecdf)

## 0.7.2

### Bug Fixes

- **platform:** 单实例监听器改用专用线程避免线程池饥饿 (#182) (631b0b3)
- **platform:** 消除单实例管道监听的调度竞态 (#181) (0e1719d)
- **runtime:** 消除虚拟环境启动器的进程绑定竞态 (#179) (8fa4433)
- **app:** 统一运行环境设置并修复截图与悬浮工具栏交互 (#178) (e2af2f4)

## 0.7.1

### Features

- **environments:** 提供默认 RapidOCR 环境并拆分模型来源 (#173) (0697bcb)

### Bug Fixes

- **runtime:** 修复安装注册前取消与回执竞态 (#176) (0965fb9)
- **smoke:** 保留工作台启动失败阶段与诊断 (#175) (890df46)
- **e2e:** 使用生产产物隔离网页冷启动 (#174) (c74cb6a)

## 0.7.0

### Features

- **toolbar:** 支持收起时长和自适应主题 (#164) (43fceb3)
- **editor:** 增加本地去水印预览与可撤销修补 (#163) (821b2be)
- **recognition:** 在识别前屏蔽区域并保持贴图取字一致 (9120dba)
- **hotkeys:** 按实键录入并协调原生注册生命周期 (#152) (28d2d22)
- **editor:** 共用截图现场与独立图片编辑 (#159) (7e5baa5)
- **settings:** 合并关于诊断并对齐设置操作 (#154) (3d8338e)
- **settings:** 推荐兼容环境并明确有效下载来源 (#153) (2588cea)
- **codes:** 统一二维码条码生成与图片识别预览 (6e85848)
- **environments:** 复用兼容环境与可信模型缓存 (#149) (455be93)

### Bug Fixes

- **toolbar:** 修复固定工具栏冷启动 XamlRoot 未就绪崩溃 (#170) (580564e)
- **smoke:** 对齐原生环境入口与异步就绪等待 (#166) (e5984c6)
- **shell:** 修复任务栏图标与托盘菜单焦点 (#151) (6b2ac1e)
- **capture:** 统一单击确认与八点缩放手势 (#147) (8acd2db)
- **runtime:** 核对进程实例并稳定心跳验收 (81e26d4)
- **ci:** 校准完整质量入口的运行预算 (55d70aa)
- **runtime:** 修复运行状态与服务重连一致性 (#146) (03e97bd)

### Dependencies

- **toolchain:** 升级正式依赖与 SDK 并适配测试运行器 (#162) (1725b96)

## 0.6.2

### Features

- **capture:** 接入有界纵向长截图与统一图片会话 (#117) (cd73761)
- **paddle:** 贯通多类型识别参数与结构化结果 (6d43c45)
- **environments:** 展示并绑定环境安装来源 (8c5fab4)
- **workbench:** 支持截图原位选字与桌面贴图 (#114) (84d71f2)
- **runtime:** 引入具名虚拟环境与安全切换 (#113) (0afc80c)
- **shell:** 统一原生快捷动作与智能截图入口 (#112) (204da48)
- **capture:** 增加独立截图编辑会话与最终像素识别 (#111) (39dfcf4)

### Bug Fixes

- **desktop:** 核实安装取消终态并排除滚动控制窗污染 (#131) (7980df2)
- **workbench:** 回收 PDF 状态与小窗口布局修复 (#130) (9978bc1)
- **runtime:** 对齐受管理环境启动与探针预算 (#126) (709535d)
- **environment:** 保留环境创建失败的脱敏诊断 (#124) (2714678)

### Dependencies

- **deps:** bump undici from 8.10.0 to 8.11.2 in /src/dotnet/VibeOCR.App/WebAssets (#120) (fbcde89)
- **deps:** 移除未使用的 Python 开发与构建依赖 (#128) (76fec48)

## 0.6.1

### Features

- **qrcode:** 在本地生成二维码并正确编码保存图片 (#101) (fea87ad)
- **mineru:** 支持远程解析并修复工作台资源展示 (#83) (15f7299)

### Bug Fixes

- **runtime:** 保留升级选装意图并修复网页任务请求 (#93) (14d6908)
- **runtime:** 遵循维护失败的权威重试语义 (#84) (d41d975)
- **settings:** 首次进入设置时加载运行环境状态 (#81) (7aba437)
- **batch:** 移除无效并发设置并明确自动调度 (#80) (8ad0924)

## 0.6.0

### Features

- **runtime:** 贯通安装计划确认与独立维护状态 (#75) (ff3c390)

## 0.5.2

### Features

- **screenshot:** 完善截图交互与快捷键状态反馈 (#65) (7860d71)

### Bug Fixes

- **app:** 拆分 T4/T5 里程碑以度量 runtime ensure 阶段 (#67) (3d58ed9)
- **app:** 修复 Supervisor 启动期间识别命令未等待即失败 (#66) (83a2683)

## 0.5.1

内部改进与维护。

## 0.5.0

### Features

- **app:** 桌面悬浮工具栏与贴边隐藏 (#62) (484af48)

### Bug Fixes

- **app:** 修复高 DPI 窗口几何与工作台高度链自适应 (#61) (c407a1e)
- **platform:** 修复便携布局注册与安装器声明失配 (#60) (ad3fb4e)

## 0.4.3

### Features

- **update:** 支持相邻版本 Velopack 增量更新 (#55) (9134fb0)

### Bug Fixes

- **app:** 修复启动依赖并完善截图标注 (#57) (1f34482)

## 0.4.2

### Dependencies

- **toolchain:** 更新 SDK 与应用依赖 (#53) (2a68c1b)

## 0.4.1

### Features

- **recognition:** 落地正式识别模式契约 (#51) (bc4f27f)

### Bug Fixes

- **runtime:** 修复首启安装范围与提交取消 (#50) (2b72411)

## 0.4.0

### Features

- **release:** Portable 成为唯一用户资产并保留 Velopack feed (#41) (c12fc19)
- **portable:** 切换完全便携状态根并收口可变路径 (#40) (e9dba1a)
- **app:** 实现运行时维护操作编排与回显 (#39) (29e74b7)
- **app:** 实现 OCR 引擎、功能与下载源选择界面 (#38) (67112ea)
- **platform:** 新增运行时选择服务与引擎偏好迁移 (#37) (d188bd2)
- **protocol:** 升级 Protocol SDK 至 2.7.1 并接入选择契约 (#36) (102fef0)

### Bug Fixes

- **release:** 规范便携包文件名 (#48) (3acdbba)
- **settings:** 恢复模型源选择 (#47) (587f8b8)
- **release:** 收敛基础运行时产品闭包 (#46) (c67026c)
- **next:** 收敛 Portable 路径与维护协调契约 (#43) (0f7422d)
- **bootstrapper:** 启动失败写入用户日志 (#34) (8c20973)

## 0.3.1

### Features

- **updater:** 迁移 Velopack 更新链 (#27) (15dc590)
- **product:** 完成 VibeOCR 产品化基线 (#25) (e24a267)

### Bug Fixes

- **settings:** 原子写入桌面配置 (#30) (d09b9b8)
- **automation:** 避免污染 scripts 命名空间 (#29) (a027e0e)
- **update:** 修复更新代理回退 (aea7bd1)

### Dependencies

- **protocol:** 升级 .NET Protocol SDK 至 2.5.0 (#31) (435898d)

## 0.3.0

### Features

- **workbench:** 迁移统一 Web 工作台与原生深宿主 (#18) (6d70f2b)
- **runtime:** 接入可靠维护与组件修复 (#14) (e2648eb)

### Bug Fixes

- **platform:** 吸收 supervisor 启动期后代 (afc885f)
- **release:** 修复 supervisor 退出竞态并恢复 0.3.0 发版 (35a0668)
- **protocol:** 解耦 SDK 与运行时协商 (#16) (272c009)
- **ci:** 修复镜像标签同步并完善六仓治理 (#15) (ad38f01)

## 0.2.0

### Features

- **runtime:** 展示安装进度与 Backend 依赖状态 (#12) (7d49a66)
- **runtime:** 接入统一 Runtime Host 契约 (#5) (16c04ad)
- **ci:** 统一 CI/CD 自动化 (#6) (4cd832a)
- publish VibeOCR Next 0.1.0 preview (3b3328b)

### Bug Fixes

- **release:** 统一候选派生资产归属 (#10) (cfb0ca8)
- **ci:** 修复发布 tag 推送认证 (#9) (6b3ba9e)
- align startup benchmark arguments (a4158c8)
- complete standalone Next release tooling (c6e0fb0)
- lock published protocol packages (41bdebb)
- compare component locks semantically (d2a31af)

### Performance

- **ci:** 支持统一分片门禁与取消过时 PR 运行 (#11) (436d066)

All notable changes to this project will be documented in this file. The format
is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
