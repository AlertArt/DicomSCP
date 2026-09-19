---
文件: DICOM-hospital-gap-backlog.md  (已存在,本会话续写)
补充权威事实快照 (提供者 ChatGPT, ASCII-only, 无BOM):

1) QRSCP 拆分终态 (git authority, HEAD=origin/master, worktree clean):
   QRSCP.cs            225  (骨架, partial)
   QRSCP.CFind.cs      422  (partial)
   QRSCP.CMove.cs      225  (partial)
   QRSCP.Transfer.cs   301  (partial)
   QRSCP.CGet.cs        98  (partial)
   QRSCP.CStore.cs      35  (partial)

2) dotnet build (Release) = 0 错误 / 0 警告
3) dotnet test            = 112 通过 / 0 失败 (含真实 SCU 端到端:
   C-STORE / C-FIND / C-MOVE / C-GET / UPS / MPPS / Storage Commitment / MWL / Push)

4) 审核要点 (QRSCP as DICOM service):
   - 查询/检索/工作流 SCP: CFind/CMove/CGet + UPS/MPPS/StorageCommitment/MWL 全在
   - 存储 SCP 独立: CStoreSCP.cs (重试/落盘重放/Storage Commitment)，与 QRSCP 分离
   - 协商表驱动: Services/DicomNegotiation.cs
   - 安全: PBKDF2, login 双维限流, AssociationGuard, AE 白名单

5) 已知缺口 (未验证/未实现):
   - SR (结构化报告) SOP Class 未入存储白名单 -> 当前形态下 SR 不可落库
     (CStoreSCP 协商分支 L119 仅 IsImageStorage, SR 是否被拒取决于 fellowOak
     对 SR SOP 的 StorageCategory 运行时归类 -- 需反射确认)
   - RT / SR 专科, 大规模并发压测背书, 运维指标/告警 (见主 backlog P1/P2)

(end)
