# DicomSCP 权威代码地图(codebase map)

> 来源:本会话**逐一实读真实源文件** + `git ls-files`(git 跟踪真身),非记忆、非文档快照。
> 会话前置:**SR-0 已提交**(HEAD `0d8e44c`"增强:结构化报告存储(SR)显式白名单协商与落盘闭环")。
> 本文件是 SR-1(结构化报告取回闭环)工作开展前的唯一权威参照。

## A) 仓库总览

- 根目录 = `DicomSCP/`(主服务,ASP.NET Core + Kestrel + fo-dicom)
- `DicomSCP.Tests/` = 单元测试(≥112)
- `DicomSCP.IntegrationTests/` = **E2E 真机集成**(真实 SCU↔SCP 全链路)
- 顶层仓库还有 `DICOM-hospital-gap-backlog.md`(差距/执行backlog)与 `docs/`(快照/地图)

## B) Services/ 权威清单(git 跟踪,22 文件)

| 文件 | 角色 |
|---|---|
| `CStoreSCP.cs` | **C-STORE SCP(存储落盘)**,SR 显式白名单协商(`IsSrStorage`)已落地 |
| `QRSCP.cs` + partial 族 | Query/Retrieve SCP,C-FIND(Study/Series/Image级)、C-MOVE、C-GET、C-STORE桩、传输协商 |
| `QRSCP.CFind.cs` | C-FIND 检索管线(水平/通配/日期范围/UID格式化) |
| `QRSCP.CGet.cs` / `QRSCP.CMove.cs` / `QRSCP.CStore.cs`(拒绝桩) / `QRSCP.Transfer.cs` | QR 其余运算 |
| `WorklistSCP.cs` | C-FIND worklist(MWL) |
| `MppsSCP.cs` | MPPS(Create/Set) |
| `UpsSCP.cs` | Unified Procedure Step(N-CREATE/SET) |
| `PrintSCP.cs` / `PrintSCU.cs` | 打印 SCP/SCU |
| `MwlScu.cs` | MWL SCU 出站 |
| `StoreSCU.cs` | C-STORE 出站 |
| `QueryRetrieveSCU.cs` | QR SCU 出站 |
| `StorageCommitmentSCP.cs` | 存储承诺 N-ACTION |
| `MwlWorkflowTests` 家族(测试侧) | 集成工作流 |
| `AssociationGuard.cs` | 关联校验(白名单/连用守卫) |
| `DicomNegotiation.cs` | 传输语法/抽象语法 UID 常量 + 协商辅助 |
| `DicomServer.cs` | SCP 启动编排(fo-dicom) |
| `DicomWebHelpers.cs` / `DicomLogger.cs` / `LoginAttemptLimiter.cs` | Web/日志/登录限流辅助 |

## C) SR-0 状态(权威)

- 已提交:`0d8e44c` 显式 SR SOP 白名单协商(C-STORE) + SR 落盘闭环。
- 协商层:`DicomNegotiation`/`CStoreSCP.IsSrStorage` 白名单显式接受 SR(SOP Class: SR 复合类)。
- 落盘:SR 与非图像同走存储管线(无像素数据也不被拒)。

## D) SR-1 目标(本会话)

结构化报告 **取回闭环**:SR 经 C-STORE 落盘 → QR C-FIND 检索到该 SR 条目 → QIDO/WADO 经 REST 取回。AC = 真实 SCU C-STORE SR → 断言文件落盘 + QR C-FIND 返回 + HTTP 取回成功。

## E) 权威构建命令

```powershell
dotnet build DicomSCP.csproj -c Release          # 0 错误
dotnet test DicomSCP.Tests -c Release            # 单元全绿
dotnet test DicomSCP.IntegrationTests -c Release # E2E 真机全链路
```
