# DICOM Service - Hospital Integration Gap Table + Executable Backlog

> Source of truth: authoritative source tree (QRSCP split into QRSCP.cs skeleton +
> CFind/CMove/CGet/CStore/Transfer partials), 112 automated tests all green
> (incl. real-SCU end-to-end drills), multi-round Authentic-Verify snapshots.
> Generated: this session. ASCII-safe.

## Legend
  [x] done         [~] partial       [ ] missing/planned
  Priority: P0 = must-have for clinical go-live
             P1 = should-have soon after     P2 = nice-to-have / specialist

## A) Gap table vs. typical hospital integration requests

| # | Hospital requirement            | St | Evidence (code/history)                                  | Clinical meaning                                  | Gap / action                                              |
|---|---------------------------------|----|----------------------------------------------------------|--------------------------------------------------|------------------------------------------------------------|
| 1 | Accept images from modalities   | [x] | C-STORE SCP/SCU (CStore partial)                         | CT/MR/US modalities push studies disk-off         | -                                                          |
| 2 | Lost-transport resilience       | [x] | failed_queue -> retry on startup; throwOnError semantics | No image loss on network drop                     | + failed queue TTL/dead-letter cap                        |
| 3 | Modality Worklist               | [x] | MWL SCU C-FIND over REST (af55e03)                       | Tech pulls scheduled orders, avoids typos         | -                                                          |
| 4 | Query/Retrieve (QRSCP)          | [x] | C-FIND 4 levels + C-MOVE/C-GET partials                  | Studies retrievable by patient/study/series/image  | -                                                          |
| 5 | Storage Commitment             | [x] | N-ACTION persist + N-EVENT-REPORT push (a3b853c)         | Modality may delete local after confirmed archive  | -                                                          |
| 6 | MPPS                           | [x] | SCP for modality performed step (a0e46dd)                | Track imaging start/end, feeding RIS               | -                                                          |
| 7 | UPS (worklists for DICOM apps) | [x] | UPS SCP N-CREATE/GET/SET/DELETE + subscribe (1d04b18)    | Third-party DICOM apps get work items              | -                                                          |
| 8 | Web viewing (WADO-RS)          | [x] | DICOMweb full endpoints (abc4ed5)                        | Any-browser image access                           | -                                                          |
| 9 | Web auth for retrieval         | [x] | dicomweb/wado/viewer behind auth (a75c570)               | Protected access                                   | -                                                          |
|10 | Login hardening                | [x] | PBKDF2 + LoginAttemptLimiter (acct+IP) (02df058/2fa911e) | Brute-force / account-lockout protection           | -                                                          |
|11 | AE-title / context control     | [x] | AssociationGuard (9f5bab0), AE title+app-context check   | Only trusted modalities may connect                | -                                                          |
|12 | Workflow: MWL-vs-Order, UPS    | [~] | MWL + UPS + MPPS present, CPS/Performed PS orchestration| Full IHE SWF/MWF requires RIS-side integration     | Build SWF profile walk-through test                       |
|13 | Structured Reports (SR)        | [ ] | -                                                        | Radiologist report/summary as DICOM SR             | Add SR SOP class (C-STORE + storage)                      |
|14 | Radiotherapy (RT)              | [ ] | -                                                        | RT dose/plan/RTSTRUCT                              | Add RT series support or defer                          |
|15 | Presentation/Print             | [~] | PrintSCU basic, deadlock fix                             | Camera/film printing                               | Full Basic Grayscale Print SCP                          |
|16 | Key Object Selection/Ref       | [ ] | -                                                        | Mark key images                                    | Add KOS SOP class                                        |
|17 | Real multidevice concurrency   | [~] | only single static DICOM client                          | 100s modalities pushing simultaneously             | Shared client pool + backpressure, load-test report       |
|18 | Transcode robustness           | [~] | TransferSyntax negotiation + transcode-if-needed        | JPEG/JPEG-LS/JPEG2000 inbound                       | Verify codec coverage + add JPEG2000/JPEG-LS tests        |
|19 | Diagnostics workstation        | [ ] | -                                                        | Reading/report/print is a separate (non-PACS)      | Out of scope (needs RIS/PACS + viewer product)            |
|20 | Ops observability              | [~] | logs, retries, queues, DB migrations/WAL                | Find "why did X fail" quickly                      | Add metrics endpoint + alerting budget                   |

Summary: core retrieval/storage/workflow/web is production-shape for routine
radiology; gaps concentrate in SR/RT/KOS/Print SCP (specialist), a shared
client pool + load-test for concurrency, and a metrics/observability surface.

## B) Executable backlog (priority ordered)

### P0 - must-have for clinical go-live
- [ ] B1  Visible-string safety: full pass to make all on-disk UTF-8 (no BOM,
         no mojibake) via authoritative .NET write; audit Services/*.cs + git
         baseline.  AC: build 0/0, tests stay green, `git status` clean.
- [ ] B2  Storage Commitment failure visibility: dead-letter + TTL for
         committed queue.  AC: a failed SC never silently vanishes; it is
         visible/retryable with expiry and reseat.
- [ ] B3  Concurrency proof: shared DICOM client pool (Lazy/Queue-based
         clients, not one static), backpressure, and a load test (N
         concurrent C-STORE SCUs e.g. 8/16/32) recorded.  AC: at 32 concurrent
         no space/idle-timeout corruption; test artifact committed.
  Ref: currently "single static DicomClient" (d523b12d08) - see service.

### P1 - should-have shortly after go-live
- [ ] B4  QIDO/WADO auth consistency: ensure QIDO-RS equally behind auth
         (WADO already is).  AC: both WADO and QIDO require login by default.
- [ ] B5  IHE SWF/MWF walk-through test: MWL -> MPPS -> C-STORE -> QR full
         chain as one integration test with real modalities roles.
         AC: automated test simulating full order-to-archive flow.
- [ ] B6  Transcode coverage: add JPEG2000-lossless and JPEG-LS into
         transcode-tests; verify negotiator falls back safely.
         AC: tests for both syntaxes, fallback = implicit VR little endian.
- [ ] B7  Metrics + alerting: prometheus-style counters (scp acks, failures,
         queue depth, retries) + summary endpoint.  AC: counters exported via
         HTTP, a health/metrics check in CI.

### P2 - specialist / nice-to-have
- [ ] B8  SR: add StructuredReport SOP class storage + retrieval (C-STORE +
         C-FIND by modifier).  AC: ARS SR stored/retrieved, tested.
- [ ] B9  KOS: Key Object Selection, C-STORE + C-FIND.  AC: KOS stored/queryable.
- [ ] B10 Basic Grayscale Print SCP (or full print workflow).  AC: BMP/grayscale
         page accepted, listed, acknowledged.
- [ ] B11 RT import (RTSTRUCT/RTDOSE/RTPlan) at least as object storage
         (accept + store + list).  AC: RT series C-STORE accepted with no error.
- [ ] B12 Reference/Response: config doc for hospital integration (AE title
         sheet, ports 11110-11118, auth policy).  AC: doc exists in repo.

## C) Always-commandable proof of base state
```
HEAD/clean tree        = 1237646fc3   (split merged & pushed; local == origin/master)
dotnet build           = 0 errors / 0 warnings
dotnet test (Release)  = 112 passed / 0 failed  (net8.0, real-SCU drills)
```
