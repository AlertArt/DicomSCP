# 医院对接配置清单 (Hospital Integration Reference)

> 权威来源：本文件按当前 `appsettings.json` 与源码默认值整理。实际部署请以现场 `appsettings.json` 为准。
> 本机服务地址示例：DICOM 端口 `11112–11118`，Web(HTTP) 端口 `5000`。

## 1. DICOM 服务（AE Title / 端口 / 用途）

| 服务 | AE Title | 默认端口 | 方向 | 用途 | 备注 |
|---|---|---|---|---|---|
| Storage SCP | `STORESCP` | 11112 | 接收 | 接收模态影像与对象（CT/MR/US/…、SR、KOS、RT） | 归档根目录 `DicomSettings:StoragePath` |
| Modality Worklist SCP | `WORKLISTSCP` | 11113 | 接收 | 模态拉取预约工作列表（C-FIND） | 空计划日期按“当天”匹配 |
| Query/Retrieve SCP | `QRSCP` | 11114 | 接收 | C-FIND（Patient/Study/Series/Image）、C-MOVE、C-GET | C-MOVE 目标见 `QRSCP:MoveDestinations` |
| Print SCP | `PRINTSCP` | 11115 | 接收 | Basic Grayscale/Color Print Management（N-CREATE/SET/ACTION） | 生成打印任务，见 `/api/Print` |
| Storage Commitment SCP | `STORECOMMITSCP` | 11116 | 接收 | N-ACTION 提交，回推 N-EVENT-REPORT | `PushRetryCount`、`RecordTtlDays` |
| MPPS SCP | `MPPSSCP` | 11117 | 接收 | 检查执行步骤 N-CREATE/N-SET | — |
| UPS SCP | `UPSSCP` | 11118 | 接收 | 统一程序步骤 N-CREATE/GET/SET/DELETE + 订阅 | — |
| Print SCU（出站） | `PRINTSCU` | — | 发送 | 向打印机发送打印请求 | 打印机列表见 `DicomSettings:PrintSCU:Printers` |
| Query/Retrieve SCU（出站） | `QRSCU` | — | 发送 | 向外部节点查询/检索/工作列表 | 节点见 `QueryRetrieveConfig:RemoteNodes` |

## 2. AE 校验（按需收紧）

各 SCP 均支持 Calling AE 白名单：

```json
"QRSCP": {
  "AeTitle": "QRSCP",
  "Port": 11114,
  "ValidateCallingAE": true,
  "AllowedCallingAEs": ["CTSCANNER", "MRSCANNER", "WORKSTATION1"]
}
```

- `ValidateCallingAE=false`：接受任意 Calling AE（对接初期便于联调）。
- `ValidateCallingAE=true`：仅接受 `AllowedCallingAEs` 中的 AE；同时校验应用上下文。详见 `AssociationGuard`。
- Storage SCP 的白名单在 `DicomSettings:Advanced`（`ValidateCallingAE` / `AllowedCallingAEs`）。

## 3. Web / HTTP 接口（端口 5000）

```json
"Kestrel": { "Endpoints": { "Http": { "Url": "http://0.0.0.0:5000" } } }
```

鉴权策略（会话 Cookie）：

| 路径 | 是否需登录 | 说明 |
|---|---|---|
| `/api/**` | 是 | 管理 API（登录接口 `/api/Auth/login`、改密、会话查询除外） |
| `/dicomweb/**` | 是 | QIDO-RS / WADO-RS / STOW-RS |
| `/wado` | 是 | 传统 WADO-URI |
| `/viewer/ohif/**`、`/viewer/weasis/**` | 是 | 查看器辅助端点 |
| `/health` | 否 | 探活（无 PHI），供负载均衡/CI |
| 静态资源（`/dicomviewer/**`、页面） | 否 | 无 PHI，数据接口仍需登录 |

初始口令 `admin/admin`，首次登录强制改密。登录限流见 `Auth`（`MaxLoginAttemptsPerIp`、`LockoutMinutes`）。

## 4. DICOMweb 端点（查看器/集成方使用）

基路径 `/dicomweb`：

- QIDO-RS：`GET /studies`、`/studies/{study}/series`、`/studies/{study}/series/{series}/instances`
- WADO-RS：实例检索、`/studies/{study}/metadata`、`/studies/{study}/series/{series}/metadata`、
  `/studies/{study}/series/{series}/instances/{instance}/metadata`、`/frames/{n}`、`/rendered`、`/thumbnail`
- STOW-RS：`POST /studies`（`multipart/related; type="application/dicom"`）

主查看器为 OHIF（`/dicomviewer`），直连上述标准端点。

Weasis（可选外部客户端）：外部进程无法携带会话 Cookie，需先登录调用
`GET /api/Weasis/token?studyInstanceUid=…` 获取绑定研究的一次性令牌，再用返回的
`weasisUrl`（manifest 与 `/wado` 均带 `token`）打开；令牌 10 分钟有效且限定该研究。

## 5. 存储与数据库

```json
"ConnectionStrings": { "DicomDb": "Data Source=./db/dicom.db;Default Timeout=30" }
```

- 归档路径：`DicomSettings:StoragePath`（默认 `./received_files`），目录结构 `yyyy/MM/dd/{StudyUID}/{SeriesUID}/{SOPInstanceUID}.dcm`。
- 临时目录：`DicomSettings:TempPath`。
- 数据库：SQLite（WAL）；表含 Patients/Studies/Series/Instances、SR 字段与 `SrReferencedInstances`、Worklist、MPPS、UPS、StorageCommitments、PrintJobs。
- 入库为批量异步（队列）；失败自动重试并落盘 `failed_queue`，启动时重放。

## 6. 运维接口（登录后）

| 端点 | 说明 |
|---|---|
| `GET /api/Metrics` | Prometheus 文本指标（C-STORE/C-FIND/C-MOVE/C-GET、入库队列深度/失败/重试、Storage Commitment） |
| `GET /api/Metrics/summary` | 指标 JSON 摘要 + 运行时长 |
| `GET /health` | 探活（匿名） |
| `GET /api/StorageCommitment?status=FailuresExist` | 存储承诺失败可见性 |
| `POST /api/StorageCommitment/{uid}/repush` | 重推 N-EVENT-REPORT |
| `POST /api/StorageCommitment/purge-expired` | 清理过期记录（TTL） |
| `GET /api/Print` | 打印任务列表 |
| `GET /api/Sr/{sop}/content` · `/{sop}/references` · `/referencing/{sop}` | SR 内容树 / 报告→图像 / 图像→报告 |
| `GET /api/Sr/key-objects?studyInstanceUid=…` | 关键对象选择（KOS）列表 |
| `GET /api/Weasis/token?studyInstanceUid=…` | 签发绑定研究的一次性令牌（10 分钟），供外部 Weasis 免会话访问 |

## 7. 对接步骤清单

1. 现场修改 `appsettings.json`：`AeTitle`、各 SCP `Port`、`StoragePath`、`AllowedCallingAEs`。
2. 模态/工作站配置本机为：Storage SCP（AE+端口），按需配置 MWL/MPPS/Storage Commitment/Print/QR。
3. 打开 `ValidateCallingAE` 并录入来源 AE。
4. 首次登录 `http://<host>:5000`，使用 `admin/admin` 并改密。
5. 验证：C-ECHO 各服务 → 模态送图 → `/api/Images` 或 OHIF 查看 → QR C-FIND/C-MOVE。
6. 监控：接入 `/api/Metrics`，告警 `/health` 与 `StorageCommitment` 失败项。
