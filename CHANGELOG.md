# Changelog

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
