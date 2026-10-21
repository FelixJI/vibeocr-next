# 托管运行环境存储布局与固定路径

适用范围：`src/runtime/vibeocr/runtime/environments/managed_environments.py` 与
`managed_cleanup.py`（多环境管理存储）。本文只描述磁盘布局与升级/迁移策略，
接口契约以代码与测试为准。

## 固定路径布局

- 每个环境的 venv 直接位于唯一固定路径 `environments/<id>`，升级前后不变；
  新环境的 `id` 为名称/用途派生的可读 slug（碰撞追加 `-2`/`-3`），不再生成 UUID，
  也没有 `revisions/<N>` 或随机后缀层级，减少 Windows 路径长度压力；这是存储布局的
  根因修复，不是 UI 截短。
- 安装/修复事务使用两个固定槽位（每环境各一个，绝不随升级次数累积）：
  - `environments/.staging/<id>`：候选 venv 构建位置；
  - `environments/.retired/<id>`：换入时旧 venv 的整体退役位置。
- 每环境私有状态（缓存/模型/临时目录）仍在固定路径 `state/environments/<id>`，
  与 venv 位置无关。

## 安装事务与崩溃恢复

1. 候选在 `.staging/<id>` 构建后先在 staging 探针（锁外、可取消）；
2. 提交在 store 锁内按序执行：journal 落盘（`state/environment-swaps/<id>.json`）
   → 旧 venv 移入 `.retired/<id>` → 候选换入固定路径 → 在最终路径复检探针
   （兜底 venv 随目录迁移的绝对路径/relocation 破损；入口均按 `python -m`
   启动，不依赖控制台脚本）→ 注册表原子提交 → 清除 journal；
3. 换入失败时回滚目录与归属；若文件占用阻碍回滚，保留 journal 并保护相关目录，待占用解除后恢复；
4. 崩溃中断：journal 保留，下一次 `list`/安装/切换/清理在
   `_recover_pending_swap` 中以**注册表提交为分界**收敛——注册表记录
   等于候选即提交已完成，只收尾清除 journal；否则一律保守回滚（不前滚
   候选），旧副本/原缺失状态复位；回滚可重入，清除失败保留 journal 与
   保护，下次扫描重试。

## 归属与清理保护

- cleanup ledger 只登记产品自建路径；换入退役时仅**镜像**既有归属记录，
  未知/未登记数据随目录移动但不获得可删除归属；
- 退役槽位回收时只删除已登记子树，存在未登记数据时 fail closed
  （提示走空间清理预览或人工检查），不推断归属、不删除未知数据；
- 存在 pending journal 的环境，其全部路径在清理扫描中一律受保护；清理
  计划冻结证据包含 journal 状态，preview 与 run 之间 journal 变化即失效。

## 旧环境升级与迁移策略

- 历史 `<id>/revisions/<N[-hex]>` 布局的记录（含旧 UUID id）照常读取、
  启动、切换与清理，不做后台自动迁移；
- 对旧布局环境重新安装即迁移：旧目录树整体移入 `.retired/<id>` 原样保留
  （内部内容不变），新 venv 落在固定路径 `environments/<id>`，历史 UUID id 保持不变；
- 退役树中的已登记旧版本目录可通过空间清理回收；未知目录永远保留，
  且会阻止后续换入复用该退役槽位，待人工处理；
- 不存在无界累积：新布局下每环境最多一个 staging 与一个 retired 槽位，
  下一次安装/修复会按 ledger 回收复用。
