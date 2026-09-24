# DICOM Service - Hospital Integration Gap Table + Executable Backlog

> Source of truth: authoritative source tree (QRSCP split into QRSCP.cs skeleton +
> CFind/CMove/CGet/CStore/Transfer partials), 163 unit + 40 integration tests all green
> (incl. real-SCU end-to-end drills). Updated this cycle. ASCII-safe.

## Legend
  [x] done         [~] partial       [ ] missing/planned
  Priority: P0 = must-have for clinical go-live
             P1 = should-have soon after     P2 = nice-to-have / specialist

## A) Gap table vs. typical hospital integration requests

| # | Hospital requirement            | St | Evidence (code/tests)                                    | Clinical meaning                                  | Gap / action                                              |
|---|---------------------------------|----|----------------------------------------------------------|--------------------------------------------------|------------------------------------------------------------|
| 1 | Accept images from modalities   | [x] | C-STORE SCP/SCU (CStore partial)                         | CT/MR/US modalities push studies disk-off         | -                                                          |
| 2 | Lost-transport resilience       | [x] | failed_queue -> retry on startup; throwOnError semantics | No image loss on network drop                     | B2 dead-letter/TTL for commit queue (done)                |
| 3 | Modality Worklist               | [x] | MWL SCU C-FIND over REST                                 | Tech pulls scheduled orders, avoids typos         | -                                                          |
| 4 | Query/Retrieve (QRSCP)          | [x] | C-FIND 4 levels + C-MOVE/C-GET partials                  | Studies retrievable by patient/study/series/image  | -                                                          |
| 5 | Storage Commitment             | [x] | N-ACTION persist + N-EVENT-REPORT push; B2 visibility   | Modality may delete local after confirmed archive  | -                                                          |
| 6 | MPPS                           | [x] | SCP for modality performed step                          | Track imaging start/end, feeding RIS               | -                                                          |
| 7 | UPS (worklists for DICOM apps) | [x] | UPS SCP N-CREATE/GET/SET/DELETE + subscribe              | Third-party DICOM apps get work items              | -                                                          |
| 8 | Web viewing (WADO-RS)          | [x] | DICOMweb QIDO/WADO/STOW + metadata/frames/rendered/thumb | Any-browser image access                           | -                                                          |
| 9 | Web auth for retrieval         | [x] | dicomweb/wado/viewer behind auth (AuthProtectionTests)   | Protected access                                   | -                                                          |
|10 | Login hardening                | [x] | PBKDF2 + LoginAttemptLimiter (acct+IP)                   | Brute-force / account-lockout protection           | -                                                          |
|11 | AE-title / context control     | [x] | AssociationGuard (AE title + app-context check)          | Only trusted modalities may connect                | -                                                          |
|12 | Workflow: MWL-vs-Order, UPS    | [x] | IheSwfWorkflowTests (MWL->MPPS->C-STORE->QR)             | Full order-to-archive flow                         | RIS-side orchestration remains external                   |
|13 | Structured Reports (SR)        | [x] | SR store/validate/content/references/KOS/generate + GET  | Radiologist report/summary as DICOM SR             | -                                                          |
|14 | Radiotherapy (RT)              | [x] | RadiotherapySupport whitelist + RtWorkflowTests          | RT dose/plan/RTSTRUCT stored, listed, retrievable  | -                                                          |
|15 | Presentation/Print             | [x] | PrintSCP (film session/box/image box) + PrintWorkflowTests| Camera/film printing                              | -                                                          |
|16 | Key Object Selection/Ref       | [x] | KOS SOP + /api/Sr/key-objects + reference links          | Mark key images                                    | -                                                          |
|17 | Real multidevice concurrency   | [x] | ConcurrencyLoadTests (8/16/32 concurrent C-STORE)        | 100s modalities pushing simultaneously             | -                                                          |
|18 | Transcode robustness           | [x] | TranscodeTests + DicomNegotiationTests (JPEG2000/JPEG-LS)| JPEG/JPEG-LS/JPEG2000 inbound                      | -                                                          |
|19 | Diagnostics workstation        | [ ] | OHIF bundled viewer (local /dicomweb)                    | Reading/report/print is a separate product         | Out of scope (needs RIS/PACS + viewer product)            |
|20 | Ops observability              | [x] | DicomMetrics + /api/Metrics + /api/Metrics/summary + /health | Find "why did X fail" quickly              | -                                                          |

Summary: all P0/P1/P2 backlog items below are implemented and tested. Remaining
work is out-of-scope (diagnostics workstation) and external (RIS-side SWF,
Weasis client software).

## B) Executable backlog (priority ordered)

### P0 - must-have for clinical go-live
- [x] B1  Visible-string safety: repo-wide encoding audit (no BOM, no mojibake);
         build 0/0, tests green.
- [x] B2  Storage Commitment failure visibility: NotificationStatus/Attempts/
         LastError/RemoteHost/Port/ExpireTime/ReferencedInstances; repository
         list/filter + purge; StorageCommitmentController (list/detail/repush/
         purge-expired); startup TTL cleanup; StorageCommitmentNotifier.
- [x] B3  Concurrency proof: per-destination/request DICOM clients (no static);
         ConcurrencyLoadTests at 8/16/32 concurrent C-STORE (all success, no
         loss/corruption).

### P1 - should-have shortly after go-live
- [x] B4  QIDO/WADO auth consistency: both behind /api|/dicomweb auth;
         AuthProtectionTests covers QIDO study/series/instances/metadata + WADO.
- [x] B5  IHE SWF/MWF walk-through: IheSwfWorkflowTests full chain.
- [x] B6  Transcode coverage: TranscodeTests (JPEG2000-lossless, JPEG-LS-lossless
         round-trip) + negotiation fallback to Implicit VR Little Endian.
- [x] B7  Metrics + alerting: DicomMetrics counters/gauges + /api/Metrics
         (Prometheus text) + /api/Metrics/summary + anonymous /health.

### P2 - specialist / nice-to-have
- [x] B8  SR: SOP whitelist, validation, content tree, image references, KOS,
         generation, concept/code query keys, time fields, STOW-RS/C-GET/SC.
- [x] B9  KOS: Key Object Selection stored/queryable + key-image links.
- [x] B10 Basic Grayscale Print SCP: film session/box/image box flow accepted,
         persisted, listed (PrintWorkflowTests).
- [x] B11 RT import: RTSTRUCT/RTDOSE/RTPlan/RTSet/RTImage whitelist + storage/list.
- [x] B12 Config doc: docs/hospital-integration.md (AE/ports/auth/endpoints).

## C) Always-commandable proof of base state
```
HEAD/clean tree        = local == origin/master (worktree clean)
dotnet build           = 0 errors / 0 warnings
dotnet test (Release)  = 163 unit + 40 integration passed / 0 failed  (net8.0, real-SCU drills)
```

## D) Viewer / OHIF
- Primary viewer: OHIF (wwwroot/dicomviewer) wired to local /dicomweb (QIDO/WADO/
  STOW/metadata/frames/rendered/thumbnail); legacy Cornerstone viewer retired.
- Weasis: external client, one-time study-scoped token via /api/Weasis/token.
- Browser smoke: tools/ohif-smoke/smoke.mjs (CDP; data-path PASS). Visual render
  confirmation requires a display environment.
