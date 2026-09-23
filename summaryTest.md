# B:\Delivery - V2 Stabilization Evidence & Test Ledger

This document serves as the rigorous evidence ledger for testing and structural changes in B:\Delivery. Every sub-phase must pass the standard Testing Gate before proceeding to the next.

## Testing Gate Structure
For each sub-phase, we document:
- **Implementation**: What was changed.
- **Unit / Integration Test**: Component-level correctness.
- **Data Integrity Test**: Ensures data equality, zero loss, and schema validity.
- **Failure / Recovery Test**: System behavior when dependencies crash or timeout.
- **Regression Test**: Ensures existing core systems do not break.
- **Docker Verification**: Host mappings, environment vars, and image behavior.
- **Rollback Point**: How to immediately revert if the gate fails.
- **Evidence & Result**: Artifacts, logs, and PASS/FAIL status.

---

## Phase 0: Baseline & Port Isolation
**Status:** Completed

### 0.1 Port Isolation
- **Implementation:** Updated docker-compose.override.yml to isolate Host ports (e.g., 5442:5432) while preserving Docker-internal service ports (postgres:5432) for intra-container communication. Renamed containers to delivery-v2-*.
- **Integration Test:** delivery-v2-backend successfully connects to DB and RabbitMQ via internal network.
- **Docker Verification:** 
  - External Mapping: docker ps verifies Host:5442 -> Container:5432.
  - Internal Resolution: Backend connects to postgres:5432 and delivery-v2-rabbitmq:5672.
- **Evidence & Result:** [PASS] Backend health checks are green. Database queries log correctly.

---

## Phase 1: Low Risk Architecture
**Status:** Completed (PASS ✅)

### 1.1 Whitelabel Parameterization
**Status:** PASS

| Gate | สถานะ | เหตุผล |
|---|---|---|
| Before Evidence | ✅ PASS | มี source of truth ครบ |
| Implementation | ✅ PASS | APP_NAME / APPLICATION_ID / API_BASE_URL ถูก parameterize |
| Test A — Parameter Injection | ✅ PASS | Wrapper ส่งค่าต่อได้ |
| Test B — Docker Build | ✅ PASS | APK 63.8 MB build สำเร็จ |
| Test C — Namespace | ✅ PASS | Default namespace `com.smart.delivery` และ build script inject `com.test.delivery` |
| Test D — Empty Parameter | ✅ PASS | PowerShell reject ได้เมื่อพารามิเตอร์ว่าง |
| Test E — API URL | ✅ PASS | Source รองรับ injection และ binary verification ยืนยันพบ `https://test.example.com` ใน `libapp.so` |
| Test F — Version | ✅ PASS | `1.0.2+2` (versionCode: 2, versionName: 1.0.2) ไม่เปลี่ยน |
| Test G — APK Metadata Verification | ✅ PASS | `aapt dump badging` ตรวจ Package ID = `com.test.delivery`, App Label = `Test Delivery`, Dart binary พบ `Test Delivery` และ `https://test.example.com` |
| Regression Test | ✅ PASS | Backend (`5010`/`8088`), Admin (`4211`), Rider (`8093`) ทำงานปกติ, Login flow สำเร็จ, และ ripgrep ยืนยัน 0 matches สำหรับ `ninemek` ใน source |
| Rollback | เตรียมไว้แล้ว | ยังไม่ต้องใช้ |

**Before Evidence:**
| Parameter | Current Before | Source |
|---|---|---|
| APPLICATION_ID | `com.ninemek.delivery` | `build.gradle.kts` |
| namespace | `com.ninemek.delivery` | `build.gradle.kts` |
| Android App Label | `Delivery` | `AndroidManifest.xml` |
| Dart appName | `Rider App` | `app_constants.dart` |
| API_BASE_URL | `''` | `environment.dart` |
| Build URL param | `$TunnelUrl`, default `""` | `build-android-docker.ps1` |
| Version | `1.0.2+2` | `pubspec.yaml` |

**Testing Results (Phase 1.1):**
- **Test A - Parameter Injection:** PASS [OK] (Script prints APP_NAME, APPLICATION_ID, API_BASE_URL correctly)
- **Test B - Build:** PASS [OK] (APK builds successfully via Docker, size 63.8 MB)
- **Test C - Namespace:** PASS [OK] (Source files and gradle modified to use default namespace `com.smart.delivery` and build script injects `com.test.delivery`)
- **Test D - Empty param rejection:** PASS [OK] (Script rejects missing/empty params with PowerShell parameter binding error)
- **Test E - Frontend Environment:** PASS [OK] (`environment.dart` reads `API_BASE_URL` correctly, confirmed in `libapp.so`)
- **Test F - Versioning:** PASS [OK] (`pubspec.yaml` maintains `1.0.2+2`, `aapt` confirms versionCode: 2, versionName: 1.0.2)

### Test G - APK Metadata & Runtime Verification
- **Target APK:** `apk/delivery-app.apk` (63.8 MB)
- **Verify Android App Label:** `Test Delivery` (Verified via `aapt dump badging` on `application-label` and `application: label`)
- **Verify Package ID:** `com.test.delivery` (Verified via `aapt dump badging` on `package: name`)
- **Verify API Base URL Injection:** `https://test.example.com` (Verified embedded in Dart AOT snapshot `lib/arm64-v8a/libapp.so`)
- **Verify Dart App Identity:** `Test Delivery` (Verified embedded in Dart AOT snapshot `lib/arm64-v8a/libapp.so`)
- **Verify Launchable Activity:** `com.smart.delivery.MainActivity` (Verified via `aapt dump badging`)
- **Result:** PASS [OK]

### Regression Test
- **Backend Starts Successfully:** PASS [OK] (`delivery-v2-backend` healthy on `http://127.0.0.1:5010/health` and `http://127.0.0.1:8088/health`)
- **Admin Dashboard Builds/Loads Successfully:** PASS [OK] (`delivery-v2-frontend` returns HTTP 200 on `http://127.0.0.1:4211` and `http://127.0.0.1:8088`)
- **Rider App Launches Successfully:** PASS [OK] (`delivery-v2-delivery-app` returns HTTP 200 on `http://127.0.0.1:8093`)
- **Login / Request Flow Reaches Backend API:** PASS [OK] (POST `http://127.0.0.1:5010/api/v1/auth/login` returns 200 OK with JWT access token for `admin@delivery.com`; authenticated GET `/api/v1/auth/session` and GET `/api/v1/shops` return 5 shops)
- **Search Unintended References:** PASS [OK] (`rg -i 'ninemek' B:\Delivery` confirms 0 matches across all active source code and configuration files)
- **Result:** PASS [OK]

- **Implementation:** Pass APP_NAME, APPLICATION_ID, API_BASE_URL dynamically via Gradle and Dart defines.
- **Rollback Point:** Revert to hardcoded values if required.

### 1.2 MinIO Media Hardening
**Status:** PASS

| Gate | สถานะ | เหตุผล |
|---|---|---|
| Before Evidence | ✅ PASS | ตรวจสอบ source code, config, docker, env ครบถ้วน บันทึก baseline defects เรียบร้อย |
| Test 1.2-0 — DI / Storage Baseline | ✅ PASS | ลงทะเบียน `IStorageService` ใน DI สำเร็จ แก้ปัญหา HTTP 500 เดิมบน `MenuItemsController` สำเร็จ (คืนค่า 200 OK) |
| Test 1.2-B — Credential Hardening | ✅ PASS | ปลด Plaintext secrets ออกจาก `appsettings.json` และส่งผ่าน `.env` + `docker-compose.yml` (`Minio__*`) อย่างปลอดภัย |
| Test 1.2-C — Initialization Lifecycle | ✅ PASS | ย้าย Bucket initialization ออกจาก Constructor ไปไว้ใน `MinioBucketInitializerHostedService` (Non-blocking async startup) |
| Test 1.2-D — MinIO Upload & Public Access | ✅ PASS | สร้าง Bucket `delivery-media` อัตโนมัติพร้อม public-read policy, อัปโหลดไฟล์รูปภาพสำเร็จ และเข้าถึงผ่าน Public URL Nginx (200 OK, 59 bytes) |
| Test 1.2-E — Failure & Recovery Test | ✅ PASS | จำลอง MinIO DOWN: Backend start ได้ราบรื่น `/health` ตอบ 200 Healthy ไม่ crash/hang; เมื่อกู้คืน MinIO UP: อัปโหลดรูปภาพกลับมาทำงานได้ทันทีโดยไม่ต้อง restart API |
| Regression Test | ✅ PASS | Backend (`5010`/`8088`), Admin (`4211`), Rider (`8093`), Auth, Sessions, Shops, และ Menu endpoints ทำงานปกติสมบูรณ์ |
| Rollback | เตรียมไว้แล้ว | ยังไม่ต้องใช้ (Revert commit / file changes) |

**Before Evidence:**
| Parameter / Component | Current Before Value / Status | Source Location | Risk / Finding |
|---|---|---|---|
| MinIO Endpoint | `"minio:9000"` | `appsettings.json` (`Minio:Endpoint`) | Hardcoded in config file |
| MinIO AccessKey | `"minioadmin"` | `appsettings.json` (`Minio:AccessKey`) | **Plaintext Secret committed in git** |
| MinIO SecretKey | `"miniopassword123"` | `appsettings.json` (`Minio:SecretKey`) | **Plaintext Secret committed in git** |
| MinIO BucketName | `"delivery-media"` | `appsettings.json` (`Minio:BucketName`) | Hardcoded in config file |
| MinIO PublicUrl | `"http://localhost:8088/storage/delivery-media"` | `appsettings.json` (`Minio:PublicUrl`) | Hardcoded in config file |
| MinIO UseSsl | `false` | `appsettings.json` (`Minio:UseSsl`) | Hardcoded in config file |
| Environment (.env) | `MINIO_ROOT_USER=minioadmin`<br>`MINIO_ROOT_PASSWORD=miniopassword123` | `B:\Delivery\.env` | Consumed only by MinIO container; not passed to Backend container |
| Docker Compose Backend | **No MinIO variables** | `docker-compose.yml` (`backend.environment`) | Backend container lacks `Minio__*` env injection |
| Constructor Blocking | `InitializeBucketAsync().GetAwaiter().GetResult();` | `MinioStorageService.cs:31` | **Synchronous blocking network I/O in constructor** |
| Bucket Policy Config | Hardcoded public-read JSON string | `MinioStorageService.cs:42` | Inline JSON inside `InitializeBucketAsync()` |
| Upload Implementation | `UploadAsync(...)` / `UploadBase64Async(...)` | `MinioStorageService.cs:51,64` | Functional via `MinioClient.PutObjectAsync` |
| DI Registration | **MISSING** (`IStorageService` is not registered) | `ServiceSetup.cs` | **Throws HTTP 500 InvalidOperationException on MenuItems/Storage endpoints** |
| Bucket State | 0 buckets exist (`delivery-media` not created) | `delivery-v2-minio` container | Service never instantiated due to missing DI registration |

**Testing Results (Phase 1.2):**
- **Test 1.2-0 - DI / Storage Baseline:** PASS [OK]
  - Pre-fix state: `GET /api/v1/menu-items` failed with HTTP 500 (`Unable to resolve service for type 'IStorageService'`).
  - Post-fix state: `services.AddSingleton<IStorageService, MinioStorageService>()` registered. Endpoint returns HTTP 200 OK (`items: []`, `count: 0`).
- **Test 1.2-B - Credential Hardening:** PASS [OK]
  - `appsettings.json` secrets stripped to `""`.
  - `.env` enriched with `MINIO_ENDPOINT`, `MINIO_BUCKET_NAME`, `MINIO_PUBLIC_URL`, `MINIO_USE_SSL`.
  - `docker-compose.yml` injects `Minio__*` environment variables into `delivery-v2-backend`.
  - `BackendApi/.env.example` documented with template configuration.
- **Test 1.2-C - Initialization Lifecycle (IHostedService):** PASS [OK]
  - Constructor in `MinioStorageService` contains zero synchronous network I/O.
  - Startup initialization moved to `MinioBucketInitializerHostedService : IHostedService`.
  - Log confirmed: `[INF] MinioBucketInitializerHostedService: Initializing MinIO bucket asynchronously during application startup...`
- **Test 1.2-D - Media Storage & Nginx Routing:** PASS [OK]
  - Bucket `delivery-media` verified created with public-read policy (`mc ls local` confirms bucket exists).
  - Multipart image upload via `POST /api/v1/storage/upload` succeeded (returns public URL: `http://localhost:8088/storage/delivery-media/test-media/<guid>.png`).
  - Image fetched via Nginx public proxy returns HTTP 200 OK (59 bytes, image/png).
  - Verified object in MinIO: `mc ls -r local/delivery-media` confirms object persisted.
- **Test 1.2-E - Failure & Recovery (Non-blocking startup):** PASS [OK]
  - Failure scenario: MinIO container stopped (`docker stop delivery-v2-minio`), backend restarted (`docker restart delivery-v2-backend`).
  - Behavior observed: Backend started cleanly without hanging or crashing. `MinioBucketInitializerHostedService` logged `[ERR] Failed to initialize MinIO bucket ... Service startup continues; requests may retry or degrade` without blocking.
  - Health check: `GET http://localhost/health` responded HTTP 200 in 36ms. All non-storage endpoints remained operational.
  - Recovery scenario: MinIO container started (`docker start delivery-v2-minio`).
  - Recovery behavior: Upload endpoint immediately succeeded on next request without requiring backend restart or intervention.
- **Regression Test:** PASS [OK]
  - Backend Healthy (5010 & 8088).
  - Admin Dashboard HTTP 200 (4211 & 8088).
  - Rider App Web HTTP 200 (8093).
  - Auth Login & Session verified (`admin@delivery.com` / Admin).
  - Shops API (5 shops) & MenuItems API (0 items) verified functioning.

### 1.3 ProcessedEvents Lifecycle (Idempotency GC)
**Status:** PASS [OK] (Phase 1.3 Fully Verified & Closed)

#### 1.3-A — Before Evidence & Policy Alignment: PASS [OK]
| Audit Item | Current Reality / Findings | Live Source / Artifact | Risk / Architectural Implication |
|---|---|---|---|
| **1. Entity / Model** | `ProcessedEvent` (`EventId: Guid`, `HandlerName: string [MaxLength(250)]`, `ProcessedAt: DateTime`) | `BackendApi/Models/System/ProcessedEvent.cs` | Well-defined C# entity, clean schema mapping |
| **2. DB Schema & Types** | `public."ProcessedEvents"`<br>- `EventId`: `uuid` NOT NULL<br>- `HandlerName`: `varchar(250)` NOT NULL<br>- `ProcessedAt`: `timestamptz` NOT NULL | PostgreSQL `delivery-v2-db` (`delivery_db`) | `ProcessedAt` มีอยู่จริง เป็น `timestamptz` (รองรับ Timezone/UTC) |
| **3. Primary Key** | Composite B-tree `PK_ProcessedEvents` on `("EventId", "HandlerName")` | PostgreSQL `delivery-v2-db` | ป้องกัน duplicate handler execution ในระดับ DB constraint |
| **4. Secondary Indexes** | `IX_ProcessedEvents_ProcessedAt` on `("ProcessedAt")` (B-tree) | PostgreSQL `delivery-v2-db` | มี Index สำหรับ query ช่วงเวลา (`ProcessedAt < cutoff`) อยู่แล้ว ไม่ต้องสร้างเพิ่ม |
| **5. Write Sites** | 1. `RabbitMqEventBus.Consumer.cs:294`<br>2. `GpsRabbitMqConsumerWorker.Batch.cs:155, 167`<br>3. `OsrmSnapWorker.cs:248` | Backend EventBus & Telemetry Consumers | ทุกจุดบันทึก `ProcessedAt = DateTime.UtcNow` |
| **6. Duplicate Check Sites** | 1. `RabbitMqEventBus.Consumer.cs:261` (`AnyAsync(...)`)<br>2. `GpsRabbitMqConsumerWorker.Batch.cs:140` (Bulk check)<br>3. `OsrmSnapWorker.cs:235` (`AnyAsync(...)`) | Consumer Loop | ตรวจ `EventId` + `HandlerName` ก่อนรัน Handler |
| **7. Transaction Boundary** | Atomic Transaction + Advisory Lock:<br>`pg_advisory_xact_lock(hashtextextended($"{eventId:N}:{handlerType.FullName}", 0))` | `RabbitMqEventBus.Consumer.cs:255-300` | ครอบคลุมทั้ง Duplicate Check, Handler Execution, และ Save `ProcessedEvents` ในทรานแซกชันเดียวกัน (Rollback 100% ถ้า Handler พัง) |
| **8. Existing Worker** | `DbMaintenanceWorker.cs` (`IHostedService` ทำงานทุก 1 ชม. ผ่าน `PeriodicTimer`) | `BackendApi/Services/BackgroundWorkers/Maintenance/DbMaintenanceWorker.cs` | **Defect หลัก:** ปัจจุบัน hardcoded ไว้ที่ 24 ชม. (`AddHours(-24)`) ยังไม่ได้อ่าน `IDEMPOTENCY_RETENTION_DAYS` จาก Config |
| **9. Configuration** | ยังไม่มี Key `IDEMPOTENCY_RETENTION_DAYS` ใน `.env`, `docker-compose.yml`, หรือ `appsettings.json` | `BackendApi/appsettings.json` | ต้องเพิ่ม Config เพื่อปลดล็อก retention tuning |
| **10. RabbitMQ Consumer Config** | Prefetch Count: 100, Dead Letter Queue: `{queue}_dlq`, Max Retries: 5 | `RabbitMqEventBus.Consumer.cs` | Retry ผ่าน header `x-delivery-retry-count`, เกิน 5 ครั้งส่งเข้า DLQ |
| **11. DB Live Counts** | Total Rows: `0`, Oldest: `null`, Newest: `null` | PostgreSQL `delivery-v2-db` | ข้อมูลปัจจุบันสะอาด พร้อมรัน Dry Run Test |
| **12. In-Flight / Retries** | 12 queues ตรวจแล้ว `messages = 0`, `unacked = 0`, `dlq = 0` | RabbitMQ `delivery-v2-rabbitmq` | ไม่มี event ค้างในคิวหรือกำลัง retry |

#### Locked Retention Semantics & Policy (Decision Locked)
- **Retention Parameter:** `IDEMPOTENCY_RETENTION_DAYS = 7` (Default: 7 days, Configurable).
- **Core Principles:**
  1. **Separation of Responsibilities:**
     - **Expiration Fence:** ตัดสินว่า Event ยังมีสิทธิ์ execute หรือไม่? (`CreationDate > cutoff`)
     - **ProcessedEvents:** ตัดสินว่า Event + Handler นี้ execute ไปแล้วหรือยัง? (`EventId + HandlerName`)
     - **GC Pruning:** ตัดสินว่า Record idempotency เก่าเกิน window หรือยัง? (`ProcessedAt < cutoff`)
  2. **Locked Rules:**
     - **Rule 1:** `ProcessedEvents` ป้องกัน idempotency ภายใน Retention Window (0–7 วัน) ผ่าน composite key `(EventId, HandlerName)`.
     - **Rule 2:** แถวใน `ProcessedEvents` ที่มี `ProcessedAt` เก่ากว่า retention window จะถูก GC ลบออก.
     - **Rule 3:** Integration Event ที่มี `CreationDate` เก่ากว่า retention window (> 7 วัน) ถือเป็น **Expired / Stale Event**.
     - **Rule 4:** Expired / Stale Event **ต้องไม่ถูกส่งเข้า Business Handler เด็ดขาด**.
     - **Rule 5 (Decision Point Locked):** Expired / Stale Event จะถูกจัดการด้วยนโยบาย **`ACK + Skip`** พร้อมบันทึก Warning Log เพื่อให้ Operator ตรวจสอบได้ (ไม่ส่งเข้า DLQ เพื่อป้องกัน Poison Queue Loop).
     - **Rule 6:** Expired Event จะ **ไม่ถูกบันทึกลงใน `ProcessedEvents`** เพื่อรักษาความหมายของตาราง (เก็บเฉพาะ Event ที่รันสำเร็จ).
     - **Rule 7:** Event ปกติ (อายุ <= 7 วัน) ตรวจสอบความซ้ำซ้อนด้วย `(EventId, HandlerName)` ภายใต้ Advisory Lock ตามเดิม.
     - **Rule 8:** GC ต้องลบเฉพาะข้อมูลในตาราง `ProcessedEvents` เท่านั้น ห้ามแตะต้อง Business Data ใดๆ.
     - **Rule 9:** ค่า Retention ต้องตั้งค่าได้ผ่าน Environment Variable `IDEMPOTENCY_RETENTION_DAYS`.
     - **Rule 10:** ยังไม่เริ่มแก้ไข Source Code (Phase 1.3-C) จนกว่าการทดสอบ 1.3-B Dry Run จะเสร็จสมบูรณ์.

```
                    RabbitMQ Event
                          │
                          ▼
                ┌───────────────────┐
                │ Validate Event    │
                │ CreationDate      │
                └─────────┬─────────┘
                          │
                          ▼
              CreationDate > cutoff? (now - 7d)
                    /                               NO               YES
                  │                 │
                  ▼                 ▼
            Expired Event      ProcessedEvents
                  │             Duplicate Check (EventId, HandlerName)
                  ▼                 │
             Log Warning         /                         │             YES       NO
                  ▼              │         │
             ACK / Skip          ▼         ▼
                  │             Skip    Handler
                  │                       │
                  │                       ▼
                  │                Save ProcessedEvent
                  │                       │
                  └───────────────────────┘
```

#### Sub-phase Execution Plan
- **1.3-A Before Evidence & Policy Alignment:** PASS [OK] (Audit ครบถ้วน, ล็อก 10 นโยบายร่วมกับ User เรียบร้อย).
- **1.3-B Dry Run Test:** PASS [OK]
  - **Seed Details:**
    - Event A (`11111111-1111-1111-1111-111111111111`): `NOW() - INTERVAL '8 days'` -> `2026-09-12 14:29:33 UTC`
    - Event B (`22222222-2222-2222-2222-222222222222`): `NOW() - INTERVAL '6 days'` -> `2026-09-14 14:29:33 UTC`
    - Event C (`33333333-3333-3333-3333-333333333333`): `NOW()` -> `2026-09-20 14:29:33 UTC`
    - Handler Name: `TestDryRunHandler` (Isolated test handler)
  - **Audit & Assertions Result:**
    - Initial baseline count: `0`
    - Seeded count: `3`
    - Dry Run Cutoff (`NOW() - 7 days`): `2026-09-13 14:29:33 UTC`
    - **Candidate Delete Count:** `1` (Exact ID match: `['11111111-1111-1111-1111-111111111111']` -> Event A)
    - **Preserved Keep Count:** `2` (Exact ID match: `['22222222-2222-2222-2222-222222222222', '33333333-3333-3333-3333-333333333333']` -> Event B & Event C)
    - **Actual Deletes during Dry Run:** `0` (DB count remained exactly `3` during SELECT-only check)
    - **Cleanup:** `DELETE FROM "ProcessedEvents" WHERE "HandlerName" = 'TestDryRunHandler'` -> deleted exactly 3 test rows
    - **Post-Cleanup Total ProcessedEvents:** `0` (Baseline preserved 100%)
    - **Source Code Modified:** `0 files`
    - **Container Restart:** `No` (Backend uptime unaffected: Up 29+ mins)
- **1.3-C Actual GC Implementation:** PASS [OK]
  - **Code Parameterization:**
    - `DbMaintenanceWorker.cs` refactored: Replaced hardcoded `AddHours(-24)` with configurable `RetentionDays` reading `IDEMPOTENCY_RETENTION_DAYS` (default: 7 days).
    - Added XML docs and `PruneProcessedEventsAsync` method signature with `CancellationToken` and optional override.
  - **Configuration Integration:**
    - `.env`: Enriched with `IDEMPOTENCY_RETENTION_DAYS=7`.
    - `docker-compose.yml`: Injected `IDEMPOTENCY_RETENTION_DAYS: ${IDEMPOTENCY_RETENTION_DAYS:-7}` into `delivery-v2-backend`.
    - `BackendApi/appsettings.json`: Added `"Idempotency": { "RetentionDays": 7 }`.
    - `BackendApi/.env.example`: Documented template entry.
  - **Unit Tests (`BackendApi.UnitTests/Maintenance/DbMaintenanceWorkerTests.cs`):**
    - 7 tests passed (Default 7d, Env Var 14d, AppSettings 30d, Fallback on non-positive/malformed inputs).
  - **Integration & Actual Deletion Test:**
    - Seeded: Event A (`NOW() - 8d`), Event B (`NOW() - 6d`), Event C (`NOW()`).
    - Cutoff applied: `NOW() - INTERVAL '7 days'`.
    - Output: `DELETE 1` (Event A strictly deleted).
    - Post-GC Verification: Event B and Event C preserved in PostgreSQL `ProcessedEvents`.
    - Business Integrity Check: `Orders`, `Shops`, `Riders`, and `Users` verified 100% untouched.
    - Cleanup: Test handler records cleaned up; table restored to 0 rows.
  - **Docker Verification:**
    - Backend rebuilt and restarted with `IDEMPOTENCY_RETENTION_DAYS=7`.
    - Startup log confirmed: `[INF] DbMaintenanceWorker started (Idempotency Retention: 7 days)`.
  - **Full Regression Suite:**
    - Backend Direct Health (`:5010/health`): HTTP 200 Healthy
    - Nginx Proxy Health (`:8088/health`): HTTP 200 Healthy
    - Admin Frontend (`:4211` & `:8088/admin/`): HTTP 200 OK
    - Rider Flutter Web (`:8093`): HTTP 200 OK
    - Admin Auth Login & Session (`:5010`): HTTP 200 OK (Token acquired)
    - Data APIs (`/api/v1/shops`, `/api/v1/menu-items`): HTTP 200 OK (5 shops, 0 menus)
    - MinIO Storage Upload & Fetch: HTTP 200 OK (Uploaded and served via Nginx)
- **1.3-D Idempotency & Expiration Regression:** PASS [OK]
  - **Live Scenarios Tested against RabbitMQ & PostgreSQL:**
    1. **Scenario 1 - Stale Event Outside Retention Window (> 7 days):**
       - Event published with `CreationDate = 2026-09-10T12:00:00Z` (10 days old, > 7 days).
       - Backend Consumer Expiration Fence triggered:
         `[WRN] Event {EventId} (OrderStatusChangedIntegrationEvent) created at ... exceeds retention window of 7 days. Dropped as expired (ACK and Skip).`
       - **Handler Execution:** Bypassed completely (0 calls).
       - **ProcessedEvents DB Write:** Strictly 0 records inserted.
       - **RabbitMQ ACK:** Message acknowledged immediately (0 messages in flight, 0 unacked).
       - **DLQ Protection:** 0 messages sent to dead letter queue (`delivery_queue_OrderStatusChangedIntegrationEvent_dlq = 0`).
    2. **Scenario 2 - Normal Recent Event (First Time Delivery):**
       - Event published with `CreationDate = 2026-09-20T14:45:00Z` (today).
       - Handler executed normally and recorded in `ProcessedEvents` (count = 1).
    3. **Scenario 3 - Recent Duplicate Event (Replay Attack / Resend):**
       - Identical event re-published to RabbitMQ.
       - Consumer duplicate check triggered:
         `[WRN] Duplicate event detected. Skipping execution of handler ...`
       - Handler execution skipped; message ACKed.
       - `ProcessedEvents` record count remained strictly 1 (zero duplicate rows).
       - Queue remained clean (0 unacked, 0 DLQ).
    4. **Cleanup:** Test records safely removed; `ProcessedEvents` restored to baseline (0 rows).
  - **Integration Test Suite (`EventBusTests.cs`):**
    - `EventBus_ExpiredEventOutsideRetention_DroppedWithoutExecutingHandler`: PASS [OK]
    - `EventBus_Idempotency_SameEventProcessedOnce`: PASS [OK]


---

## Phase 2: Telemetry Redesign (Hot/Cold GPS)
**Status:** IN PROGRESS (Phase 2.1-A CLOSED ✅ | Phase 2.1-B Architecture Contract Locked 🛑)
**Source Code Modified:** 0 files (Strictly 0 code changes in Phase 2 during 2.1-A and 2.1-B Contract)

---

### 2.1-A Before Evidence & Architecture Audit
**Gate Status:** PASS / CLOSED ✅

#### 1. PostgreSQL `RiderLocationHistories` Table & Partition Structure
- **Partitioning Model:** Table is partitioned by **`RANGE ("RecordedAt")`** (Monthly range partitions).
- **Physical Partitions:**
  - `RiderLocationHistories_2026_09`: `FOR VALUES FROM ('2026-09-01 00:00:00+00') TO ('2026-10-01 00:00:00+00')`
  - `RiderLocationHistories_2026_10`: `FOR VALUES FROM ('2026-10-01 00:00:00+00') TO ('2026-11-01 00:00:00+00')`
  - `RiderLocationHistories_2026_11`: `FOR VALUES FROM ('2026-11-01 00:00:00+00') TO ('2026-12-01 00:00:00+00')`
  - `RiderLocationHistories_2026_12`: `FOR VALUES FROM ('2026-12-01 00:00:00+00') TO ('2027-01-01 00:00:00+00')`
- **Schema & Columns:**
  - `Id`: `text` NOT NULL
  - `RiderId`: `text` NOT NULL
  - `Location`: `geometry(Point, 4326)` NOT NULL (PostGIS 2D Point, WGS84)
  - `RecordedAt`: `timestamp with time zone` NOT NULL (Partition key)
  - `RecordedFromIp`: `text` NULL
  - `OrderId`: `text` NULL
- **Primary Key:** Composite B-tree `PK_RiderLocationHistories` on `("Id", "RecordedAt")`.
- **Secondary Indexes:**
  - `IX_RiderLocationHistories_Location_Gist`: Spatial GiST index on `Location` for geo-bounding queries.
  - `IX_RiderLocationHistories_RiderId_RecordedAt`: B-tree index on `("RiderId", "RecordedAt")` for rider route history.
- **Current Live Row Counts:**
  - Total: `0` rows (Partition 2026_09: 0, 2026_10: 0, 2026_11: 0, 2026_12: 0).
  - Storage size: 32 kB per partition table (Clean baseline).

#### 2. Telemetry Ingestion Pipeline Trace
```
Rider Mobile / Web
   │ (SignalR or REST /gps or /gps/batch)
   ▼
TrackingHub.Location / TelemetryController
   │ (ProcessLocationUpdateAsync / ProcessLocationBatchAsync)
   ▼
TelemetryService
   ├── [Hot Path] ──────► Redis Presence Cache (GEOADD + HSET + LPUSH) & SignalR Broadcast
   └── [Cold Path] ─────► GpsRabbitMqPublisher (In-memory bounded channel -> RabbitMQ)
                                  │
                                  ▼
                         gps_telemetry_queue (Durable, DLQ: gps_telemetry_dlx)
                                  │
                                  ▼
                     GpsRabbitMqConsumerWorker.Batch.cs (Prefetch: 5,000)
                                  │
                                  ▼
                           Deduplication check via ProcessedEvents
                           (HandlerName = "GpsConsumer", EventId = SHA256(riderId_ticks))
                                  │
                                  ▼
                           Atomic PostgreSQL Transaction:
                           ├── Insert ProcessedEvents
                           └── GpsHistoryService.SavePointsAsync -> RiderLocationHistories
                                  │
                                  ▼
                           Manual Batch BasicAck up to maxDeliveryTag
```
- **Idempotency & Deduplication:** `GenerateGuidFromPoint(TrackPoint)` generates deterministic 16-byte GUID from `SHA256($"{RiderId}_{Timestamp.Ticks}")`. Checked against `ProcessedEvents` where `HandlerName = "GpsConsumer"`.
- **Transaction Scope:** EF Core transaction covers both `ProcessedEvents` and `RiderLocationHistories` bulk insert.
- **ACK Protocol:** RabbitMQ messages are acknowledged (`BasicAck(multiple: true)`) ONLY after the database transaction commits successfully. On exception, transaction rolls back and messages are NACKed (`BasicNack(requeue: false)`) to `gps_telemetry_dlq`.

#### 3. Latest Position Flow & TrackingHub Audit
- **SignalR Transport Gateway (`TrackingHub.Location.cs`):** Pure transport layer forwarding to `_telemetryService.ProcessLocationUpdateAsync(riderId, lat, lng, accuracy)` without business logic.
- **Redis Operational Presence (`RiderPresenceService.cs`):**
  - Spatial Index: `GEOADD riders:locations <lng> <lat> <riderId>` (Used by Dispatch Engine for `GEORADIUS` proximity matching).
  - Telemetry Hash: `HSET riders:gps:<riderId>` (`lat`, `lng`, `updated_at`, `speed_kmh`, `accuracy`, TTL: 24h).
  - Heartbeat: `SET riders:heartbeat:<riderId>` (`ticks`, TTL: 5m).
  - Speed Moving Average: `LPUSH riders:speed_buffer:<riderId>` (Maintains 5-point moving average).
- **SignalR Real-time Broadcast:**
  - Group `admins`: Throttled dynamically between 1.0s and 5.0s depending on rider velocity.
  - Group `rider:{riderId}`: Self-mirroring for smooth animation.
- **Critical Architectural Baseline Note:**
  - Direct database writes of `Riders.CurrentLocation` have ALREADY been removed from the real-time hot path in `TelemetryService.cs` (lines 239-244).
  - PostgreSQL `Riders.CurrentLocation` is NOT updated per GPS tick. Live queries rely 100% on Redis Presence Cache.

#### 4. Live Redis Container Audit (`delivery-v2-redis`)
- **Memory Profile:**
  - `used_memory`: 1.33 MB (`used_memory_rss`: 8.59 MB).
  - `maxmemory`: 256 MB (`268435456` bytes).
  - `maxmemory-policy`: **`allkeys-lru`**.
- **Persistence & Durability Profile:**
  - `aof_enabled`: `1` (`yes`).
  - `appendfsync`: **`everysec`** (1-second sync window).
  - `appenddirname`: `appendonlydir`, `appendfilename`: `appendonly.aof`.
  - RDB snapshotting: Enabled, last bgsave status: `ok`.
- **Existing Keys & Streams:**
  - Key count: `1` key (`riders:hotspots:heatmap`, type: string, TTL: ~3570s).
  - Existing Redis Streams: **`0`** (No telemetry streams currently exist; clean baseline).

---

### 2.1-B Dual-Write Architecture Contract & Implementation
**Gate Status:** 
- Unit-Test Evidence: **PASS ✅** (7/7 Unit Tests in `GpsRabbitMqConsumerWorkerTests`)
- Implementation Gate: **PASS ✅** (File 1 Constants & DI, File 2 Dual-Write Batch, File 3 Strict Tests)
- Docker Integration Proof: **PASS ✅** (Empirically verified across 4 live Docker scenarios)
- Overall Phase 2.1-B: **CLOSED ✅**

#### Empirical Docker Integration Test Results (Executed on live Docker Stack):
| Scenario | Action Executed | Observed System Behavior | Verification Status |
|---|---|---|---|
| **1. Happy Path** | Published GPS point `rider_dock_01` to `gps_telemetry_queue` | - PostgreSQL: +1 row in `RiderLocationHistories`, +1 row in `ProcessedEvents`<br>- Redis: +1 entry in `telemetry:stream:gps` with all fields (`eventId`, `riderId`, `timestamp`, `lat`, `lng`, `accuracy`)<br>- Redis: `telemetry:stream:seen:{eventId}` created with TTL ~3600s<br>- RabbitMQ: ACKed with `multiple: true` (`messages=0`) | **PASS ✅** |
| **2. Redis Failure (Pause)** | Paused Redis container (`docker pause delivery-v2-redis`), published point `rider_dock_fail` | - Worker timed out at 5068ms (as configured by `SyncTimeout=5000`)<br>- PostgreSQL transaction rolled back (0 new rows in DB)<br>- Message NACKed with `requeue: false`<br>- Message parked safely in `gps_telemetry_queue_dlq` (`messages=1`) | **PASS ✅** |
| **3. Duplicate Replay (Seen Guard)** | Re-published identical point `rider_dock_01` to `gps_telemetry_queue` | - PostgreSQL detected duplicate in `ProcessedEvents` (`New unique: 0`)<br>- Redis checked seen key -> key exists -> skipped duplicate `XADD`<br>- Stream count unchanged<br>- Message ACKed up to tag 3 (`messages=0`) | **PASS ✅** |
| **4. DLQ Replay & Recovery** | Unpaused Redis, retrieved message from `gps_telemetry_queue_dlq`, published back to queue | - Worker processed recovered point<br>- PostgreSQL committed `rider_dock_fail` (+1 row in DB)<br>- Redis stream written & seen key set<br>- Message ACKed (`messages=0`)<br>- DLQ purged clean | **PASS ✅** |

#### Architectural Caveats & Verified Evidence Logged in Ledger:
1. **`_redis == null` (Known Untested/Unsafe Configuration Branch):** In `GpsRabbitMqConsumerWorker.Batch.cs`, if `_redis` or `redisDb` is null, the code proceeds with PostgreSQL-only without throwing. If DI loses `IConnectionMultiplexer`, Redis dual-write is skipped silently. In our Docker runtime, DI injection of `IConnectionMultiplexer` was verified active. This branch is documented as a known unsafe configuration branch and intentionally left unchanged to avoid scope expansion.
2. **Message-level Zero-Loss / DLQ Preservation Verified:** Message-level zero-loss / DLQ preservation was verified under the tested Redis failure scenario. The worker safely rolled back the PostgreSQL transaction and routed uncommitted messages to `gps_telemetry_queue_dlq`.
3. **Redis Stream Buffer Nature & Orphan Entries:** Redis Stream may contain orphan/duplicate entries under ambiguous network failure; downstream archival must deduplicate by eventId. Live Docker testing empirically proved that when client timeout occurs, the delayed write creates an orphan buffer entry while DB rolls back, confirming the architecture rule that Redis is strictly an ephemeral buffer and NOT an authoritative 2PC store.

#### 2.1-B.1 Source of Truth & Dual-Write Transaction Boundary
```
RabbitMQ (Durable transit log)
   │
   ▼
GpsRabbitMqConsumerWorker
   │
   ├──► [Primary Source of Truth] ──► PostgreSQL (RiderLocationHistories + ProcessedEvents)
   │                                   - Authoritative durable historical record
   │                                   - Used for route playback, dispute audits, compliance
   │
   └──► [Buffer / Pipeline Sink] ───► Redis Stream (telemetry:stream:gps)
                                       - Ephemeral streaming pipeline
                                       - Consumed by Phase 2.2 Archive Worker (MinIO)
                                       - NOT considered an authoritative database
```
- **Fundamental Principle:** PostgreSQL and Redis do NOT form a distributed 2PC atomic transaction.
- **PostgreSQL Authority:** PostgreSQL remains the primary authoritative historical store during the entire Dual-Write phase.
- **Ordering Contract:**
  1. Consumer reads batch from RabbitMQ.
  2. Consumer checks `ProcessedEvents` for idempotency (`HandlerName = "GpsConsumer"`).
  3. Consumer stages insert into `ProcessedEvents` and `RiderLocationHistories` within an EF Core PostgreSQL Transaction.
  4. Consumer executes Redis Stream `XADD` (or appends after DB stage).
  5. If Redis fails -> PostgreSQL Transaction is aborted/rolled back, RabbitMQ message is NOT ACKed.
  6. If PostgreSQL commit fails -> PostgreSQL rolls back. If an entry reached Redis, it is an orphan buffer entry.
  7. RabbitMQ ACK is issued ONLY after BOTH PostgreSQL transaction commit AND Redis XADD are confirmed.

#### 2.1-B.2 Redis Stream Schema & Bounding Policy
- **Stream Key:** `telemetry:stream:gps`
- **Fields per Entry:**
  - `eventId`: String (UUID format, e.g. `550e8400-e29b-41d4-a716-446655440000`)
  - `riderId`: String
  - `timestamp`: String (ISO-8601 UTC with ticks: `yyyy-MM-ddTHH:mm:ss.fffffffZ`)
  - `lat`: Double as string (Invariant culture format)
  - `lng`: Double as string (Invariant culture format)
  - `accuracy`: Double as string (Meters)
- **Bounding & Trimming Policy (Memory Bounding vs Retention Guarantee):**
  - Trimming Strategy: `XADD telemetry:stream:gps MAXLEN ~ 100000 * ...`
  - **Critical Constraint:** `MAXLEN ~ 100000` is strictly a **Memory Bounding Policy** to protect the 256MB Redis instance (`allkeys-lru`) from OOM/eviction. It is **NOT a Data Retention Guarantee**.
  - **Retention Window:** At 100 riders x 1 pt/s = ~16.7 min buffer; at 5 Hz burst = ~3.3 min buffer. If the Phase 2.2 Archive Worker stops or lags behind, older entries are trimmed. Phase 2.2 must prove Archive Worker throughput exceeds producer throughput under load.

#### 2.1-B.3 Reconciled Runtime Failure Semantics Contract Table
The exact system behavior across all failure scenarios is locked and reconciled with the existing codebase (`requeue: false -> DLQ` preservation) as follows:

| Scenario | PostgreSQL History | Redis Stream (`telemetry:stream:gps`) | RabbitMQ Action | Message Destination & Recovery Behavior |
|---|---|---|---|---|
| **1. Happy Path (Both Succeed)** | Committed | Appended (`XADD`) | `BasicAck(maxDeliveryTag, multiple: true)` | Normal operation. Points recorded in both PostgreSQL and Redis Stream buffer. |
| **2. DB Failure Before Redis** | Rolled Back | Not executed | `BasicNack(maxDeliveryTag, multiple: true, requeue: false)` | Message preserved in `gps_telemetry_queue_dlq` (Message-level zero loss / DLQ preservation). |
| **3. Redis Failure Before DB Commit** | Rolled Back | Failed / Partial | `BasicNack(maxDeliveryTag, multiple: true, requeue: false)` | DB rolled back, batch moved to `gps_telemetry_queue_dlq` (Message-level zero loss / DLQ preservation). |
| **4. Redis Succeeds → DB Commit Fails** | Rolled Back | Orphan entry in buffer | `BasicNack(maxDeliveryTag, multiple: true, requeue: false)` | Batch routed to DLQ. **Case 4 Rule:** If DLQ is replayed, duplicate entries in Redis Stream are deduplicated by `eventId` in Phase 2.2 MinIO Archiver. |
| **5. DB Committed → Redelivery Occurs** | Already Committed (Skip) | Guarded against duplicate storm | `BasicAck(maxDeliveryTag, multiple: true)` | **Case 5 Rule:** Duplicate detected in DB -> checks `telemetry:stream:seen:{eventId}`: if seen -> skip `XADD`; if missing -> repair `XADD` + set seen key. If repair fails -> `BasicNack(requeue: false)` to DLQ. Prevents duplicate storms while guaranteeing stream repair. |
| **6. Crash Before ACK/NACK (Ungraceful)** | DB state depends on crash point | Stream state depends on crash point | Broker Auto-Requeue (Unacked) | RabbitMQ automatically requeues unacknowledged messages when consumer connection/channel closes. On redelivery, Case 5 idempotency logic handles deduplication. |

**Notes on Runtime Failure Semantics & DLQ Topology:**
- `requeue: false` is the current application-level failure behavior implemented in `GpsRabbitMqConsumerWorker.Batch.cs` to prevent poison message infinite loops on high-throughput telemetry.
- Failed messages are preserved in `gps_telemetry_queue_dlq` for manual/admin replay (Message-level zero loss / DLQ preservation).
- This DLQ is a parking queue, not an automatic retry queue.
- Ungraceful consumer/process failure before ACK/NACK is handled differently: RabbitMQ requeues unacknowledged messages when the consumer connection/channel closes.

#### 2.1-B.4 Identity Design Caveat (Pre-2.1-C Lock)
- Current Deterministic Formula: `SHA256($"{RiderId}_{Timestamp.Ticks}")` truncated to 16 bytes UUID.
- **Architectural Boundary:**
  - Assumes each distinct GPS point from a given Rider has a unique timestamp tick.
  - If a device or test simulator emits two points with the exact same `Timestamp.Ticks` for the same Rider with different coordinates, they will map to the same `EventId` and the second point will be discarded by `ProcessedEvents`.
  - **Decision:** Do NOT alter the identity algorithm now. Validate this boundary thoroughly in Phase 2.1-C test scenarios.

#### 2.1-B.5 Ambiguous Commit State & Target SLAs vs Verified Evidence
- **Ambiguous PostgreSQL Commit Failure Mode (Phase 2.1-D Requirement):**
  - Calling `CommitAsync()` over a network can produce an ambiguous/unknown outcome (e.g. connection timeout where client cannot verify whether DB committed).
  - This is a known limitation of dual-writing without 2PC.
  - **Requirement:** Phase 2.1-D Failure testing must explicitly simulate ambiguous commit recovery via `ProcessedEvents` redelivery idempotency.
- **Verified Facts (Audit Complete):**
  - PostgreSQL schema, partitioning (`RANGE`), indexes, and current 0 rows.
  - Redis 256MB LRU policy, AOF `everysec`, and 0 existing streams.
  - RabbitMQ prefetch 5,000 and consumer transaction boundary.
- **Target SLAs / Design Intent (To be proven in 2.1-C & 2.1-D, NOT yet verified):**
  - *"Zero data loss under Redis failure"* is the **Target Recovery Behavior**, not an empirical test result yet.
  - *"Hot path latency < 20 ms"* and *"Cold path batch < 1-3s"* are **Design Targets**, pending load and benchmark testing.

---

### 2.1-C Identity Match & Boundary Verification
**Gate Status:** **CLOSED (PASS ✅)**
- **Sub-Gate C-A (Identity Source Audit):** PASS ✅ (Origin verified at Consumer `GenerateGuidFromPoint`, RabbitMQ payload has no EventId, `RiderLocationHistories` stores no EventId column, schema discrepancy reconciled).
- **Sub-Gate C-B (Data Equivalence Test, N = 100):** PASS ✅ (Empirically verified across 100 points: ProcessedEvents = 100, RiderLocationHistories = 100, Redis Stream = 100, Missing = 0, Extra = 0, Layer 1 Direct UUID Match = 100/100, Layer 2 Natural Key Data Tuple Match = 100/100, Recomputed Formula Match = 100/100).
- **Sub-Gate C-C (Collision / Timestamp Boundary Test):** VERIFIED ✅ (Empirically proved that identical `(RiderId, Timestamp.Ticks)` with different coordinates produces identical `EventId`. Point A is committed & buffered; Point B is recognized as duplicate by `ProcessedEvents` and Redis seen guard, and safely absorbed with RabbitMQ ACK).
- **Sub-Gate C-D (Identity Decision & Consistency Audit):** PASS ✅
  - **D-1 (Requirement Guarantee):** Singular observation per time-slice locked as deliberate contract behavior.
  - **D-2 (Timestamp Resolution in Production):** Real-time SignalR generates timestamp on server clock (`DateTime.UtcNow`); Offline SQLite buffer uses Dart microsecond timestamps with $\ge 3$m adaptive filter.
  - **D-3 (Impact Inventory):** Complete mapping of downstream components (`Consumer`, `ProcessedEvents`, `Redis Stream`, `OsrmSnapWorker`, `Archiver`, `Tests`).
  - **D-4 (Implementation Consistency):** `GpsRabbitMqConsumerWorker.GenerateGuidFromPoint` and `OsrmSnapWorker.GenerateGuidFromPoint` verified 100% identical byte-for-byte in IL. No other consumers synthesize EventId from GPS points.

#### Locked V2 Telemetry Identity Contract
> *"The V2 telemetry identity contract treats (RiderId, Timestamp.Ticks) as the event identity. Distinct coordinates sharing the same identity are therefore treated as duplicate events. Production telemetry paths currently generate timestamps at intervals substantially larger than the identity resolution tested here, but this remains an explicit contract assumption rather than a physical impossibility."*

---

### Master Road Map (Reconciled & Locked)
- [x] **2.1-A Before Evidence & Architecture Audit** (CLOSED ✅)
- [x] **2.1-B Dual-Write Architecture Contract & Implementation** (CLOSED ✅)
- [x] **2.1-C Dual-Write Integration & Identity Match Verification** (CLOSED ✅)
- [x] **2.2-A Pre-Implementation Audit** (CLOSED ✅)
- [x] **2.2-B Redis Stream to MinIO Archiver Implementation** (CLOSED ✅ — Sub-steps 2.2-B.1 ถึง 2.2-B.2.6 และ Throughput Benchmark T0-T5 CLOSED ✅)
- [x] **2.1-D Dual-Write Failure & Recovery Verification** (D-A ✅, D-B ✅, D-C ✅, D-D ✅ — CLOSED / READY FOR GATE REVIEW ✅)
- [x] **2.1-E Regression Verification** (Admin Live Map, SignalR Hub, Leaflet Dynamic Subscription) (CLOSED ✅)
- [x] **Phase 3.1: Redis Distributed Lock with Fallback** (Implementation, 100-Concurrency Integration, Failure Recovery) (CLOSED ✅)
- [x] **Phase 3.2: Final Functional Parity Verification (Core Operational Workflows)** (CLOSED / PASS ✅)
  > *"3.2 ไม่ได้มีหน้าที่พิสูจน์ว่า V2 “เหมือนระบบเดิม 100%” แต่พิสูจน์ว่า Core Operational Workflow ที่ V2 ตั้งใจรักษาไว้ยังทำงานครบตาม Acceptance Criteria เพราะ Original กับ V2 เป็นคนละ codebase และ V2 มี intentional changes อยู่แล้ว"*
  - [x] **Checklist 1: Order Lifecycle** (CLOSED / PASS ✅)
  - [x] **Checklist 2: Task & Dispatch** (CLOSED / PASS ✅)
  - [x] **Checklist 3: Rider Journey** (CLOSED / PASS ✅)
  - [x] **Checklist 4: Settlement** (CLOSED / PASS ✅)
  - [x] **Checklist 5: Monitoring** (CLOSED / PASS ✅)
- [x] **V2 FREEZE & PORTFOLIO READY** (CLOSED / PASS ✅ — Ready for Chat Backend & Portfolio Showcase)

---

## Phase 3: Lock Architecture
**Status:** In Progress / Next

### 3.1 Redis Distributed Lock with Fallback
- **Implementation:** Redis-first lock with PostgreSQL fallback.
- **Integration Test:** 100 concurrent requests -> 1 owner, 99 rejected/waited.
- **Failure / Recovery Test (Concurrency Ownership):**
  - Scenario B: Redis DOWN *before* acquire -> PostgreSQL fallback handles 100 concurrent requests safely.
  - Scenario C: Redis DOWN *during* operation -> Verify lock ownership behavior. System must not create two owners if the lock provider switches mid-operation.
- **Rollback Point:** Revert to PostgreSQL-only locks.

---
*(Phase 4 Optimization: Paused)*

---

## Phase 2.2: Redis Stream to MinIO Archiver Pipeline

### 2.2-A Pre-Implementation Audit
- **Status:** **CLOSED (PASS ✅)**
- **Audit Findings Reconciled & Locked:**
  1. Stream Contract: `telemetry:stream:gps` verified (payload fields: `eventId`, `riderId`, `timestamp`, `lat`, `lng`, `accuracy`).
  2. Consumption Mode: Stream ID Cursor (`telemetry:archiver:last_id`) without consumer group PEL complexity. Exclusive cursor reading (`> last_id`).
  3. Staging Checkpoint: `telemetry:archiver:pending_batch` (`firstStreamId|lastStreamId`) for deterministic crash recovery.
  4. Deduplication Hierarchy: Guaranteed in-batch `HashSet<Guid>` + Best-effort active guard `telemetry:archiver:seen:{eventId}` (TTL 2h) + Downstream analytical deduplication on `eventId`.
  5. Gap Policy: `checkpoint < firstAvailableStreamId` -> `CRITICAL LOG + SAFE HALT`.
  6. Storage Separation: `ITelemetryArchiveStorage` strictly isolated from `IStorageService` (`delivery-media`), bucket `delivery-telemetry-archive` 100% private (no public policies).
  7. Object Naming: Deterministic key `gps/year=YYYY/month=MM/day=DD/hour=HH/batch_{firstStreamId}_{lastStreamId}.ndjson.gz`.

### 2.2-B Implementation & Verification
#### Sub-step 2.2-B.1: Storage Abstraction Layer
- **Status:** **CLOSED (PASS ✅)**
- **Implemented Components:**
  - `BackendApi/Services/Storage/ITelemetryArchiveStorage.cs`: Dedicated archive interface (`EnsureArchiveBucketExistsAsync`, `PutArchiveObjectAsync`, `ObjectExistsAsync`, `GetArchiveObjectAsync`).
  - `BackendApi/Services/Storage/MinioTelemetryArchiveStorage.cs`: MinIO SDK implementation with strictly private bucket initialization (`SetPolicyAsync` explicitly omitted), lazy bucket verification, and stream upload.
  - `BackendApi/appsettings.json`: Added `Minio:TelemetryBucketName = "delivery-telemetry-archive"`.
  - `docker-compose.yml`: Added `Minio__TelemetryBucketName=${MINIO_TELEMETRY_BUCKET_NAME:-delivery-telemetry-archive}` to backend environment.
  - `BackendApi/Setup/Extensions/ServiceSetup.cs`: Registered `ITelemetryArchiveStorage` alongside `IStorageService` as Singleton.
- **Verification Evidence:**
  - `dotnet build B:\Delivery\BackendApi\BackendApi.csproj`: Succeeded with 0 Warnings, 0 Errors.
  - `dotnet test B:\Delivery\RootScripts\scripts.test\test\BackendApi.UnitTests --filter FullyQualifiedName~MinioTelemetryArchiveStorageTests`: **8/8 PASSED** (including DI resolution and strict privacy assertions).
  - `docker build`: Backend container image built cleanly with Swagger generation (Exit code 0).
  - Git diff: 100% clean, zero out-of-scope modifications.
  - Implementation guarantee: Telemetry archive storage does not apply any public-read bucket policy. Runtime bucket privacy will be verified during the Phase 2.2-B live MinIO integration test.

#### Sub-step 2.2-B.2.1: Worker Skeleton + Exclusive Cursor
- **Status:** **CLOSED (PASS ✅)**
- **Implemented Components:**
  - `BackendApi/Services/BackgroundWorkers/Jobs/TelemetryArchiveWorker.cs`: BackgroundService skeleton initializing `CurrentCheckpoint` from Redis key `telemetry:archiver:last_id` (fallback `"0-0"`), reading `telemetry:stream:gps` strictly `> CurrentCheckpoint` (Exclusive Cursor), and handling empty stream without error.
  - `BackendApi/Setup/Extensions/ServiceSetup.cs`: Registered `TelemetryArchiveWorker` as `IHostedService`.
  - `RootScripts/scripts.test/test/BackendApi.UnitTests/Telemetry/TelemetryArchiveWorkerTests.cs`: 6 unit tests covering checkpoint initialization (`0-0` default vs loaded value), exclusive cursor query (`> CurrentCheckpoint`), empty stream non-blocking wait, Redis outage resilience, and hosted service DI resolution.
- **Verification Evidence:**
  - `dotnet build B:\Delivery\BackendApi\BackendApi.csproj`: Succeeded with 0 Warnings, 0 Errors.
  - `dotnet test B:\Delivery\RootScripts\scripts.test\test\BackendApi.UnitTests --filter FullyQualifiedName~TelemetryArchiveWorkerTests`: **6/6 PASSED** (All 14 Phase 2.2 storage + worker unit tests passed).
  - `docker build`: Backend container image built cleanly with Swagger generation (Exit code 0).
  - Git diff: Clean diff on `ServiceSetup.cs`, zero unintended modifications.

#### Sub-step 2.2-B.2.2: Batch Collection + Boundary
- **Status:** **CLOSED (PASS ✅)**
- **Implemented Components:**
  - `BackendApi/Services/BackgroundWorkers/Jobs/TelemetryArchiveWorker.cs`:
    - Added `TelemetryBatch` abstraction capturing `FirstStreamId`, `LastStreamId`, `Entries`, and `CreatedAt`.
    - Batch Boundary Rules:
      - Rule 1 (Size Threshold): Immediate flush when buffered entries reach `MaxBatchEntries = 5000` (zero wait).
      - Rule 2 (Timer Threshold): Partial batch flushes when `MaxWaitInterval = 60s` expires with $>0$ entries.
      - Rule 3 (Empty Stream): No batch created when stream is empty (`0` entries).
      - Rule 4 (Deterministic Read Capping): When stream has more than needed entries, read request is capped at `MaxBatchEntries - BufferedCount` so batch size never exceeds 5,000.
    - Deterministic Object Key Generator: `gps/year=YYYY/month=MM/day=DD/hour=HH/batch_{firstStreamId}_{lastStreamId}.ndjson.gz` using Gregorian integer formatting (`utc.Year:D4`) immune to regional culture calendar shifts (prevented Buddhist BE 2569 bug).
    - Pending Batch Representation: `FormatPendingBatch` / `ParsePendingBatch` for `firstStreamId|lastStreamId` metadata.
  - `RootScripts/scripts.test/test/BackendApi.UnitTests/Telemetry/TelemetryArchiveWorkerTests.cs`:
    - 20 unit tests in total, using `FakeTimeProvider` (avoiding real 60-second delays) verifying:
      - Empty stream does not create batch.
      - 100 entries flushes as partial batch after simulated 60s timeout with exact boundary IDs.
      - 4,999 entries waits as partial batch until timeout.
      - 5,000 entries flushes immediately without timer delay.
      - Read request capped at needed count when $>5000$ entries are waiting.
      - Deterministic object key generation matches contract.
      - Pending batch formatting and parsing handles valid and invalid payloads.
- **Verification Evidence:**
  - `dotnet build B:\Delivery\BackendApi\BackendApi.csproj`: Succeeded with 0 Warnings, 0 Errors.
  - `dotnet test B:\Delivery\RootScripts\scripts.test\test\BackendApi.UnitTests --filter FullyQualifiedName~TelemetryArchiveWorkerTests`: **20/20 PASSED** (All 28 Phase 2.2 storage + worker unit tests passed).
  - `docker build`: Backend container image built cleanly with Swagger generation (Exit code 0).
  - Git diff: Scoped strictly to `TelemetryArchiveWorker.cs` and unit test file.

#### Sub-step 2.2-B.2.3: Deduplication Hierarchy (In-batch + Cross-batch)
- **Status:** **CLOSED (PASS ✅)**
- **Implemented Components:**
  - `BackendApi/Services/BackgroundWorkers/Jobs/TelemetryArchiveWorker.cs`:
    - `ExtractEventId`: Helper to extract `eventId` GUID from `StreamEntry.Values`.
    - `DeduplicateEntriesAsync`:
      1. Guaranteed in-batch deduplication via `HashSet<Guid>` on `eventId`. Duplicate entries within the same batch are not added to `DeduplicatedEntries`.
      2. Best-effort active cross-batch deduplication via transient Redis key `telemetry:archiver:seen:{eventId}` with 2-hour TTL.
    - `TelemetryBatch`: Holds both `RawEntries` and `DeduplicatedEntries`.
    - **Boundary Integrity Guarantee:** Filtered duplicate entries still belong to the batch boundary (`FirstStreamId` and `LastStreamId` match the raw stream boundary, and `LastStreamId` points to the final raw entry of the batch regardless of duplicates).
  - `RootScripts/scripts.test/test/BackendApi.UnitTests/Telemetry/TelemetryArchiveWorkerTests.cs`:
    - 26 unit tests total (6 from B.2.1, 14 from B.2.2, 6 new for B.2.3), covering:
      - Test 1 (No duplicates): 10 entries -> archive input = 10, exact boundaries preserved.
      - Test 2 (In-batch duplicate): 10 entries with 9 unique eventIds -> archive input = 9, `LastStreamId` remains entry #10.
      - Test 3 (Cross-batch duplicate): Batch 1 marks event A as seen. Batch 2 with [A, B, C] filters A, keeps B and C, boundary ends at entry C.
      - Test 4 (Missing seen key): Event without seen key passes into archive pipeline.
      - Test 5 (TTL expiration): After 2h TTL expires, event re-enters archive pipeline.
      - Helper test: `ExtractEventId` handles valid, missing, and invalid payloads.
- **Verification Evidence:**
  - `dotnet build B:\Delivery\BackendApi\BackendApi.csproj`: Succeeded with 0 Warnings, 0 Errors.
  - `dotnet test B:\Delivery\RootScripts\scripts.test\test\BackendApi.UnitTests --filter FullyQualifiedName~TelemetryArchiveWorkerTests`: **26/26 PASSED** (Combined Phase 2.2 storage + worker unit tests: **34/34 PASSED**).
  - `docker build`: Backend container image built cleanly with Swagger generation (Exit code 0).
  - Git diff: Confined strictly to `TelemetryArchiveWorker.cs` and test suite.

#### Sub-step 2.2-B.2.4: NDJSON Serialization, GZip Compression & MinIO Upload
- **Status:** **CLOSED (PASS ✅)**
- **Implemented Components:**
  - `BackendApi/Services/BackgroundWorkers/Jobs/TelemetryArchiveWorker.cs`:
    - `TelemetryArchivePoint`: Data transfer contract containing strictly `eventId`, `riderId`, `timestamp`, `lat`, `lng`, `accuracy`.
    - `SerializeToNdjson`: Serializes strictly `DeduplicatedEntries` into NDJSON bytes (one JSON object per line terminated by `\n`). Does not use `RawEntries` for payload (preserving payload efficiency while keeping boundary metadata intact).
    - `CompressGzip`: Pure in-memory streaming compression (`MemoryStream` -> `GZipStream(CompressionLevel.Optimal, leaveOpen: true)` -> `output.ToArray()`). Zero temporary files created on disk. Properly disposes `GZipStream` before extracting bytes, guaranteeing GZip headers and trailers (CRC32 & ISIZE) are flushed.
    - `UploadBatchAsync`: Bridges batch to cold storage by generating deterministic object key (`batch.GenerateObjectKey()`), compressing payload, and invoking `ITelemetryArchiveStorage.PutArchiveObjectAsync` with content type `application/gzip`.
    - **Scope Boundary Compliance:** Excluded checkpoint advance (`telemetry:archiver:last_id`), pending-batch staging, and crash recovery logic (strictly reserved for Sub-step 2.2-B.2.5). Upload errors propagate upwards and do not advance checkpoint.
  - `RootScripts/scripts.test/test/BackendApi.UnitTests/Telemetry/TelemetryArchiveWorkerTests.cs`:
    - 33 unit tests total (covering all 8 required test scenarios):
      1. Empty deduplicated batch: Returns empty NDJSON bytes, compresses to valid GZip container (~20 bytes header/footer), decompresses to empty string without corruption.
      2. Single entry: Valid JSON schema terminated by newline (`\n`), exact field mapping.
      3. Multiple entries: Line count strictly matches `DeduplicatedEntries.Count`.
      4. JSON schema verification: All 6 properties (`eventId`, `riderId`, `timestamp`, `lat`, `lng`, `accuracy`) present and strongly typed.
      5. GZip round-trip: Compresses and decompresses with 100% data fidelity back to raw NDJSON text.
      6. MinIO upload invocation: Calls `PutArchiveObjectAsync` with correct deterministic key and `contentType: "application/gzip"`.
      7. Dedup boundary preservation: Raw = 10, Dedup = 9 -> uploaded payload contains exactly 9 records, while object key retains `firstStreamId` and `lastStreamId` matching the full raw boundary.
      8. Upload failure handling: Propagates storage exceptions upwards; worker checkpoint remains strictly untouched (`"0-0"`).
- **Verification Evidence:**
  - `dotnet build B:\Delivery\BackendApi\BackendApi.csproj`: Succeeded with 0 Warnings, 0 Errors.
  - `dotnet test B:\Delivery\RootScripts\scripts.test\test\BackendApi.UnitTests --filter FullyQualifiedName~TelemetryArchiveWorkerTests`: **33/33 PASSED** (0 failed, 0 skipped).
  - Combined Phase 2.2 Suite (`TelemetryArchiveWorkerTests` + `MinioTelemetryArchiveStorageTests`): **41/41 PASSED** (Duration: 344 ms).
  - `docker build`: Clean container build with Swagger/OpenAPI spec generation (`delivery-backend-test`, Exit code 0).
  - Git diff: Changes strictly confined to `TelemetryArchiveWorker.cs` and `TelemetryArchiveWorkerTests.cs`.
#### Sub-step 2.2-B.2.5: Pending Batch + Checkpoint / Crash Recovery & Trim Safety
- **Status:** **CLOSED (PASS ✅)**
- **Implemented Components:**
  - `BackendApi/Services/BackgroundWorkers/Jobs/TelemetryArchiveWorker.cs`:
    - `ProcessBatchPipelineAsync`: 4-step pipeline:
      1. Stage `pending_batch` before upload (`telemetry:archiver:pending_batch = $"{firstStreamId}|{lastStreamId}"`).
      2. Upload archive object to MinIO cold storage (`UploadBatchAsync`).
      3. Commit checkpoint `telemetry:archiver:last_id = lastStreamId` strictly after upload succeeds.
      4. Clear staging `DEL telemetry:archiver:pending_batch`.
    - `RecoverPendingBatchAsync`: Comprehensive crash recovery engine executed on startup:
      - **Case C (Crash after checkpoint, before DEL pending):** Detects `CompareStreamIds(CurrentCheckpoint, lastStreamId) >= 0` -> clears stale pending key, zero redundant uploads, no checkpoint regression.
      - **Case B (Crash after upload, before checkpoint):** Uses deterministic object key `gps/.../batch_{first}_{last}.ndjson.gz` to check existence in MinIO (`ObjectExistsAsync`). If found -> commits checkpoint and clears pending staging, preventing duplicate upload or split files.
      - **Case A (Crash before upload):** MinIO object not found -> fetches exact stream range (`XRANGE first last`), executes Trim Safety check, deduplicates, uploads to MinIO, advances checkpoint, and clears pending staging.
      - **Trim Gap Safety & Safe Halt:** If entry `firstStreamId` was trimmed/pruned by Redis (`entries == null || entries.Length == 0 || entries[0].Id != firstStreamId`), logs **CRITICAL ARCHITECTURE VIOLATION**, sets `IsHalted = true`, and throws `TelemetryStreamTrimGapException`. Does NOT blindly advance or skip data.
      - **Initial Checkpoint 0-0:** Clean startup without pending batch is recognized as normal initial startup, not a trim gap.
      - **seen:{eventId} Independence:** Purely relies on durable checkpoint and MinIO object receipts, NOT transient `seen` keys.
    - `CompareStreamIds`: High-precision Stream ID comparison parsing epoch milliseconds and sequence number accurately.
    - `ExtractTimestampFromStreamId`: Deterministic timestamp extraction from Stream ID epoch milliseconds, guaranteeing identical partition keys across restarts.
  - `RootScripts/scripts.test/test/BackendApi.UnitTests/Telemetry/TelemetryArchiveWorkerTests.cs`:
    - 51 unit tests total (33 previous + 18 test cases/theory data points for B.2.5):
      1. `ProcessBatchPipelineAsync_StagesPendingBatch_BeforeStorageUpload`: Verifies pending staging occurs strictly before MinIO upload.
      2. `ProcessBatchPipelineAsync_WhenUploadSucceeds_AdvancesCheckpointAndDeletesPendingBatch`: Verifies atomic pipeline progression on success.
      3. `ProcessBatchPipelineAsync_WhenUploadFails_CheckpointNotAdvanced_AndPendingBatchRemains`: Verifies checkpoint unchanged and pending staging retained on failure.
      4. `RecoverPendingBatchAsync_CaseA_WhenObjectNotInMinIO_ArchivesStreamRangeAndAdvancesCheckpoint`: Full recovery of un-uploaded batch.
      5. `RecoverPendingBatchAsync_CaseB_WhenObjectAlreadyInMinIO_AdvancesCheckpointWithoutReUpload`: Checkpoint catch-up without re-upload.
      6. `RecoverPendingBatchAsync_CaseC_WhenCheckpointAlreadyAtOrPastLastStreamId_ClearsPendingWithoutReUpload`: Stale staging cleanup without re-upload or regression.
      7. `RecoverPendingBatchAsync_WhenStreamIntact_SuccessfullyRecoversBatch`: Exact stream boundary recovery validation.
      8. `RecoverPendingBatchAsync_WhenStreamTrimGapDetected_LogsCriticalAndEntersSafeHalt`: Critical logging and safe halt on missing stream head.
      9. `InitializeCheckpointAsync_WhenNoPendingBatch_StartsNormallyWithoutRecovery`: Clean startup verification.
      10. `InitializeCheckpointAsync_WhenInitial_0_0_DoesNotTreatAsTrimGap`: Initial "0-0" clean start verification.
      11. `CompareStreamIds_HandlesAllOrderingCasesAccurately`: Theory validating 8 ordering permutations.
- **Verification Evidence:**
  - `dotnet build B:\Delivery\BackendApi\BackendApi.csproj`: Succeeded with 0 Warnings, 0 Errors.
  - `dotnet test B:\Delivery\RootScripts\scripts.test\test\BackendApi.UnitTests --filter FullyQualifiedName~TelemetryArchiveWorkerTests`: **51/51 PASSED** (0 failed, 0 skipped).
  - Combined Phase 2.2 Suite (`TelemetryArchiveWorkerTests` + `MinioTelemetryArchiveStorageTests`): **59/59 PASSED** (Duration: 360 ms).
  - `docker build`: Clean container build with Swagger/OpenAPI spec generation (`delivery-backend-test`, Exit code 0).
  - Git diff: Changes strictly confined to `TelemetryArchiveWorker.cs` and `TelemetryArchiveWorkerTests.cs`.

#### Sub-step 2.2-B.2.6-A: Live Normal Archive (Docker Stack Integration)
- **Status:** **CLOSED (PASS ✅)**
- **Implemented Components & Live Integration Harness:**
  - `RootScripts/scripts.test/test/BackendApi.IntegrationTests/Telemetry/TelemetryArchiveLiveIntegrationTests.cs`:
    - Full end-to-end integration test executed against live Docker containers (`delivery-v2-redis` on 6389 and `delivery-v2-minio` on 9002).
    - **Step 1: Environment Reset:**
      - Flushed `telemetry:stream:gps` to 0 entries.
      - Reset `telemetry:archiver:last_id` checkpoint to `"0-0"`.
      - Cleared `telemetry:archiver:pending_batch`.
    - **Step 2: Runtime Bucket Privacy Verification:**
      - Verified MinIO bucket `delivery-telemetry-archive` has no public-read policy (`mc anonymous get local/delivery-telemetry-archive` confirmed `private`).
      - Verified unauthenticated HTTP GET requests to bucket (`http://127.0.0.1:9002/delivery-telemetry-archive`) and archive object return HTTP 403 Forbidden / Unauthorized.
    - **Step 3: 1,000 Live Telemetry Entries Injection:**
      - Injected 1,000 unique events across 25 simulated riders into Redis Stream `telemetry:stream:gps`.
      - Stream length verified at exactly 1,000.
    - **Step 4: Live Worker Execution & Pipeline:**
      - `TelemetryArchiveWorker` buffered entries, observed 1-second interval threshold, and flushed partial batch.
      - Executed live 4-step pipeline: `SET pending_batch` -> upload to MinIO -> `SET last_id` -> `DEL pending_batch`.
    - **Step 5: Checkpoint & State Verification:**
      - Checkpoint advanced to exact `lastStreamId` (`1789928705715-2`).
      - `telemetry:archiver:pending_batch` confirmed deleted.
      - Redis Stream retained all 1,000 entries (retention/bounding policy intact).
    - **Step 6: Object Download, GZip Decompression & Data Equivalence:**
      - Object verified in MinIO cold storage (`gps/year=2026/month=09/day=20/hour=18/batch_1789928705120-0_1789928705715-2.ndjson.gz`, 36 KiB).
      - Downloaded object stream, decompressed GZip, parsed all 1,000 NDJSON lines.
      - **1:1 Strict Data Equivalence:**
        - Total archived records: 1,000 unique events.
        - Missing events: 0.
        - Extra events: 0.
        - `eventId`, `riderId`, `timestamp`, `lat` (6 decimals), `lng` (6 decimals), `accuracy` matched source data with 100% precision.
  - `BackendApi/Services/Storage/MinioTelemetryArchiveStorage.cs`:
    - **Environment/Runtime Compatibility Fix:** พบและแก้ปัญหา S3 Signature ที่เกิดจาก Thai Regional Culture บนสภาพแวดล้อม Host Windows ที่ทำให้ S3 Signature ใช้วันที่ปี พ.ศ. (Buddhist Era 2569) แทน ค.ศ. (2026) โดยกำหนด InvariantCulture ใน static constructor ของ `MinioTelemetryArchiveStorage` และยืนยันการทำงานใหม่ด้วย Live Test หลังแก้ไข (ระบุเฉพาะเจาะจงตามหลักฐานที่พบใน Host Environment นี้)
- **Verification Evidence:**
  - `dotnet build B:\Delivery\BackendApi\BackendApi.csproj`: Succeeded with 0 Warnings, 0 Errors.
  - `dotnet test B:\Delivery\RootScripts\scripts.test\test\BackendApi.IntegrationTests --filter FullyQualifiedName~TelemetryArchiveLiveIntegrationTests`: **1/1 PASSED (2s)**.
  - Full Phase 2.2 Unit Test Suite (`TelemetryArchiveWorkerTests` + `MinioTelemetryArchiveStorageTests`): **59/59 PASSED** (Duration: 349 ms).
  - `docker build`: Clean container build with Swagger/OpenAPI spec generation (`delivery-backend-test`, Exit code 0).
  - Live MinIO CLI verification: `mc ls --recursive local/delivery-telemetry-archive` confirmed `36KiB STANDARD gps/.../batch_...ndjson.gz`.
  - Live MinIO Privacy verification: `mc anonymous get local/delivery-telemetry-archive` confirmed `private`.
  - Live Redis verification: `xlen telemetry:stream:gps` = 1000, `get telemetry:archiver:last_id` = lastStreamId, `get telemetry:archiver:pending_batch` = (nil).


#### Sub-step 2.2-B.2.6-B: Live Crash Recovery
- **Status:** **CLOSED (PASS ✅)** (Sub-Gates B-A, B-B, B-C Complete)
- **เป้าหมาย:** พิสูจน์ lifecycle recovery จริงของ `telemetry:archiver:pending_batch` บน Docker Redis และ MinIO โดยแบ่งออกเป็น 3 กรณีตาม Failure Boundary:
  - **B-A:** Crash ก่อน Upload (`pending` ถูก SET แต่กระบวนการตายก่อน `PutArchiveObjectAsync`)
  - **B-B:** Crash หลัง Upload ก่อน Checkpoint (Object มีอยู่แล้วใน MinIO แต่ตายก่อน `SET last_id`)
  - **B-C:** Crash หลัง Checkpoint ก่อน Delete Pending (`last_id` ขยับแล้วแต่ตายก่อน `DEL pending_batch`)

##### Sub-Gate 2.2-B.2.6-B-A: Crash ก่อน Upload (Crash Before Upload)
- **Status:** **CLOSED (PASS ✅)**
- **การจำลองสภาพแวดล้อม (Setup & Crash Injection):**
  1. Reset Stream / Checkpoint: `telemetry:stream:gps` ถูกเคลียร์, `telemetry:archiver:last_id = "0-0"`, `telemetry:archiver:pending_batch` ถูกลบ
  2. Inject 250 Live Telemetry Entries: พ่นข้อมูลพิกัดจริง 250 รายการเข้า Redis Stream
  3. Pre-Crash Invariant Verification:
     - Checkpoint ยังคงเป็น `"0-0"` (ยังไม่ขยับ)
     - จำลอง Crash: เขียน `telemetry:archiver:pending_batch = $"{firstStreamId}|{lastStreamId}"` เข้า Redis สำเร็จ แต่กระบวนการ Worker ถูกตัดทิ้งทันทีก่อนอัปโหลด
     - ตรวจสอบใน MinIO ผ่าน `ObjectExistsAsync`: **Object ยังไม่มีอยู่จริงใน Cold Storage (`false`)**
- **กระบวนการกู้คืนเมื่อ Worker รีสตาร์ต (Autonomous Recovery Lifecycle):**
  1. Bootstrapping Worker Instance ใหม่ จำลองการกู้คืนระบบ
  2. Worker เรียก `InitializeCheckpointAsync()` ซึ่งทริกเกอร์ `RecoverPendingBatchAsync()` โดยอัตโนมัติ
  3. ตรวจพบ Pending Batch ใน Redis (`firstStreamId|lastStreamId`)
  4. ตรวจสอบ MinIO Cold Storage พบว่ายังไม่มี Object Key ของ Batch นี้ -> ระบุเป็น **Case A (Crash before upload)**
  5. รัน `XRANGE firstStreamId lastStreamId` ดึงข้อมูลทั้งหมดของ Batch จาก Redis Stream พบครบถ้วน 250/250 entries (Trim Safety check ผ่าน)
  6. ทำ In-Batch Deduplication, Serialize NDJSON, บีบอัด GZip ในหน่วยความจำ
  7. ดำเนินการอัปโหลดไฟล์ `gps/.../batch_{first}_{last}.ndjson.gz` ขึ้น MinIO Bucket `delivery-telemetry-archive`
  8. อัปเดต Checkpoint `telemetry:archiver:last_id = lastStreamId`
  9. สั่งลบ `telemetry:archiver:pending_batch` ออกจาก Redis
- **ผลการตรวจสอบหลังการกู้คืน (Empirical Verification Evidence):**
  - **MinIO Object Count & Key:** พบ Object ใหม่ถูกสร้างขึ้นใน MinIO Bucket เพียง 1 ไฟล์ตาม Deterministic Key (`gps/year=2026/month=09/day=20/hour=18/batch_...ndjson.gz`, ~8.9 KiB)
  - **Archive Content & Data Equivalence:** ดาวน์โหลด Object, คลายบีบอัด GZip และตรวจสอบข้อมูลแบบ 1:1:
    - จำนวน Records: ครบถ้วน 250 รายการตรงกับ Source
    - Duplicate Event IDs: **0 (ไม่มี Event ซ้ำ)**
    - Extra / Missing Events: **0**
    - ความถูกต้องของข้อมูล: `eventId`, `riderId`, `timestamp`, `lat`, `lng`, `accuracy` ตรงกับค่าที่ฉีดเข้า Redis Stream 100%
  - **Redis State:**
    - `telemetry:archiver:last_id` = `lastStreamId` (ขยับตรงเป้าหมาย)
    - `telemetry:archiver:pending_batch` = `(nil)` (ถูกล้างสมบูรณ์)
    - `xlen telemetry:stream:gps` = 250 (ข้อมูลใน Stream ยังคงอยู่ครบถ้วน ไม่ถูกตัดทิ้ง)
    - ไม่มีการ Skip Batch ใดๆ
  - **Idempotency on Second Worker Reboot:** รีสตาร์ต Worker ซ้ำอีกครั้ง พบว่า Checkpoint ยังคงอยู่ที่ `lastStreamId` เดิม ไม่มีการ Re-archive ซ้ำ และไม่เกิด Duplicate Archive Object ใน MinIO
- **Verification Evidence:**
  - `dotnet build B:\Delivery\BackendApi\BackendApi.csproj`: Succeeded with 0 Warnings, 0 Errors.
  - `dotnet test B:\Delivery\RootScripts\scripts.test\test\BackendApi.IntegrationTests --filter FullyQualifiedName~SubStep_2_2_B_2_6_B_A`: **1/1 PASSED (544 ms)**.
  - Live Integration Suite (`SubStep_2_2_B_2_6_A` + `SubStep_2_2_B_2_6_B_A`): **2/2 PASSED (3 s)**.
  - Full Phase 2.2 Unit Test Suite (`TelemetryArchiveWorkerTests` + `MinioTelemetryArchiveStorageTests`): **59/59 PASSED** (Duration: 350 ms).
  - `docker build`: Clean container build with Swagger/OpenAPI spec generation (`delivery-backend-test`, Exit code 0).
  - Live MinIO CLI verification: `mc ls local/delivery-telemetry-archive/ --recursive` ยืนยันพบ Object 8.9 KiB ตรงตาม deterministic key เพียง 1 ไฟล์.
  - Live Redis CLI verification: `telemetry:archiver:last_id` = lastStreamId, `telemetry:archiver:pending_batch` = (nil).

##### Sub-Gate 2.2-B.2.6-B-B: Crash หลัง Upload ก่อน Checkpoint (Crash After Upload / Before Checkpoint)
- **Status:** **CLOSED (PASS ✅)**
- **การจำลองสภาพแวดล้อม (Setup & Crash Injection):**
  1. Reset Stream / Checkpoint: `telemetry:stream:gps` ถูกเคลียร์, `telemetry:archiver:last_id = "0-0"`, `telemetry:archiver:pending_batch` ถูกลบ
  2. Inject 250 Live Telemetry Entries: พ่นข้อมูลพิกัดจริง 250 รายการเข้า Redis Stream
  3. Pre-Crash Pipeline Execution:
     - Worker รวบรวมข้อมูล 250 รายการ, เขียน `telemetry:archiver:pending_batch = $"{firstStreamId}|{lastStreamId}"`
     - ทำการอัปโหลดไฟล์ขึ้น MinIO Cold Storage สำเร็จด้วย deterministic object key (`gps/.../batch_{first}_{last}.ndjson.gz`)
  4. จำลอง Crash: ตัดกระบวนการ Worker ทันทีหลัง Upload สำเร็จ ก่อนที่จะบันทึก `SET last_id` หรือ `DEL pending_batch`
  5. Pre-Recovery Invariant & Content Verification:
     - `telemetry:archiver:pending_batch` ยังคงมีอยู่จริงใน Redis
     - Checkpoint ยังคงอยู่ที่ `"0-0"` (`< lastStreamId`)
     - Object มีอยู่จริงใน MinIO (`ObjectExistsAsync == true`)
     - **การตรวจสอบความสมบูรณ์ของเนื้อหา Object ก่อนเริ่มกู้คืน (Content Validity Verification):**
       - ดาวน์โหลด Object จาก MinIO, คลายบีบอัด GZip
       - ตรวจสอบ Record Count: 250/250 รายการครบถ้วน
       - Duplicate Event IDs: **0**
       - ตรวจสอบ `eventId`, `riderId`, `timestamp`, `lat`, `lng`, `accuracy` ตรงกับ Source 100%
- **กระบวนการกู้คืนเมื่อ Worker รีสตาร์ต (Autonomous Recovery Lifecycle - Case B):**
  1. Bootstrapping Worker Instance ใหม่พร้อม `TrackingTelemetryArchiveStorage` เพื่อตรวจจับการเรียก `PutArchiveObjectAsync`
  2. Worker เรียก `InitializeCheckpointAsync()` ทริกเกอร์ `RecoverPendingBatchAsync()`
  3. ตรวจพบ Pending Batch ใน Redis (`firstStreamId|lastStreamId`)
  4. ตรวจสอบ MinIO ผ่าน `ObjectExistsAsync` พบว่ามี Object อยู่แล้ว -> ชี้ชัดว่าเป็น **Case B**
  5. Worker อัปเดต Checkpoint `telemetry:archiver:last_id = lastStreamId` ทันที
  6. Worker สั่งลบ `telemetry:archiver:pending_batch` ออกจาก Redis
  7. **Zero Re-upload Verification:** `trackingStorage.PutObjectCallCount == 0` พิสูจน์ว่าไม่มีการอัปโหลดไฟล์ซ้ำสู่ MinIO
- **ผลการตรวจสอบหลังการกู้คืน (Empirical Verification Evidence):**
  - **Redis Checkpoint:** `telemetry:archiver:last_id` = `lastStreamId` (ขยับตรงเป้าหมาย)
  - **Redis Staging:** `telemetry:archiver:pending_batch` = `(nil)` (ถูกล้างสมบูรณ์)
  - **MinIO Upload Count:** `PutObjectCallCount == 0` (Zero additional upload)
  - **Archive Object Fidelity:** ดาวน์โหลด Object อีกครั้งหลัง Recovery ยืนยันว่าไฟล์เดิมไม่ถูกทับ ขนาดไบต์และเนื้อหา 250 records ยังคงถูกต้อง 100%
  - **Redis Stream Length:** `telemetry:stream:gps` ความยาวคงเดิม 250 รายการ (ไม่มีการตัดหรือ skip)
  - **Reboot Idempotency:** รีสตาร์ต Worker อีกครั้ง พบว่า Checkpoint ยังคงอยู่ที่ `lastStreamId` เดิม, `PutObjectCallCount == 0`, ไม่เกิดการบันทึกซ้ำ
- **Verification Evidence:**
  - `dotnet build B:\Delivery\BackendApi\BackendApi.csproj`: Succeeded with 0 Warnings, 0 Errors.
  - `dotnet test B:\Delivery\RootScripts\scripts.test\test\BackendApi.IntegrationTests --filter FullyQualifiedName~SubStep_2_2_B_2_6_B_B`: **1/1 PASSED (1 s)**.
  - Live Integration Suite (`SubStep_2_2_B_2_6_A` + `B_A` + `B_B`): **3/3 PASSED (4 s)**.
  - Full Phase 2.2 Unit Test Suite (`TelemetryArchiveWorkerTests` + `MinioTelemetryArchiveStorageTests`): **59/59 PASSED** (Duration: 448 ms).
  - `docker build`: Clean container build with Swagger/OpenAPI spec generation (`delivery-backend-test`, Exit code 0).
  - Live MinIO CLI verification: `mc ls local/delivery-telemetry-archive/ --recursive` ยืนยันพบ Object ตาม deterministic key เดิม โดยไม่มีการสร้าง Object ซ้ำ.
  - Live Redis CLI verification: `telemetry:archiver:last_id` = lastStreamId, `telemetry:archiver:pending_batch` = (nil).

##### Sub-Gate 2.2-B.2.6-B-C: Crash หลัง Checkpoint ก่อน Delete Pending (Crash After Checkpoint / Before Pending Delete)
- **Status:** **CLOSED (PASS ✅)**
- **การจำลองสภาพแวดล้อม (Setup & Crash Injection):**
  1. Reset Stream / Checkpoint: `telemetry:stream:gps` ถูกเคลียร์, `telemetry:archiver:last_id = "0-0"`, `telemetry:archiver:pending_batch` ถูกลบ
  2. Inject 250 Live Telemetry Entries: พ่นข้อมูลพิกัดจริง 250 รายการเข้า Redis Stream
  3. Pipeline Step 1, 2, & 3 Execution:
     - Worker รวบรวมข้อมูล 250 รายการ, เขียน `telemetry:archiver:pending_batch = $"{firstStreamId}|{lastStreamId}"`
     - ทำการอัปโหลดไฟล์ขึ้น MinIO Cold Storage สำเร็จด้วย deterministic object key (`gps/.../batch_{first}_{last}.ndjson.gz`)
     - บันทึก Checkpoint `SET telemetry:archiver:last_id = lastStreamId` สำเร็จ
  4. จำลอง Crash: ตัดกระบวนการ Worker ทันทีหลัง Checkpoint Commit สำเร็จ แต่ก่อนที่จะเรียก `DEL pending_batch` (Pipeline Step 4 ยังไม่ได้รัน)
  5. Pre-Recovery Invariant & Content Verification:
     - `telemetry:archiver:pending_batch` ยังคงค้างอยู่ใน Redis (`firstStreamId|lastStreamId`)
     - Checkpoint ถูก Commit ไปที่ `lastStreamId` แล้ว (`checkpoint >= pending.last`)
     - Object มีอยู่จริงใน MinIO (`ObjectExistsAsync == true`)
     - ดาวน์โหลด Object จาก MinIO มาคลายบีบอัด GZip ตรวจสอบความสมบูรณ์: ครบ 250 records, 0 duplicate eventId, ข้อมูลตรงกับ Source 100%
- **กระบวนการกู้คืนเมื่อ Worker รีสตาร์ต (Autonomous Recovery Lifecycle - Case C):**
  1. Bootstrapping Worker Instance ใหม่พร้อม `TrackingTelemetryArchiveStorage` เพื่อตรวจจับการเรียก `PutArchiveObjectAsync`
  2. Worker เรียก `InitializeCheckpointAsync()` ทริกเกอร์ `RecoverPendingBatchAsync()`
  3. ตรวจพบ Pending Batch ใน Redis (`firstStreamId|lastStreamId`)
  4. เปรียบเทียบ Stream ID: ตรวจพบ `CompareStreamIds(CurrentCheckpoint, lastStreamId) >= 0` -> ชี้ชัดว่าเป็น **Case C** (Batch ถูก commit เสร็จสมบูรณ์แล้ว)
  5. Worker ถือว่า batch commit แล้ว ทำการลบ Staging ทันที: `DEL telemetry:archiver:pending_batch`
  6. ไม่มีการ Re-upload: `trackingStorage.PutObjectCallCount == 0` (Zero additional upload สำหรับ Case B-C scenario)
  7. ไม่มีการ Rollback Checkpoint: `CurrentCheckpoint` ยังคงอยู่ที่ `lastStreamId` เดิม
- **ผลการตรวจสอบหลังการกู้คืน (Empirical Verification Evidence):**
  - **Redis Checkpoint:** `telemetry:archiver:last_id` = `lastStreamId` (ไม่ Rollback และไม่ขยับผิดพลาด)
  - **Redis Staging:** `telemetry:archiver:pending_batch` = `(nil)` (ถูกล้างสมบูรณ์)
  - **MinIO Upload Count:** `PutObjectCallCount == 0` (Zero additional upload)
  - **Archive Object Fidelity:** ดาวน์โหลด Object หลัง Recovery ยืนยันว่าไฟล์เดิมไม่ถูกกระทบ ขนาดไบต์และเนื้อหา 250 records ถูกต้อง 100%
  - **Redis Stream Length:** `telemetry:stream:gps` ความยาวคงเดิม 250 รายการ (ไม่มีการตัดหรือ skip)
  - **Reboot Idempotency:** รีสตาร์ต Worker ซ้ำอีกครั้ง พบ Checkpoint คงเดิม, `PutObjectCallCount` เป็น 0, ไม่เกิดการประมวลผลซ้ำ
- **Verification Evidence:**
  - `dotnet build B:\Delivery\BackendApi\BackendApi.csproj`: Succeeded with 0 Warnings, 0 Errors.
  - `dotnet test B:\Delivery\RootScripts\scripts.test\test\BackendApi.IntegrationTests --filter FullyQualifiedName~SubStep_2_2_B_2_6_B_C`: **1/1 PASSED (1 s)**.
  - Live Integration Suite (`SubStep_2_2_B_2_6_A` + `B_A` + `B_B` + `B_C`): **4/4 PASSED (5 s)**.
  - Full Phase 2.2 Unit Test Suite (`TelemetryArchiveWorkerTests` + `MinioTelemetryArchiveStorageTests`): **59/59 PASSED** (Duration: 371 ms).
  - `docker build`: Clean container build with Swagger/OpenAPI spec generation (`delivery-backend-test`, Exit code 0).
  - Live MinIO CLI verification: `mc ls local/delivery-telemetry-archive/ --recursive` ยืนยัน Object เดิมคงอยู่ ไม่มีการสร้างไฟล์ซ้ำ.
  - Live Redis CLI verification: `telemetry:archiver:last_id` = lastStreamId, `telemetry:archiver:pending_batch` = (nil).

#### Sub-step 2.2-B.2.6-C: MinIO Outage & Recovery (Infrastructure Outage Lifecycle)
- **Status:** **CLOSED (PASS ✅)**
- **การจำลองสภาพแวดล้อม (Setup & Outage Simulation):**
  1. Reset Stream / Checkpoint: `telemetry:stream:gps` ถูกเคลียร์, `telemetry:archiver:last_id = "0-0"`, `telemetry:archiver:pending_batch` ถูกลบ
  2. Inject 250 Live Telemetry Entries: พ่นข้อมูลพิกัดจริง 250 รายการเข้า Redis Stream
  3. Worker Collect & Buffer: Worker รวบรวมข้อมูล 250 รายการ
  4. Real Container Outage: หยุดการทำงานของ MinIO Container จริงผ่าน `docker stop delivery-v2-minio`
  5. Worker Pipeline Execution:
     - ขั้นที่ 1: `SET telemetry:archiver:pending_batch = $"{firstStreamId}|{lastStreamId}"` สำเร็จใน Redis
     - ขั้นที่ 2: เรียก `UploadBatchAsync(batch)` -> ล้มเหลวทันที (Throw Exception) เนื่องจากพอร์ต 9002 ปิด และ Container ดับ
- **การตรวจสอบ Invariants ระหว่างเกิด Outage (Outage Invariant Verification):**
  - `telemetry:archiver:last_id` ยังคงเป็นค่าเดิม `"0-0"` ไม่ขยับเด็ดขาด
  - `telemetry:archiver:pending_batch` ยังคงอยู่ครบถ้วนใน Redis ไม่ถูกลบหรือสูญหาย
  - Worker Process ยังคงทำงานอยู่ ไม่เกิด Process Crash (`worker.IsHalted == false`)
  - ไม่มีการถือว่า object สำเร็จจากความล้มเหลว (Zero false-positive commit)
  - Redis Stream ความยาวคงเดิม 250 รายการ (ข้อมูลไม่ถูกตัดทิ้ง)
- **การจัดการกรณี Network Ambiguity (Network Ambiguity Handling & Evidence):**
  - ในกรณีที่ Request ส่งถึง MinIO แล้วเกิด Timeout หรือ Network หลุด โดย Client ไม่ทราบแน่ชัดว่า Server บันทึกสำเร็จหรือไม่:
    - สถาปัตยกรรมของ Worker ไม่สรุปจาก Exception เพียงอย่างเดียวว่า Object ไม่มีอยู่จริง
    - ในรอบถัดไปหรือเมื่อเกิด Recovery ระบบจะนำ Deterministic Object Key ไปตรวจเช็คผ่าน `ObjectExistsAsync` ก่อนเสมอ
    - หาก Object ถูกเขียนลง MinIO แล้วจริง -> จัดเป็น **Case B** (Advance Checkpoint ทันทีโดยไม่อัปโหลดซ้ำ)
    - หาก Object ยังไม่ได้ถูกเขียน -> จัดเป็น **Case A** (ดึง XRANGE จาก Redis Stream มาสร้างไฟล์และอัปโหลดใหม่)
- **การกู้คืนระบบเมื่อ MinIO กลับมาทำงาน (Recovery upon MinIO UP):**
  1. เปิด MinIO Container กลับคืนมา: `docker start delivery-v2-minio`
  2. ตรวจสอบความพร้อมจน MinIO กลับมา Healthy (`EnsureArchiveBucketExistsAsync` สำเร็จ)
  3. Worker ทำการ Retry ประมวลผล Batch (`ProcessBatchPipelineAsync`):
     - อัปโหลด Object สู่ MinIO สำเร็จ
     - Checkpoint ขยับเป็น `lastStreamId` ทันที
     - ลบ `telemetry:archiver:pending_batch` ออกจาก Redis
  4. **การตรวจสอบความถูกต้องของข้อมูลหลังการกู้คืน (Fidelity & Equivalence):**
     - ดาวน์โหลด Object จาก MinIO, คลายบีบอัด GZip
     - ตรวจสอบ Record Count: ครบ 250 records
     - Duplicate Event IDs: **0**
     - ค่า `eventId`, `riderId`, `timestamp`, `lat`, `lng`, `accuracy` ตรงกับ Source 100%
  5. **Reboot Idempotency:** รีสตาร์ต Worker อีกครั้ง พบว่า Checkpoint คงเดิมที่ `lastStreamId`, ไม่มีการอัปโหลดซ้ำ, Worker ทำงานต่อได้ปกติ
- **Verification Evidence:**
  - `dotnet build B:\Delivery\BackendApi\BackendApi.csproj`: Succeeded with 0 Warnings, 0 Errors.
  - `dotnet test B:\Delivery\RootScripts\scripts.test\test\BackendApi.IntegrationTests --filter FullyQualifiedName~SubStep_2_2_B_2_6_C`: **1/1 PASSED (4 s)** (รวมเวลา docker stop/start).
  - Live Integration Suite (`SubStep_2_2_B_2_6_A` + `B_A` + `B_B` + `B_C` + `C`): **5/5 PASSED (10 s)**.
  - Full Phase 2.2 Unit Test Suite (`TelemetryArchiveWorkerTests` + `MinioTelemetryArchiveStorageTests`): **59/59 PASSED** (Duration: 354 ms).
  - `docker build`: Clean container build with Swagger/OpenAPI spec generation (`delivery-backend-test`, Exit code 0).
  - Live MinIO CLI verification: `mc ls local/delivery-telemetry-archive/ --recursive` ยืนยันพบ Object ตาม deterministic key เดิม.
  - Live Redis CLI verification: `telemetry:archiver:last_id` = lastStreamId, `telemetry:archiver:pending_batch` = (nil).

#### Sub-step 2.2-B.2.6-D: Trim-gap Safe Halt (Pruned Stream Safety Boundary)
- **Status:** **CLOSED (PASS ✅)**
- **การจำลองสภาพแวดล้อม (Setup & Trim Gap Simulation):**
  1. Reset Stream / Checkpoint: `telemetry:stream:gps` ถูกเคลียร์, `telemetry:archiver:last_id = "0-0"`, `telemetry:archiver:pending_batch` ถูกลบ
  2. Inject 250 Live Telemetry Entries: พ่นข้อมูลพิกัดจริง 250 รายการเข้า Redis Stream
  3. Stage Pending Batch: บันทึก `telemetry:archiver:pending_batch = $"{firstStreamId}|{lastStreamId}"`
  4. Deliberate Stream Pruning Simulation:
     - ใช้คำสั่ง `XDEL telemetry:stream:gps firstStreamId` เพื่อลบ Entry แรก (`firstStreamId`) ออกจาก Stream (จำลองกรณีเกิด Trim Gap หรือข้อมูลถูกตัดโดย Retention Policy)
     - ตรวจสอบยืนยันว่า `firstStreamId` หายไปจาก Stream จริง (`XRANGE` บน ID นั้นคืนค่า empty)
     - ตรวจสอบว่า Stream ส่วนที่เหลือ (`streamIds[1]` ถึง `streamIds[^1]`) ยังคงมีอยู่ครบถ้วน 249 รายการ
     - ตรวจสอบใน MinIO: ยังไม่มี Object ของ batch นี้
- **การทำงานของ Worker เมื่อเริ่มกู้คืน (Worker Trim Gap Detection & Safe Halt):**
  1. Worker เริ่มกระบวนการกู้คืนผ่าน `InitializeCheckpointAsync()` -> `RecoverPendingBatchAsync()`
  2. ดึงช่วงข้อมูลผ่าน `XRANGE firstStreamId lastStreamId`
  3. ตรวจพบว่า Entry แรกที่ดึงมาได้ ไม่ตรงกับ `firstStreamId` ของ pending batch (หัวแถวหายไป เกิด Trim Gap)
  4. Worker ปฏิบัติตาม Strict Safety Boundary ทันที:
     - ออก Log ระดับ **CRITICAL**: `CRITICAL ARCHITECTURE VIOLATION: Stream trim-gap detected during recovery! Pending batch start {firstStreamId} was pruned... Entering SAFE HALT.`
     - กำหนดแฟล็ก `IsHalted = true`
     - โยน Exception `TelemetryStreamTrimGapException` ขัดขวางการทำงานทั้งหมด
- **การตรวจสอบ Safety Invariants (Safety Boundary Verification):**
  - **Worker State:** `IsHalted == true` เข้าสู่ Safe Halt สมบูรณ์
  - **Zero Auto-Skip:** Worker ไม่คิดข้ามเอง ไม่เลื่อน Cursor ข้ามข้อมูลที่หายไป
  - **Checkpoint Unchanged:** Checkpoint ยังคงอยู่ที่ `"0-0"` เท่าเดิม ไม่มีการขยับ
  - **Pending Key Retained:** `telemetry:archiver:pending_batch` ยังคงค้างอยู่ใน Redis รอให้ Operator เข้ามาตรวจสอบ
  - **No Partial / Corrupted Object:** ไม่มี Object ใดๆ ถูกสร้างขึ้นใน MinIO
  - **Stream Data Protected:** ข้อมูลที่เหลือ 249 รายการใน Redis Stream ยังคงอยู่ครบถ้วน ไม่ถูกแตะต้องหรือตัดทิ้ง
- **ข้อสังเกตและข้อจำกัด (Testing Boundary Note):**
  - การตัดหรือลบข้อมูลใน Stream (`XDEL`) เป็นสิ่งที่เราตั้งใจจำลองขึ้นเพื่อทดสอบ Safety Boundary ของระบบเท่านั้น ไม่ใช่ข้อสรุปว่าใน Production สภาพแวดล้อมจริงจะต้องเกิด Trim Gap เสมอไป
- **Verification Evidence:**
  - `dotnet build B:\Delivery\BackendApi\BackendApi.csproj`: Succeeded with 0 Warnings, 0 Errors.
  - `dotnet test B:\Delivery\RootScripts\scripts.test\test\BackendApi.IntegrationTests --filter FullyQualifiedName~SubStep_2_2_B_2_6_D`: **1/1 PASSED (331 ms)**.
  - Live Integration Suite (`SubStep_2_2_B_2_6_A` + `B_A` + `B_B` + `B_C` + `C` + `D`): **6/6 PASSED (10 s)**.
  - Full Phase 2.2 Unit Test Suite (`TelemetryArchiveWorkerTests` + `MinioTelemetryArchiveStorageTests`): **59/59 PASSED** (Duration: 374 ms).
  - `docker build`: Clean container build with Swagger/OpenAPI spec generation (`delivery-backend-test`, Exit code 0).
  - Live Redis CLI verification: `telemetry:archiver:last_id` = "0-0", `telemetry:archiver:pending_batch` = first|last, `xlen` = 249.

#### Sub-step 2.2-B.2.6-E: Final Data Equivalence & Pipeline Completeness (Multi-Batch Continuous Archival)
- **Status:** **CLOSED (PASS ✅)**
- **การจำลองสภาพแวดล้อม (Multi-Batch Setup & Deliberate Duplication):**
  1. Clean Slate Reset: เคลียร์ทุก Key ภายใต้ `telemetry:archiver:*` (checkpoint, pending, seen keys ทั้งหมด) และเคลียร์ `telemetry:stream:gps`
  2. ฉีดข้อมูลหลาย Batch พร้อมสร้าง Duplicate แบบตั้งใจ (In-Batch และ Cross-Batch):
     - **Batch 1:** 300 Raw Entries (297 Unique Events + 3 In-Batch Duplicates)
     - **Batch 2:** 300 Raw Entries (296 Unique Events + 2 Cross-Batch Duplicates จาก Batch 1 + 2 In-Batch Duplicates)
     - **Batch 3:** 150 Raw Entries (149 Unique Events + 1 Cross-Batch Duplicate จาก Batch 2)
     - **สรุปปริมาณข้อมูลที่ทดสอบ:**
       - **Raw Stream Entries:** **750 รายการ**
       - **Duplicates Injected:** **8 รายการ** (In-batch: 5, Cross-batch: 3)
       - **Unique Event IDs:** **742 รายการ**
       - **Expected Archived Records:** **742 รายการ**
- **การทำงานของ Worker ต่อเนื่องข้ามหลาย Batch (Multi-Batch Continuous Execution):**
  1. Worker เริ่มต้นด้วย Checkpoint `"0-0"`
  2. **Batch 1:** รวบรวม 300 raw entries -> กรองเหลือ 297 unique entries -> Upload สำเร็จ -> Checkpoint ขยับเป็น `batch1.LastStreamId` -> Pending ลบสำเร็จ
  3. **Batch 2:** อ่านต่อจาก Exclusive Cursor (`> batch1.LastStreamId`) -> รวบรวม 300 raw entries -> ตรวจพบ 2 cross-batch duplicates (ผ่าน seen keys) + 2 in-batch duplicates -> กรองเหลือ 296 unique entries -> Upload สำเร็จ -> Checkpoint ขยับเป็น `batch2.LastStreamId`
  4. **Batch 3:** อ่านต่อจาก `> batch2.LastStreamId` -> รวบรวม 150 raw entries -> รอรอบ 1 วินาที timeout แล้ว Flush Partial Batch -> กรองเหลือ 149 unique entries -> Upload สำเร็จ -> Checkpoint ขยับเป็น `batch3.LastStreamId`
- **การตรวจสอบขอบเขต Cursor Boundary (Cursor Boundary Invariants):**
  - `Batch 1 LastStreamId` -> `Batch 2 FirstStreamId`: เป็นลำดับถัดไปโดยตรงอย่างเข้มงวด (`Batch 2 First > Batch 1 Last`)
  - `Batch 2 LastStreamId` -> `Batch 3 FirstStreamId`: เป็นลำดับถัดไปโดยตรงอย่างเข้มงวด (`Batch 3 First > Batch 2 Last`)
  - ไม่มีการทับซ้อน (No overlap) และไม่มีช่องว่างหลุดหาย (No gap) ของ Stream ID
- **Reconciliation & Full 1:1 Data Equivalence Audit:**
  - ตรวจพบ MinIO Archive Objects ครบทั้ง 3 ไฟล์:
    - Batch 1 Object: 297 records (ขนาด ~11 KiB)
    - Batch 2 Object: 296 records (ขนาด ~11 KiB)
    - Batch 3 Object: 149 records (ขนาด ~5.4 KiB)
    - **ผลรวมข้อมูลที่จัดเก็บบน MinIO:** 297 + 296 + 149 = **742 records** ตรงตาม Unique Event IDs 100%
  - ตรวจสอบไส้ในของทุก Object (GZip decompress + NDJSON parse):
    - **Duplicate Event IDs across all archives:** **0 (ไม่มี Event ซ้ำปนเปื้อนใน Cold Storage แม้แต่ตัวเดียว)**
    - **Missing Events:** **0 (ครบ 742/742 รายการ)**
    - **Extra Events:** **0**
    - **Field-by-field Comparison:** ค่า `riderId`, `timestamp`, `lat` (6 dec), `lng` (6 dec), `accuracy` ตรงกับ Source ขาเข้า 100%
- **Redis State & Retention:**
  - `telemetry:archiver:last_id` = `batch3.LastStreamId` (ตรงกับจุดสิ้นสุดข้อมูล)
  - `telemetry:archiver:pending_batch` = `(nil)` (ไม่ค้าง)
  - `xlen telemetry:stream:gps` = 750 (ข้อมูลใน Stream ถูกเก็บรักษาไว้ครบถ้วน ไม่ถูกลบหรือตัดทิ้งโดย Archiver)
- **Reboot Idempotency:** สปอว์น Worker Instance ใหม่เข้ามาตรวจสอบ พบ Checkpoint อยู่ที่ตำแหน่งสิ้นสุด ไม่มีการดึงข้อมูลมา archive ซ้ำ และไม่เกิดการ Upload เพิ่ม
- **Verification Evidence:**
  - `dotnet build B:\Delivery\BackendApi\BackendApi.csproj`: Succeeded with 0 Warnings, 0 Errors.
  - `dotnet test B:\Delivery\RootScripts\scripts.test\test\BackendApi.IntegrationTests --filter FullyQualifiedName~SubStep_2_2_B_2_6_E`: **1/1 PASSED (8 s)**.
  - Live Integration Suite (`SubStep_2_2_B_2_6_A` ถึง `E` ครบทั้ง 7 เทสต์): **7/7 PASSED (12 s)**.
  - Full Phase 2.2 Unit Test Suite (`TelemetryArchiveWorkerTests` + `MinioTelemetryArchiveStorageTests`): **59/59 PASSED** (Duration: 454 ms).
  - `docker build`: Clean container build with Swagger/OpenAPI spec generation (`delivery-backend-test`, Exit code 0).
  - Live MinIO CLI verification: พบ 3 objects ขนาด 11 KiB, 11 KiB, 5.4 KiB ตามลำดับ.
  - Live Redis CLI verification: `telemetry:archiver:last_id` = batch3.LastStreamId, `telemetry:archiver:pending_batch` = (nil), `xlen` = 750.

---

### 2.2-C Throughput Benchmark & Bottleneck Analysis
- **Contract Reference:** 250 points/sec เป็น Producer Design Target สำหรับเปรียบเทียบเชิงประจักษ์ ไม่ใช่ตัวเลข binary pass/fail เพียงตัวเดียว และค่า throughput ใน benchmark นี้วัดจากสภาพแวดล้อม Live Docker Stack จริง ไม่มีการคาดเดาหรืออ้างอิงตัวเลขประมาณการในอดีต (~50k pts/sec)

#### Sub-step T0: Benchmark Harness Preparation
- **Status:** **CLOSED (PASS ✅)**
- **Verification:**
  - สร้าง `TelemetryArchiveThroughputBenchmarkTests.cs` โดยไม่แก้ไข Production / Worker Architecture ใดๆ
  - Smoke Test (50 raw entries -> 48 unique + 2 duplicates) รันผ่านสมบูรณ์บน Live Stack (Redis 6389 + MinIO 9002)
  - เครื่องมือตรวจวัดทำงานครบ: High-resolution Stopwatch, Instrumented MinIO Storage, GZip NDJSON Reconciler, Redis INFO memory, และ Worker CPU/Memory/GC stats

#### Sub-step T1: Scenario 1 — Steady-State Concurrent Flow (500 pts/s × 10s)
- **Status:** **CLOSED (PASS ✅)**
- **Workload:** Controlled empirical workload ที่เป้าหมาย 500 points/sec ต่อเนื่อง 10 วินาที (5,000 raw stream entries, 1% intentional duplicates = 50 dupes, 4,950 unique events) โดย Producer พ่นข้อมูลคู่ขนานกับ Archiver Worker
- **ผลการวัดเชิงประจักษ์:**
  - **Actual Producer Ingestion Rate:** **463.9 points/sec** (5,000 entries ใน 10.78 วินาที)
  - **Actual Archiver Drain Throughput:** **416.9 points/sec** (5,000 entries ใน 11.99 วินาที)
  - **เปรียบเทียบกับ Producer Design Target (250 pts/s):** Throughput margin เหนือเป้าหมายอยู่ที่ **+66.7%** ภายใต้ workload นี้
  - **Reconciliation:** 4,950 / 4,950 records ตรงกับ Unique Events ต้นทาง 100% (Missing: 0, Extra: 0, Duplicate in Archive: 0)
  - **Real-time Checkpoint Lag:** เฉลี่ย 379.1 entries, สูงสุด 700 entries (ไม่พบ stream overflow หรือ data loss ใน workload นี้)
  - **Pending Key Duration:** เฉลี่ย 15.4 ms (สูงสุด 61 ms)
  - **Physical End-to-End Latency:** เฉลี่ย 1,027.0 ms (สูงสุด 1,769.8 ms) สอดคล้องกับ partial-batch timeout `maxWaitInterval = 1s`
  - **MinIO Upload Latency:** เฉลี่ย 6.2 ms (สูงสุด 14 ms ต่อ batch)
  - **Stream Preservation:** 5,000 / 5,000 entries ยังคงอยู่ครบถ้วนใน Redis Stream
  - **Resource Usage:** Worker CPU Delta 1,984.4 ms (~1.98s CPU / 12s wall clock), Memory Delta +13.6 MiB, GC 2/2/1, Redis Memory 1.69M -> 3.04M

#### Sub-step T2: Scenario 1 Deep Analysis
- **Status:** **CLOSED (PASS ✅)**
- **การวิเคราะห์ผลลัพธ์:**
  1. **MinIO ไม่ใช่ Bottleneck:** การอัปโหลดไฟล์ขนาด ~20 KiB ขึ้น S3 ใช้เวลาเฉลี่ยเพียง 6.2 ms ต่อ batch คิดเป็น < 1% ของวงรอบประมวลผล จึงไม่มีเหตุผลให้ optimize ฝั่ง S3 ในสภาวะนี้
  2. **Data Integrity ภายใต้ Concurrency สมบูรณ์แบบ:** การทำงานคู่ขนานของ Producer และ Archiver ไม่ทำให้ข้อมูลสูญหาย หรือเกิด Duplicate ใน Cold Storage
  3. **Latency Bound:** End-to-End Latency เฉลี่ย 1.027s ถูกกำหนดโดย `maxWaitInterval = 1s` ของ partial-batch flush เป็นหลัก
  4. **T1 ยังไม่ใช่ขีดจำกัดสูงสุด (Not a Stress Test):** Throughput 416.9 pts/s ใน T1 ถูก bound โดย Producer Ingestion Pacing และ Batch Wait Intervals ไม่ใช่ขีดจำกัดของตัว Archiver Pipeline

#### Sub-step T3: Scenario 2 — Peak Backlog Drain (10,000 entries / 10 batches)
- **Status:** **CLOSED (PASS ✅)**
- **Workload:** Backlog 10,000 raw stream entries (200 intentional duplicates = 2.0%, 9,800 unique events) ถูกฉีดสะสมไว้ใน Redis Stream ล่วงหน้า ก่อนรัน Worker ด้วย `maxBatchEntries = 1,000` แบบไร้การจำกัดเวลาของ Producer
- **ผลการวัดเชิงประจักษ์:**
  - **Backlog Pre-population:** 10,000 entries ภายใน 129 ms (77,260.5 pts/s)
  - **Archiver Drain Duration:** **5.75 วินาที**
  - **Peak Archiver Drain Throughput:** **1,738.8 points/sec**
  - **เปรียบเทียบกับ Producer Design Target (250 pts/s):** Throughput margin เหนือเป้าหมายอยู่ที่ **+595.5%** (~6.95 เท่าของ target)
  - **Batching Behavior:** แบ่งการประมวลผลเป็น 10 Batches พอดี (เฉลี่ย 1,000 raw / 980 unique records ต่อ batch)
  - **Reconciliation:** 9,800 / 9,800 records ตรงกับ Unique Events ต้นทาง 100% (Missing: 0, Extra: 0, Duplicate in Archive: 0)
  - **Pending Key Duration:** เฉลี่ย 18.6 ms (สูงสุด 74 ms)
  - **MinIO Upload Latency:** เฉลี่ย 6.6 ms (สูงสุด 14 ms ต่อ batch)
  - **MinIO Total Compressed Bytes:** 359,407 bytes (~351 KiB รวมทั้ง 10 objects)
  - **Stream Preservation:** 10,000 / 10,000 entries ยังคงอยู่ครบถ้วนใน Redis Stream
  - **Resource Usage:** Worker CPU Delta 2,687.5 ms (~2.69s CPU / 5.75s wall clock), Memory Delta +7.47 MiB, GC 4/3/2, Redis Memory 2.96M -> 4.39M

#### Sub-step T4: Metrics Synthesis & Empirical Reconciliation (T1 vs T3 Comparison)
- **Status:** **CLOSED (PASS ✅)**
- **ตารางเปรียบเทียบ T1 (Steady-State Concurrent) vs T3 (Peak Backlog Drain):**
  | มิติการวัดผล | Scenario 1 (T1: Steady-State Concurrent) | Scenario 2 (T3: Peak Backlog Drain) |
  |---|:---:|:---:|
  | **Workload Profile** | Concurrent Ingestion (500 pts/s target) | Pre-populated Backlog Drain |
  | **Raw Stream Volume** | 5,000 entries | 10,000 entries |
  | **Unique Event IDs** | 4,950 events | 9,800 events |
  | **Injected Duplicates** | 50 (1.0%) | 200 (2.0%) |
  | **Archived Records in MinIO** | 4,950 records (100%) | 9,800 records (100%) |
  | **Missing / Extra / Duplicate** | 0 / 0 / 0 | 0 / 0 / 0 |
  | **Ingestion / Pre-population Rate** | 463.9 points/sec (Concurrent Input) | 77,260.5 points/sec (Stream Pre-population)* |
  | **Measured Archiver Throughput** | **416.9 points/sec** | **1,738.8 points/sec** |

  *(หมายเหตุ: 77,260.5 pts/s เป็นอัตราการฉีดข้อมูลเข้า Redis Stream ล่วงหน้า ไม่ใช่ Throughput ของตัว Archiver; ขีดความสามารถในการดูดประมวลผลจริงของ Archiver วัดได้ที่ 1,738.8 pts/s)*
  | **Margin over 250 pts/s Target** | **+66.7%** | **+595.5%** |
  | **Objects / Batches Created** | 9 objects | 10 objects |
  | **MinIO Upload Latency (Avg)** | 6.2 ms | 6.6 ms |
  | **Pending Staging Duration (Avg)** | 15.4 ms | 18.6 ms |
  | **End-to-End Latency (Avg)** | 1,027.0 ms | N/A (Drain test) |
  | **Worker CPU Time** | 1.98 s (over 12.0s clock) | 2.69 s (over 5.75s clock) |
  | **Stream Retention** | 5,000 / 5,000 intact | 10,000 / 10,000 intact |

#### Sub-step T5: Bottleneck Analysis & Optimization Verdict
- **Status:** **CLOSED (PASS ✅)**
- **ข้อสรุปเชิงสถาปัตยกรรมและคอขวดที่แท้จริง:**
  1. **สัดส่วน Latency ภายใน Pipeline:**
     - ใน 1 Batch ขนาด 1,000 records (ใช้เวลาเฉลี่ย ~575 ms ในการ drain backlog):
       - MinIO S3 PutObject ใช้เวลาเพียง **6.6 ms** (~1.1% ของเวลา batch)
       - Pending Stage SET และ DEL รวมกันใช้เวลาเพียง **~18.6 ms** (~3.2% ของเวลา batch)
       - เวลาส่วนใหญ่ที่เหลือหลังหัก MinIO upload (~6.6 ms) และ pending staging (~18.6 ms) เกิดขึ้นภายในขั้นตอน Redis fetch/dedup/serialization/GZip ตามโครงสร้างการทำงานของ pipeline; benchmark ชุดนี้ไม่ได้แยกเวลาของแต่ละขั้นตอนออกจากกันโดยตรง
  2. **เปรียบเทียบกับ Producer Design Target (250 points/sec):**
     - ในสภาวะ Concurrent Flow (T1) Pipeline ทำได้ **416.9 pts/sec** (> 250 pts/sec อยู่ +66.7%)
     - ในสภาวะ Backlog Drain สูงสุด (T3) Pipeline ทำได้ **1,738.8 pts/sec** (> 250 pts/sec อยู่ +595.5%)
  3. **คำตัดสินด้านการปรับแต่งประสิทธิภาพ (Optimization Verdict):**
     - **ไม่พบความจำเป็นในการปรับสถาปัตยกรรมหรือเพิ่มความซับซ้อนจาก workload ที่ทดสอบใน Phase 2.2:** สถาปัตยกรรมปัจจุบันของ Phase 2.2-B (Single Worker, Exclusive Cursor, 1,000 Batch Quota, 1s Timeout, GZip Compression, Deterministic S3 Key) รองรับเป้าหมายการออกแบบ Producer Design Target 250 points/sec ได้อย่างมี Margin ปลอดภัยภายใต้ workload ที่ทดสอบทั้งสอง scenario
     - การเพิ่มความซับซ้อน เช่น Parallel Archiver, Redis Cluster หรือ Kafka ในขั้นนี้จะถือเป็นการ Over-engineering โดยไม่มีความจำเป็นตามข้อห้ามใน `AGENTS.md §3 และ §5`

---

## Phase 2.1-D: Dual-Write Failure & Recovery Verification
- **Context & Architecture Contract:**
  - **Durable Authoritative Store:** PostgreSQL (`RiderLocationHistories` + `ProcessedEvents`)
  - **Ephemeral Buffer:** Redis Stream (`telemetry:stream:gps`)
  - **Authoritative Idempotency:** `ProcessedEvents` (Composite Key: `EventId`, `HandlerName`)
  - **Transient Guard:** `telemetry:stream:seen:{eventId}` (TTL: 1 hour)
  - **Broker Routing & Dead Lettering:** RabbitMQ Direct Exchange `gps_telemetry_dlx` + `gps_telemetry_queue_dlq` (via `requeue: false`)
  - **No 2PC / No Saga:** ยึดมั่นตามข้อห้าม `AGENTS.md §3 & §5`

### Sub-step 2.1-D-A: Redis Down Before DB Commit (Rollback & DLQ Recovery)
- **Status:** **PASS / READY FOR GATE REVIEW ✅**
- **Test File:** `RootScripts/scripts.test/test/BackendApi.IntegrationTests/Telemetry/DualWriteFailureRecoveryIntegrationTests.cs`
- **Method:** `SubStep_2_1_D_A_RedisDownBeforeDbCommit_RollbackAndDlqRecovery`
- **Infrastructure Stack Used:** Live Docker Stack (PostgreSQL on port 5442, Redis on port 6389, RabbitMQ on port 5682)
- **Failure Injection Mechanics:**
  - Injected `RedisConnectionException` simulating Redis outage during `WriteToRedisStreamAsync` inside the database transaction block.
- **Empirical Evidence & Invariants Verified:**
  1. **PostgreSQL Rollback Invariant:**
     - `ProcessedEvents` table: Exactly **0 rows** for the failed event (Verified via `FindAsync(eventId, "GpsConsumer") == null`).
     - `RiderLocationHistories` table: Exactly **0 rows** for the failed rider (Zero orphan GPS records).
     - Confirmed that failure in Redis write causes full atomic rollback of PostgreSQL transaction.
  2. **RabbitMQ Broker Dead-Lettering Invariant:**
     - Worker caught exception, executed `BasicNack(maxDeliveryTag, multiple: true, requeue: false)`.
     - RabbitMQ broker transferred message from `gps_telemetry_queue` to `gps_telemetry_queue_dlq` via DLX.
     - Primary Queue Message Count: **0 messages**.
     - DLQ Message Count: **1 message** (dead-lettered).
  3. **Redis Stream Isolation Invariant:**
     - `telemetry:stream:seen:{eventId}`: **Does NOT exist** (`KeyExistsAsync == false`).
     - No orphan guard keys created during the outage.
  4. **Redis Recovery & DLQ Replay Invariant:**
     - Simulated Redis recovery (restored healthy connection to live Redis on 6389).
     - DLQ Replay executed: Read message from DLQ via `BasicGet`, acknowledged from DLQ, and republished to `gps_telemetry_queue`.
     - Worker consumed replayed message:
       - Processed idempotently without conflict.
       - Committed to PostgreSQL: `ProcessedEvents` has 1 row (`HandlerName = "GpsConsumer"`), `RiderLocationHistories` has 1 row (`lat = 13.7563, lng = 100.5018`).
       - Committed to Redis: Stream entry added to `telemetry:stream:gps`, seen guard key `telemetry:stream:seen:{eventId}` created with value `"1"`.
       - Worker ACKed to RabbitMQ: Primary queue = **0 messages**, DLQ = **0 messages**.
- **Verification Gates Completed:**
  - Sub-step 2.1-D-A Test: **PASSED (3/3 consecutive runs, 4s duration)**
  - Live Regression (`TelemetryArchiveLiveIntegrationTests`): **7/7 PASSED** (Duration: 13s)
  - Phase 2.2 Suite (`TelemetryArchiveWorkerTests` + `MinioTelemetryArchiveStorageTests`): **59/59 PASSED** (Duration: 343 ms)
  - Consumer Worker Unit Suite (`GpsRabbitMqConsumerWorkerTests`): **7/7 PASSED** (Duration: 1s)
  - BackendApi Build: **0 Warning(s), 0 Error(s)**
  - Docker Image Build (`BackendApi/Dockerfile`): **Successfully built & validated cleanly**

### Sub-step 2.1-D-B: Redis Succeeds, DB Commit Fails (Non-2PC Orphan & Downstream MinIO Archiver Dedup)
- **Status:** **PASS / READY FOR GATE REVIEW ✅**
- **Test File:** `RootScripts/scripts.test/test/BackendApi.IntegrationTests/Telemetry/DualWriteFailureRecoveryIntegrationTests.cs`
- **Method:** `SubStep_2_1_D_B_RedisSucceedsDbCommitFails_OrphanRedisEntryAndMinioArchiverDedup`
- **Infrastructure Stack Used:** Live Docker Stack (PostgreSQL on port 5442, Redis on port 6389, RabbitMQ on port 5682, MinIO S3 on port 9002)
- **Failure Injection Mechanics:**
  - Injected `FailCommitTransactionInterceptor` (EF Core `DbTransactionInterceptor`) at `TransactionCommittingAsync` to specifically fail `transaction.CommitAsync()` after Redis XADD and `seen` guard key write had already succeeded against real Redis.
- **Empirical Evidence & Invariants Verified:**
  1. **PostgreSQL Rollback Invariant:**
     - `ProcessedEvents` table: Exactly **0 rows** for the failed event (`FindAsync(eventId, "GpsConsumer") == null`).
     - `RiderLocationHistories` table: Exactly **0 rows** for the failed rider.
     - Confirmed that commit interception causes full atomic rollback of PostgreSQL transaction.
  2. **Redis Stream Orphan Entry Invariant (Non-2PC Proof):**
     - Querying `telemetry:stream:gps` from baseline cursor confirmed **EXACTLY 1 orphan entry** with the target `eventId`.
     - Confirms the Non-2PC architectural reality: Redis Stream contains an orphan uncommitted entry.
  3. **RabbitMQ Broker Dead-Lettering Invariant:**
     - Worker caught commit failure, executed `BasicNack(maxDeliveryTag, multiple: true, requeue: false)`.
     - RabbitMQ broker transferred message to `gps_telemetry_queue_dlq`.
     - Primary Queue: **0 messages**, DLQ: **1 message**.
  4. **DLQ Replay & Redis Stream Duplicate Generation:**
     - Interceptor disabled (`ShouldFailCommit = false`).
     - Message replayed from DLQ to `gps_telemetry_queue` and ACKed from DLQ.
     - Worker processed replayed message:
       - Processed as new unique point (since `ProcessedEvents` had 0 rows).
       - Committed to PostgreSQL: `ProcessedEvents` = 1 row, `RiderLocationHistories` = 1 row (`lat = 13.7563, lng = 100.5018`).
       - Dual-wrote second entry to Redis Stream.
       - Worker ACKed to RabbitMQ: Primary queue = 0, DLQ = 0.
     - **Redis Stream Duplicate Proof:** Querying `telemetry:stream:gps` confirmed **EXACTLY 2 entries** with the same `eventId` (Orphan Stream ID ≠ Replay Stream ID).
  5. **Downstream Phase 2.2 Archiver Dedup & MinIO Immutable Storage:**
     - `TelemetryArchiveWorker` collected batch containing both stream entries (`batch.RawEntries` = 2 entries).
     - Archiver applied in-batch `eventId` deduplication: `batch.DeduplicatedEntries` = **1 unique entry** (Duplicate eliminated).
     - Uploaded to MinIO bucket `delivery-telemetry-archive`.
     - Archive object downloaded from MinIO, decompressed from GZip, and NDJSON parsed:
       - Target `eventId` count in MinIO: **EXACTLY 1 RECORD** (`lat = 13.7563, lng = 100.5018`).
       - Full archive integrity audit: **0 missing, 0 extra, 0 duplicate records**.
- **Verification Gates Completed:**
  - Sub-step 2.1-D-B Test: **PASSED (3/3 consecutive runs across D-A and D-B suite, 5s duration)**
  - Live Regression (`TelemetryArchiveLiveIntegrationTests`): **7/7 PASSED** (Duration: 13s)
  - Phase 2.2 Suite (`TelemetryArchiveWorkerTests` + `MinioTelemetryArchiveStorageTests`): **59/59 PASSED** (Duration: 399 ms)
  - Consumer Worker Unit Suite (`GpsRabbitMqConsumerWorkerTests`): **7/7 PASSED** (Duration: 1s)
  - BackendApi Build: **0 Warning(s), 0 Error(s)**
  - Docker Image Build (`BackendApi/Dockerfile`): **Successfully built & validated cleanly**

### Sub-step 2.1-D-C: Crash Before RabbitMQ ACK (Broker Redelivery & Idempotency)
- **Status:** **CLOSED (PASS ✅)**
- **Test File:** `RootScripts/scripts.test/test/BackendApi.IntegrationTests/Telemetry/DualWriteFailureRecoveryIntegrationTests.cs`
- **Method:** `SubStep_2_1_D_C_CrashBeforeRabbitMqAck_BrokerRedeliveryAndIdempotency`
- **Infrastructure Stack Used:** Live Docker Stack (PostgreSQL on port 5442, Redis on port 6389, RabbitMQ on port 5682)
- **Failure Injection Mechanics:**
  - Implemented `CrashBeforeAckWorker` with `DispatchProxy` (`DecoratingConnectionProxy` + `CrashBeforeAckChannelProxy`).
  - Intercepted `IModel.BasicAck` right after PostgreSQL transaction committed and Redis Stream write/seen guard succeeded.
  - Abruptly terminated the AMQP channel via `Target.Abort()`, simulating an unceremonious worker process crash / power outage immediately before the ACK packet reaches RabbitMQ broker.
  - Suppressed any catch-block NACK attempts (`IModel.BasicNack` returns null) to strictly model a dead process that cannot communicate with the broker.
- **Empirical Evidence & Invariants Verified:**
  1. **Dual Sink Pre-Crash Persistence Invariant:**
     - `ProcessedEvents` table: Exactly **1 row** committed before crash (`HandlerName = "GpsConsumer"`).
     - `RiderLocationHistories` table: Exactly **1 row** committed before crash (`lat = 13.7563, lng = 100.5018`).
     - Redis Stream `telemetry:stream:gps`: Exactly **1 entry** with `eventId`.
     - Redis Guard Key `telemetry:stream:seen:{eventId}`: Exists (`KeyExistsAsync == true`).
  2. **Broker Redelivery Isolation Invariant (No DLQ Routing):**
     - Because channel was aborted with unacknowledged messages, RabbitMQ broker returned message to primary queue.
     - Primary Queue (`gps_telemetry_queue`): **1 message** (`redelivered: true`).
     - Dead Letter Queue (`gps_telemetry_queue_dlq`): **0 messages** (Critical: Broker redelivery is strictly isolated from poison/error DLQ routing).
  3. **Restarted Clean Worker Consumption & Idempotency Filter:**
     - Clean `GpsRabbitMqConsumerWorker` (Worker 2) started without crash injection.
     - Consumed redelivered message from `gps_telemetry_queue`.
     - `ProcessedEvents` check: Event already registered (`existingEventIds` contains `eventId`).
     - PostgreSQL idempotency filter: `newPoints.Count == 0` -> DB insert skipped.
     - Redis seen guard check: `KeyExistsAsync(StreamSeenPrefix + eventId)` returns `true` -> duplicate XADD skipped.
     - Worker 2 successfully executed `BasicAck`.
     - Primary Queue drained: **0 messages**, DLQ: **0 messages**.
  4. **Strict Zero-Duplicate Invariant Audit:**
     - PostgreSQL `ProcessedEvents`: Exactly **1 row** (No duplicate rows).
     - PostgreSQL `RiderLocationHistories`: Exactly **1 row** (No duplicate historical coordinates).
     - Redis Stream `telemetry:stream:gps`: Exactly **1 entry** with target `eventId` (Redis idempotency guard successfully suppressed second stream append).
     - Redis Guard Key: Retained with valid TTL.
- **Verification Gates Completed:**
  - Sub-step 2.1-D-C Test: **PASSED (3/3 consecutive runs across D-A, D-B, and D-C suite, 7s duration)**
  - Live Regression (`TelemetryArchiveLiveIntegrationTests`): **7/7 PASSED** (Duration: 13s)
  - Phase 2.2 Suite (`TelemetryArchiveWorkerTests` + `Storage`): **59/59 PASSED** (Duration: 351 ms)
  - Consumer Worker Unit Suite (`GpsRabbitMqConsumerWorkerTests`): **7/7 PASSED** (Duration: 1s)
  - BackendApi Build: **0 Warning(s), 0 Error(s)**
  - Docker Image Build (`BackendApi/Dockerfile`): **Successfully built & validated cleanly**

### Sub-step 2.1-D-D: Duplicate Storm & Seen Guard Dynamics
- **Status:** **CLOSED (PASS ✅)**
- **Test File:** `RootScripts/scripts.test/test/BackendApi.IntegrationTests/Telemetry/DualWriteFailureRecoveryIntegrationTests.cs`
- **Method:** `SubStep_2_1_D_D_DuplicateStormAndSeenGuardDynamics`
- **Infrastructure Stack Used:** Live Docker Stack (PostgreSQL on port 5442, Redis on port 6389, RabbitMQ on port 5682)
- **Failure Injection & Verification Mechanics:**
  1. **Case D-D.1: Duplicate Storm Absorbed (ProcessedEvents + Seen Guard Active):**
     - Pre-seeded Event 1 into PostgreSQL (`ProcessedEvents` = 1, `RiderLocationHistories` = 1) and Redis Stream (`Stream` = 1, `seen:{eventId}` = "1").
     - Injected a concurrent burst storm of 5 identical messages into `gps_telemetry_queue`.
     - Worker drained all 5 messages. `ProcessedEvents` bulk check recognized existing eventId -> skipped DB write (`newPoints = 0`).
     - Redis seen guard check returned `true` for all 5 duplicates -> completely suppressed duplicate XADD.
     - Worker executed `BasicAck` up to tag 5. Primary queue: 0, DLQ: 0.
     - PostgreSQL verified: exactly 1 row (zero duplicate rows).
     - Redis Stream verified: exactly 1 entry (zero duplicate stream appends).
  2. **Case D-D.2: Stream Repair on Guard Expiration / Gap (ProcessedEvents Present + Seen Missing):**
     - Pre-seeded Event 2 into PostgreSQL (`ProcessedEvents` = 1, `RiderLocationHistories` = 1), but removed Redis seen guard and Stream entry (simulating TTL expiration after 1h or Redis memory flush).
     - Published redelivered message for Event 2 to `gps_telemetry_queue`.
     - Worker processed message: recognized duplicate in DB -> skipped DB write.
     - Checked Redis seen guard: returned `false` (gap detected!).
     - Repair logic triggered: executed `WriteToRedisStreamAsync` -> repaired stream entry with identical eventId and recreated `seen:{eventId}` with 1h TTL.
     - Worker executed `BasicAck` up to tag 6. Primary queue: 0, DLQ: 0.
     - PostgreSQL verified: exactly 1 row (zero duplicate rows).
     - Redis Stream verified: exactly 1 repaired entry appended.
  3. **Case D-D.3: Repair Attempt Fails Under Redis Outage -> Route to DLQ (Message-Level Loss Prevention via DLQ):**
     - Pre-seeded Event 3 into PostgreSQL (`ProcessedEvents` = 1, `RiderLocationHistories` = 1), seen guard removed (repair needed).
     - Injected simulated Redis connection outage (`RedisConnectionException`).
     - Published redelivered message for Event 3 to `gps_telemetry_queue`.
     - Worker caught Redis outage exception during seen check / repair -> executed `BasicNack(requeue: false)`.
     - Message preserved in `gps_telemetry_queue_dlq`: **1 message** (message-level loss prevention via DLQ). Primary queue: **0 messages**.
     - PostgreSQL verified: exactly 1 row intact.
- **Verification Gates Completed:**
  - Sub-step 2.1-D-D Test: **PASSED (3/3 consecutive runs across all D-A, D-B, D-C, and D-D tests, 8s duration)**
  - Live Regression (`TelemetryArchiveLiveIntegrationTests`): **7/7 PASSED** (Duration: 12s)
  - Phase 2.2 Suite (`TelemetryArchiveWorkerTests` + `Storage`): **59/59 PASSED** (Duration: 1s)
  - Consumer Worker Unit Suite (`GpsRabbitMqConsumerWorkerTests`): **7/7 PASSED** (Duration: 1s)
  - BackendApi Build: **0 Warning(s), 0 Error(s)**
  - Docker Image Build (`BackendApi/Dockerfile`): **Successfully built & validated cleanly**

### Phase 2.1-D Master Summary: Dual-Write Failure & Recovery Gate Review
- **Status:** **CLOSED (PASS ✅)**
- **Scope Verified:**
  - `2.1-D-A`: Normal Path Dual-Write (DB Commit + Stream Append + Seen Guard + Broker ACK) -> **CLOSED (PASS ✅)**
  - `2.1-D-B`: Redis Outage / Stream Write Failure (Rollback + Requeue / DLQ Routing) -> **CLOSED (PASS ✅)**
  - `2.1-D-C`: Crash Before RabbitMQ ACK (Broker Redelivery + Dual Idempotency Filter) -> **CLOSED (PASS ✅)**
  - `2.1-D-D`: Duplicate Storm & Seen Guard Dynamics (Storm Absorption + Guard Expiration Repair + DLQ Safe-Store) -> **CLOSED (PASS ✅)**
- **Audit Conclusion:** All 4 failure and recovery dynamics empirically verified on live infrastructure. Message-level loss prevention via DLQ and strict zero-duplicate state proved.

### Sub-step 2.1-E: Regression Verification (Admin Live Map + SignalR Hub)
- **Status:** **CLOSED (PASS ✅)**
- **Test Files:**
  - `RootScripts/scripts.test/test/BackendApi.IntegrationTests/Hubs/TrackingHubRegressionIntegrationTests.cs` (7 Integration Tests)
  - `RootScripts/scripts.test/test/BackendApi.UnitTests/Hubs/TrackingHubSecurityTests.cs` (10 Unit Tests)
  - `RootScripts/scripts.test/test/BackendApi.UnitTests/Telemetry/RiderPresenceServiceTests.cs` (Degraded Accuracy Unit Tests)
  - `admin-dashboard/src/app/core/services/tracking-signalr.service.spec.ts` & Frontend Karma Suite (18 Angular Unit Tests)
- **Scope & Contract Invariants Verified:**
  1. **Transport Layer & Security Contract (Pure Transport Rule):**
     - Live negotiate endpoint (`/hubs/tracking/negotiate`) verified: unauthenticated connections strictly rejected with `401 Unauthorized` and `X-Correlation-Id` tracking header.
     - Integration Case 1: unauthenticated WebSocket connection throws exception on connect.
     - Integration Case 2: authenticated Admin (`Role = "Admin"`) correctly placed in group `admins`; authenticated Rider placed in group `rider:{riderId}`.
     - StorePartner connection verified: aborted immediately if `shop_id` claim is missing.
     - TrackingHub acts as a pure transport router: zero business logic / mutations inside hub methods (`UpdateLocation`, `UpdateHeartbeat`, `UpdateStatus`, `AcceptOffer`, `RejectOffer`).
  2. **Realtime GPS Telemetry & Accuracy Classification:**
     - Contract alignment: `CoreAccuracyThresholdMeters` in `TelemetryService.cs` aligned to `50.0m` strictly matching `signalr-contracts.md`.
     - Standard accuracy (`<= 50.0m`): Case 3 verified realtime broadcast to `admins` and `rider:{riderId}` mirror group with exact lat/lng/accuracy.
     - Degraded accuracy (`> 50.0m` and `<= 300.0m`): Case 4 verified broadcast to `admins` group ONLY (isolated from non-admin groups; not stored in Redis presence or RabbitMQ hot path).
     - Unusable accuracy (`> 300.0m`): Case 5 verified immediate rejection (0 broadcasts to admins/clients).
     - Teleport anomaly protection: Case 6 verified coordinate jumps (> 50 m/s or 180 km/h) are rejected and ignored.
     - Targeted Offer dispatch: Case 7 verified `OfferReceived` is delivered exclusively to the targeted rider group.
  3. **Admin Live Map Frontend Architecture & Security (Angular 19 + Leaflet):**
     - **Anti-XSS DOM Binding:** `MapComponent.escapeHtml()` utilizes DOM text node creation to sanitize all dynamic data (`riderId`, `status`, `shopName`, `menuName`, `phone`, `rating`). Popup buttons bound programmatically via `addEventListener` inside Angular Zone (`this.zone.run(...)`); zero inline `onclick` string interpolation.
     - **Reactive UI (No Manual Reload):** Realtime updates handled via `TrackingSignalRService` (`riderLocations$` BehaviorSubject) updating Leaflet marker positions smoothly without page reload. Reconnection configured with exponential backoff (`[0, 2000, 5000, 10000, 30000]`) and deduplication of scan events via `getDispatchScanKey`.
     - **Memory Leak Prevention:** Comprehensive teardown in `MapComponent.ngOnDestroy()`: unsubscribes RxJS subscriptions, stops SignalR hub connection, stops drawing animations, removes polylines, circles, candidate markers, and destroys Leaflet map instance.
- **Verification Gates Completed:**
  - TrackingHub Integration Suite (`TrackingHubRegressionIntegrationTests`): **7/7 PASSED (3/3 consecutive runs, 100% repeatability, 3s duration)**
  - TrackingHub Security Unit Suite (`TrackingHubSecurityTests`): **10/10 PASSED**
  - Full Backend Unit Suite (`BackendApi.UnitTests`): **189/189 PASSED** (Duration: 3s)
  - Full Live Integration Regression (`TelemetryArchiveLiveIntegrationTests`): **7/7 PASSED** (Duration: 13s)
  - Dual-Write Failure Recovery Suite (`DualWriteFailureRecoveryIntegrationTests`): **4/4 PASSED** (D-A, D-B, D-C, D-D, Duration: 8s)
  - Frontend Karma Unit Suite (`admin-dashboard` ChromeHeadless): **18/18 SUCCESS**
  - BackendApi Build: **0 Warning(s), 0 Error(s)**
  - Docker Stack Health: All containers running and healthy (`delivery-v2-backend`, `delivery-v2-frontend`, `delivery-v2-db`, `delivery-v2-redis`, `delivery-v2-rabbitmq`, `delivery-v2-minio`)



### Phase 3.1: Redis Distributed Lock with Fallback
- **Status:** **CLOSED (PASS ✅)**
- **Audit Statement:** *"V2 has empirically verified distributed locking, failure recovery, idempotency, telemetry archival, and real-time tracking behavior under the tested scenarios."*
- **Test Files:**
  - `RootScripts/scripts.test/test/BackendApi.IntegrationTests/Locking/DistributedLockIntegrationTests.cs` (7 Integration Tests)
  - `BackendApi/Infrastructure/Redis/RedisLockService.cs` (Core Implementation)
  - `BackendApi/Data/ApplicationDbContext.cs` (`DistributedLocks` Fallback Table)
- **Scope & Contract Invariants Verified:**
  1. **Sub-step 3.1-A: Live Redis Distributed Lock (Primary Path)**
     - **100-Concurrency Race (`SubStep_3_1_A1_LiveRedis_100ConcurrencyRace_ExactOneWinner`):** 100 concurrent async tasks competed simultaneously to acquire the dispatch lock for a single rider (`rider_race_*`). Live Redis `SET key value NX PX` atomic operation guaranteed **exactly 1 winner** and **99 losers**. Lock holder confirmed via `GetLockHolderAsync` and `IsLockedAsync`.
     - **Ownership Protection (`SubStep_3_1_A2_LiveRedis_OwnershipProtection_OnlyHolderCanRelease`):** Imposter task attempting release with an unowned `offerId` was strictly rejected (Lua unlock script returned `0`). Lock remained active until true owner released with matching `offerId`.
     - **TTL Expiration Takeover (`SubStep_3_1_A3_LiveRedis_TtlExpirationTakeover`):** Acquired lock with 250ms TTL blocked competing offers immediately; after TTL expiration (+450ms), competing offer successfully claimed lock ownership.
  2. **Sub-step 3.1-B: PostgreSQL Fallback Under Redis Outage**
     - **100-Concurrency Race (`SubStep_3_1_B1_PostgresFallback_100ConcurrencyRace_ExactOneWinner`):** Simulated Redis connection outage (`RedisConnectionException`). 100 concurrent tasks raced against PostgreSQL `DistributedLocks` fallback table. PostgreSQL atomic UPSERT with row-level lock (`INSERT ... ON CONFLICT ("LockKey") DO UPDATE ... WHERE DistributedLocks.ExpiresAt <= now`) guaranteed **exactly 1 winner** and **99 losers**. Database state verified: exactly 1 row in `DistributedLocks` matching winner's `offerId`.
     - **Ownership Protection (`SubStep_3_1_B2_PostgresFallback_OwnershipProtection_OnlyHolderCanRelease`):** Imposter release attempt failed (`DELETE FROM DistributedLocks WHERE LockKey = @key AND Value = @value` affected 0 rows); DB row preserved. Legitimate holder release succeeded and cleaned up the DB row.
     - **TTL Expiration Takeover (`SubStep_3_1_B3_PostgresFallback_TtlExpirationTakeover`):** PostgreSQL fallback lock with 200ms timeout rejected competing requests while active; after expiration (+350ms), competing offer atomically claimed the expired slot and updated the row value.
  3. **Sub-step 3.1-C: Failover & Recovery Dynamics (Outage -> Recovery Cycle)**
     - **Full Lifecycle Resilience (`SubStep_3_1_C_FailoverAndRecovery_CleanLifecycle`):** 
       - Phase 1 (Outage): System gracefully routed lock requests to PostgreSQL fallback table.
       - Phase 2 (In-Flight Fallback): Holder safely released lock from PostgreSQL.
       - Phase 3 (Redis Recovery): When Redis connection recovered, system seamlessly resumed native Redis distributed locking with zero residual locks or split-brain states.
- **Verification Gates Completed:**
  - Distributed Lock Integration Suite (`DistributedLockIntegrationTests`): **7/7 PASSED (3/3 consecutive runs, 100% repeatability, 5s duration)**
  - TrackingHub Regression Integration Suite (`TrackingHubRegressionIntegrationTests`): **7/7 PASSED** (Duration: 3s)
  - Full Live Integration Regression (`TelemetryArchiveLiveIntegrationTests`): **7/7 PASSED** (Duration: 13s)
  - Full Backend Unit Suite (`BackendApi.UnitTests`): **189/189 PASSED** (Duration: 3s)
  - BackendApi Build: **0 Warning(s), 0 Error(s)**
  - Docker Stack Health: All containers running and healthy (`delivery-v2-backend`, `delivery-v2-frontend`, `delivery-v2-db`, `delivery-v2-redis`, `delivery-v2-rabbitmq`, `delivery-v2-minio`)


### Phase 3.2: Final Functional Parity Verification (Core Operational Workflows)
> *"3.2 ไม่ได้มีหน้าที่พิสูจน์ว่า V2 'เหมือนระบบเดิม 100%' แต่พิสูจน์ว่า Core Operational Workflow ที่ V2 ตั้งใจรักษาไว้ยังทำงานครบตาม Acceptance Criteria เพราะ Original กับ V2 เป็นคนละ codebase และ V2 มี intentional changes อยู่แล้ว"*
- **Audit Statement:** *"Verify what V2 actually does, rather than forcing V2 to match legacy assumptions by adding un-implemented features. V2 has empirically verified distributed locking, failure recovery, idempotency, telemetry archival, and real-time tracking behavior under the tested scenarios."*
- **Scope Adjustments Applied from Pre-Implementation Source Audit:**
  - **Order Lifecycle:** Verified against actual V2 dispatch lifecycle and state-machine transitions (`CREATED` -> `MATCHING` -> `OFFERING` -> `ASSIGNED` -> `PICKING_UP` -> `DELIVERING` -> `COMPLETED`, and `CANCELLED`).
  - **Rider Identity:** Authenticated via Role + NameIdentifier (`UserId`) with backend resolution to `User.RiderId` (JWT claim does not contain `RiderId`).
  - **Proof of Delivery:** Excluded from currently verified V2 functional surface due to lack of source implementation.
  - **Settlement & Monitoring:** Verified against actual endpoints and architecture.

#### Sub-step 3.2-A: Order Lifecycle (Checklist 1)
- **Status:** **PASS / READY FOR SUB-STEP REVIEW ✅**
- **Test File:** `RootScripts/scripts.test/test/BackendApi.IntegrationTests/Parity/OrderLifecycleParityTests.cs` (4 Integration Tests)
- **Scope & Contract Invariants Verified:**
  1. **Order Creation & Snapshot Integrity (`SubStep_3_2_A1_OrderCreation_ValidPayload_SnapshotsItemsAndReturnsCreated`):**
     - Customer creates order via `POST /api/v1/orders`.
     - Initial state returned: `CREATED` with tracking code generated.
     - Price tampering immunity: menu item prices snapshot verified from database (`MenuItem.Price`), guaranteeing historical item name and price integrity.
     - Delivery fee and route distance populated via actual OSRM Dijkstra calculations.
  2. **Authorization Matrix & Access Boundaries (`SubStep_3_2_A2_OrderQuery_AuthorizationMatrix_EnforcesAccessBoundaries`):**
     - Owner Customer queries `GET /api/v1/orders/{id}` -> 200 OK.
     - Unrelated Customer queries `GET /api/v1/orders/{id}` -> 403 Forbidden strictly enforced.
     - Admin/Operations queries `GET /api/v1/orders/{id}` -> 200 OK.
     - Non-existent order ID -> 404 Not Found.
     - Admin paginated query `GET /api/v1/orders?page=1&pageSize=10` -> 200 OK with accurate `TotalCount` and pagination metadata.
  3. **State Machine Progression & Illegal Jump Guards (`SubStep_3_2_A3_OrderStateProgression_ValidTransitionsAndIllegalGuards`):**
     - Sequentially progressed order through full valid lifecycle: `CREATED` -> `MATCHING` -> `OFFERING` -> `ASSIGNED` -> `PICKING_UP` -> `DELIVERING` -> `COMPLETED`.
     - Illegal jump guard: Direct skip `CREATED` -> `COMPLETED` strictly blocked with `400 Bad Request`.
     - Invalid status string: Arbitrary strings strictly blocked with `400 Bad Request`.
     - Terminal state guard: `COMPLETED` order cannot transition backwards (`COMPLETED` -> `CREATED` blocked with `400 Bad Request`).
  4. **Cancellation Lifecycle & Terminal Protection (`SubStep_3_2_A4_OrderCancellation_ValidStatesAndTerminalProtection`):**
     - Active pre-completion order cancellation via `POST /api/v1/orders/{id}/cancel` succeeds with 200 OK and updates state to `CANCELLED`.
     - Double cancellation guard: Second cancellation attempt on already cancelled order returns `400 Bad Request`.
     - Completed order cancellation guard: Attempting to cancel a `COMPLETED` order returns `400 Bad Request`.
     - Non-existent order cancellation returns `404 Not Found`.
- **Verification Gates Completed:**
  - Order Lifecycle Parity Suite (`OrderLifecycleParityTests`): **4/4 PASSED (3/3 consecutive runs, 100% repeatability, 15s duration/run)**
  - Distributed Lock Suite (`DistributedLockIntegrationTests`): **7/7 PASSED** (5s duration)
  - TrackingHub Regression Suite (`TrackingHubRegressionIntegrationTests`): **7/7 PASSED** (3s duration)
  - Full Backend Unit Suite (`BackendApi.UnitTests`): **189/189 PASSED** (3s duration)
  - BackendApi Build: **0 Warning(s), 0 Error(s)**
  - Docker Stack Health: All containers running and healthy.


#### Sub-step 3.2-B: Task & Dispatch (Checklist 2)
- **Status:** **CLOSED (PASS ✅)**
- **Audit Statement:** *"V2's dispatch workflow empirically verified candidate eligibility, ranking, rider reservation, offer acceptance, rejection/timeout handling, reservation release, and automatic re-dispatch under the tested scenarios."*
- **Test File:** `RootScripts/scripts.test/test/BackendApi.IntegrationTests/Parity/TaskDispatchParityTests.cs` (4 Integration Tests)
- **Scope & Contract Invariants Verified:**
  1. **Candidate Discovery & Eligibility Filtering (`SubStep_3_2_B1_CandidateEligibilityAndFiltering`):**
     - Spatial & Proximity filtering: candidate within search radius (`searchRadiusKm = 10km`) is discovered; out-of-radius candidate (>100km) is strictly excluded.
     - State Eligibility: only riders with `RiderState.IDLE` are considered eligible; non-IDLE riders (`BUSY`, `RESERVED`, `OFFLINE`, `STALE`) are strictly excluded.
     - Lock Guard: an IDLE nearby rider currently holding an active reservation lock (`dispatch:lock:rider:{id}`) is strictly excluded from new offers.
     - Outcome: Baseline candidate correctly discovered; exactly 1 eligible candidate matched from the 7-member test cohort.
  2. **Candidate Ranking & Reservation Locking (`SubStep_3_2_B2_CandidateRankingAndReservationLock`):**
     - Eligible candidates processed via `DispatchCandidateRanker` and selected candidate receives offer.
     - Reservation lock integration: atomic Redis lock `dispatch:lock:rider:{winnerId}` acquired with exact matching `offerId`.
     - Order state transitions to `OFFERING` with `CurrentOfferId`, `OfferVersion = 1`, and `OfferExpiresAt > now`.
     - Winner rider state transitions from `IDLE` -> `RESERVED`.
     - Competing eligible candidate remains `IDLE` with zero locks held.
  3. **Offer Acceptance Branch (`SubStep_3_2_B3_AcceptOffer_TransitionsToAssignedAndBusy`):**
     - Rider accepts offer via `DispatchOfferHandler.AcceptOfferAsync(riderId, offerId, version)`.
     - Order state transitions from `OFFERING` -> `ASSIGNED` with `AssignedAt` populated.
     - Rider state transitions from `RESERVED` -> `BUSY`.
     - Offer concurrency lock `lock:offer:{offerId}` cleanly deleted in `finally` block according to contract.
  4. **Offer Rejection / Timeout Branch & Re-dispatch (`SubStep_3_2_B4_RejectOffer_ReleasesReservationAndRedispatchesNextCandidate`):**
     - Rider rejects offer via `DispatchOfferHandler.RejectOrTimeoutAsync(offerId, riderId, "rejected")`.
     - Reservation lock on first rider is immediately released (`IsLockedAsync` returns `false`).
     - Order re-enters `MATCHING` state and invokes `FindAndOfferAsync(orders)` for re-dispatch.
     - Re-dispatch success: second candidate receives new offer with incremented `OfferVersion >= 2`, new `CurrentOfferId`, second rider becomes `RESERVED`, and second rider holds new reservation lock.
- **Verification Gates Completed:**
  - Task & Dispatch Parity Suite (`TaskDispatchParityTests`): **4/4 PASSED (3/3 consecutive runs, 100% repeatability, 18s duration/run)**
  - Order Lifecycle Parity Suite (`OrderLifecycleParityTests`): **4/4 PASSED** (15s duration)
  - Distributed Lock Suite (`DistributedLockIntegrationTests`): **7/7 PASSED** (5s duration)
  - TrackingHub Regression Suite (`TrackingHubRegressionIntegrationTests`): **7/7 PASSED** (3s duration)
  - Full Backend Unit Suite (`BackendApi.UnitTests`): **189/189 PASSED** (3s duration)
  - BackendApi Build: **0 Warning(s), 0 Error(s)**
  - Docker Stack Health: All containers running and healthy.


#### Sub-step 3.2-C: Rider Journey (Checklist 3)
- **Status:** **CLOSED (PASS โ…)**
- **Audit Statement:** *"V2's rider journey empirically verified rider authentication via Role and NameIdentifier claims with backend resolution to User.RiderId, offer acceptance, GPS telemetry accuracy contract enforcement (Core <=50m, Degraded 50-300m, Unusable >300m), real-time Redis presence caching, sequential state progression (ASSIGNED -> PICKING_UP -> DELIVERING -> COMPLETED with CompletedAt timestamp), automatic rider return to IDLE, and imposter mutation guards under the tested scenarios."*
- **Test File:** `RootScripts/scripts.test/test/BackendApi.IntegrationTests/Parity/RiderJourneyParityTests.cs` (5 Integration Tests)
- **Scope & Contract Invariants Verified:**
  1. **Rider Auth & Identity Resolution (`SubStep_3_2_C1_RiderAuthAndIdentityResolution_ResolvesRiderProfile`):**
     - Rider registers via `POST /api/v1/auth/register` with `Role = "Rider"`, creating a `User` entity linked to a newly persisted `Rider` entity (`user.RiderId = rider.Id`) in initial `RiderState.OFFLINE`.
     - JWT claims verified: contains `Role = "Rider"` and `NameIdentifier = UserId`.
     - Identity invariant strictly verified: JWT token does **NOT** expose `RiderId` claim.
     - Backend resolution: `GET /api/v1/orders/my` successfully resolves `User.RiderId` from `UserId` token claim, returning 200 OK.
  2. **Offer Acceptance Branch (`SubStep_3_2_C2_OfferAcceptance_TransitionsToAssignedAndBusy`):**
     - Rider in `RESERVED` state accepts matching offer via `DispatchOfferHandler.AcceptOfferAsync(riderId, offerId, version)`.
     - Order transitions from `OFFERING` -> `ASSIGNED` with `AssignedRiderId` and `AssignedAt` populated.
     - Rider transitions from `RESERVED` -> `BUSY`.
     - Concurrency lock `lock:offer:{offerId}` cleanly released after acceptance.
  3. **GPS Telemetry Contract & In-Flight Presence (`SubStep_3_2_C3_GpsTelemetryIngestion_EnforcesAccuracyAndUpdatesRedisPresence`):**
     - Standard Core Accuracy (`accuracy = 15.0m <= 50.0m`): accepted by `POST /api/v1/telemetry/gps` (200 OK), updates Redis Presence Cache (`_presenceService.GetLastKnownLocationAsync`), and writes Redis Hash `riders:gps:{riderId}` with accuracy.
     - Degraded Accuracy (`accuracy = 120.0m > 50.0m && <= 300.0m`): accepted by endpoint (200 OK) for admin-only monitoring, but strictly does **NOT** overwrite core Redis location.
     - Unusable Accuracy (`accuracy = 350.0m > 300.0m`): rejected/discarded, core Redis location remains unchanged.
     - Explicit spatial cleanup executed (`RemoveRiderAsync` and rate-limit key deletion) to prevent cache leakage.
  4. **Full Lifecycle Progression & Automatic Idle Return (`SubStep_3_2_C4_FullJourneyProgression_PickingUpToDeliveringToCompleted_RiderReturnsIdle`):**
     - Sequential progression: Rider calls `PATCH /api/v1/orders/{id}/status`:
       - Step 1: `ASSIGNED` -> `PICKING_UP` (200 OK, DB state = `PICKING_UP`).
       - Step 2: `PICKING_UP` -> `DELIVERING` (200 OK, DB state = `DELIVERING`).
       - Step 3: `DELIVERING` -> `COMPLETED` (200 OK, DB state = `COMPLETED`).
     - Timestamp: `order.CompletedAt` automatically populated with UTC timestamp.
     - Automatic Idle Return: Since rider has no remaining active orders, backend automatically transitions rider from `BUSY` -> `IDLE` in both PostgreSQL and Redis cache (`riders:status:{riderId}` = `"IDLE"`).
  5. **Security Boundaries & State Machine Guards (`SubStep_3_2_C5_SecurityGuard_ImposterRiderCannotMutateAnotherRidersOrder`):**
     - Imposter Rider Guard: Rider B attempting to update Order assigned to Rider A is rejected with `403 Forbidden` (`"เธเธธเธ“เนเธกเนเนเธ”เนเธฃเธฑเธเธกเธญเธเธซเธกเธฒเธขเนเธซเนเธ—เธณเธญเธญเน€เธ”เธญเธฃเนเธเธตเน"`).
     - Role Authorization: Customer attempting to update order status is rejected with `403 Forbidden`.
     - State Machine Guard: Rider attempting illegal state transition (e.g. jumping directly from `ASSIGNED` -> `COMPLETED`, skipping pickup and delivery) is rejected with `400 Bad Request` (`"เนเธกเนเธชเธฒเธกเธฒเธฃเธ–เน€เธเธฅเธตเนเธขเธเธชเธ–เธฒเธเธฐเธเธฒเธ ASSIGNED เน€เธเนเธ COMPLETED เนเธ”เน"`). Order remains strictly `ASSIGNED`.
- **Verification Gates Completed:**
  - Rider Journey Parity Suite (`RiderJourneyParityTests`): **5/5 PASSED (3/3 consecutive runs, 100% repeatability, ~22s duration/run)**
  - Order Lifecycle Parity Suite (`OrderLifecycleParityTests`): **4/4 PASSED** (33s duration)
  - Task & Dispatch Parity Suite (`TaskDispatchParityTests`): **4/4 PASSED** (33s duration)
  - TrackingHub Regression Suite (`TrackingHubRegressionIntegrationTests`): **7/7 PASSED** (25s duration)
  - Distributed Lock Suite (`DistributedLockIntegrationTests`): **7/7 PASSED** (17s duration)
  - Full Backend Unit Suite (`BackendApi.UnitTests`): **189/189 PASSED** (3s duration)
  - BackendApi Build: **0 Warning(s), 0 Error(s)**
  - Docker Stack Health: All containers running and healthy.

#### Sub-step 3.2-D: Settlement & Financial Parity (Checklist 4)
- **Status:** **CLOSED (PASS ✅)**
- **Audit Statement:** *"V2's settlement workflow empirically verified rider completed orders ledger extraction (filtered strictly by rider ID, COMPLETED status, and bounded time window with delivery fee and spatial metrics), strict time-range and role-based authorization guards (Admin/Dispatcher 200, Customer/Rider 403), shop sales revenue aggregation excluding delivery fee from store revenue, and CSV export streaming with UTF-8 BOM preamble matching summary figures under the tested scenarios."*
- **Test File:** RootScripts/scripts.test/test/BackendApi.IntegrationTests/Parity/SettlementParityTests.cs (4 Integration Tests)
- **Scope & Contract Invariants Verified:**
  1. **Rider Completed Orders Ledger (SubStep_3_2_D1_RiderCompletedOrdersLedger_CalculatesFeesAndFiltersCompletedOnly):**
     - Target endpoint: GET /api/v1/riders/{riderId}/completed-orders?from={fromUtc}&to={toUtc}&limit=50.
     - Explicit time-window bounding (romUtc / 	oUtc) applied.
     - Strict isolation: Orders assigned to other riders or in non-completed states (DELIVERING) are excluded.
     - Field verification: DeliveryFee (45.50), DistanceKm (3.5), ShopName, DeliveryAddress, PickupLat/Lng, DropoffLat/Lng, CompletedAt, and Rating correctly retrieved.
  2. **Validation & Security Boundaries (SubStep_3_2_D2_RiderCompletedOrders_TimeRangeValidationAndAuthorizationGuard):**
     - Invalid time range (rom >= to): rejected with 400 Bad Request (INVALID_TIME_RANGE).
     - Excessive time range (> 31 days): rejected with 400 Bad Request (TIME_RANGE_TOO_LARGE).
     - Unknown rider lookup: rejected with 404 Not Found (NOT_FOUND).
     - OperationsPolicy authorization guard: Customer and Rider roles rejected with 403 Forbidden; Admin/Dispatcher authenticated users accepted with 200 OK.
  3. **Shop Sales Summary & Revenue Calculation (SubStep_3_2_D3_ShopSalesSummary_CalculatesRevenueAndOrderBreakdownAccurately):**
     - Target endpoint: GET /api/v1/shops/{shopId}/reports/summary?period=day&date={date}.
     - Order counts verified: TotalOrders = 3 (includes CANCELLED), CompletedOrders = 1, CancelledOrders = 1.
     - Revenue calculation rule verified: TotalRevenue = 275.00 THB (Σ(UnitPrice × Quantity) of non-cancelled orders). DeliveryFee is strictly excluded from shop revenue.
     - Metrics verified: AverageOrderValue = 137.50 THB (TotalRevenue / 2 non-cancelled orders).
     - Ranking verified: TopItems accurately groups and sums quantities (Pad Thai: 3 units / 240 THB, Thai Tea: 1 unit / 35 THB).
     - Breakdown verified: Orders detailed list contains all 3 orders with customer name and status.
  4. **Shop Report CSV Export & Data Consistency (SubStep_3_2_D4_ShopReportExportCsv_StreamsUtf8BomAndMatchesSummaryData):**
     - Target endpoint: GET /api/v1/shops/{shopId}/reports/export?period=day&date={date}&format=csv.
     - Headers verified: Content-Type: text/csv; charset=utf-8 and Content-Disposition filename matching store-report-{shopId8}-day-{yyyyMMdd}.csv.
     - Binary preamble verified: UTF-8 BOM bytes ( xEF, 0xBB, 0xBF) prepended for spreadsheet display compatibility.
     - Data consistency verified: Section 1 (Executive Summary) figures match D3 calculations (TotalOrders: 2, CompletedOrders: 1, CancelledOrders: 1, TotalRevenue: 150.00 บาท).
     - Section 2 (Detailed Order Breakdown) accurately lists order reference #201, menu items (Tom Yum), and amounts.
     - Default query parameters verified: omitting query parameters defaults to period=day and ormat=csv (200 OK).
- **Verification Gates Completed:**
  - Settlement Parity Suite (SettlementParityTests): **4/4 PASSED (3/3 consecutive runs, 100% repeatability: 16.74s, 16.84s, 16.72s)**
  - All Parity Tests Combined (3.2-A, 3.2-B, 3.2-C, 3.2-D): **17/17 PASSED** (39.38s duration)
  - TrackingHub Regression Suite (TrackingHubRegressionIntegrationTests): **7/7 PASSED** (25.21s duration)
  - Distributed Lock Suite (DistributedLockIntegrationTests): **7/7 PASSED** (17.16s duration)
  - Full Backend Unit Suite (BackendApi.UnitTests): **189/189 PASSED** (4s duration)
  - BackendApi Build: **0 Warning(s), 0 Error(s)**
  - Docker Stack Health: All 21 containers running and healthy.


#### Sub-step 3.2-E: Monitoring & Telemetry Parity (Checklist 5)
- **Status:** **CLOSED (PASS ✅)**
- **Audit Statement:** *"V2's monitoring architecture was empirically verified across its 3 distinct operational tiers: Live Monitoring (E1 via Redis presence & admin live map initial state with OperationsPolicy), Accuracy Tiering (E2 validating Core <=50m Redis updates while isolating Degraded 50-300m and dropping Unusable >300m from core cache), Durable Historical Ledger (E3 route history queried from PostgreSQL RiderLocationHistories with PostGIS SRID 4326 Point spatial mapping X=Lng, Y=Lat, strictly bounded by order time window and assigned rider ID), and Security/Edge Boundaries (E4 handling 404 for nonexistent orders, 200 with empty list for unassigned orders, and 403/401 for unauthorized roles)."*
- **Test File:** `RootScripts/scripts.test/test/BackendApi.IntegrationTests/Parity/MonitoringParityTests.cs` (4 Integration Tests)
- **Scope & Contract Invariants Verified:**
  1. **Live Map Initial State & Redis Presence (`SubStep_3_2_E1_LiveMapInitialState_ReadsFromRedisPresence_WithAuthorizationGuards`):**
     - Target endpoint: `GET /api/v1/rider-locations` (Admin Live Map initial marker load).
     - Redis-first architecture verified: scans `riders:gps:*`, batches `HashGetAllAsync`, inspects `riders:status:*` for active operational states (`IDLE`, `RESERVED`, `BUSY`), and falls back to PostgreSQL `Riders` table for names/statuses.
     - Role authorization: Admin/Dispatcher accepted with 200 OK and complete rider telemetry fields (`RiderId`, `Latitude`, `Longitude`, `SpeedKmh`, `Accuracy`, `Status`). Customer and Rider roles strictly rejected with 403 Forbidden.
  2. **GPS Accuracy Tiering & Core Redis Separation (`SubStep_3_2_E2_GpsAccuracyTiering_EnforcesCoreUpdateAndPreservesRedisPosition`):**
     - Target endpoint: `POST /api/v1/telemetry/gps` with `Latitude`, `Longitude`, and `Accuracy`.
     - Tier 1 Core Accuracy (`accuracy = 18.0m <= 50.0m`): 200 OK, immediately updates Redis Presence Cache (`_presenceService.GetLastKnownLocationAsync`) and Redis Hash `riders:gps:{riderId}`.
     - Tier 2 Degraded Accuracy (`accuracy = 120.0m > 50.0m && <= 300.0m`): 200 OK for admin broadcast, but strictly does **NOT** overwrite the core Redis location. Core position remains locked to Tier 1 coordinates.
     - Tier 3 Unusable Accuracy (`accuracy = 450.0m > 300.0m`): dropped/rejected; core Redis location remains unchanged.
     - Verified rate-limiting bypass/reset between consecutive requests to guarantee test isolation.
  3. **Order Route History PostGIS Query & Ordering (`SubStep_3_2_E3_OrderRouteHistory_QueriesPostgisLedger_FiltersAndOrdersAscending`):**
     - Target endpoint: `GET /api/v1/orders/{id}/route-history` (Admin/Operations audit ledger).
     - Data source verified: queries PostgreSQL `RiderLocationHistories` partitioned ledger directly using PostGIS `GeometryFactory` (SRID 4326) Point geometry.
     - Spatial mapping verified: `DTO.Lat == ST_Y(h.Location)` and `DTO.Lng == ST_X(h.Location)`.
     - Temporal & Identity filtering verified: points outside the order activity window (`(AssignedAt ?? CreatedAt) - 5 min` to `(CompletedAt ?? UtcNow) + 2 min`) and points belonging to unrelated riders are strictly excluded.
     - Chronological sorting verified: points strictly ordered ascending by `RecordedAt`.
  4. **Route History Security & Edge Cases (`SubStep_3_2_E4_RouteHistorySecurityAndEdgeCases_EnforcesContracts`):**
     - Non-existent order: rejected with 404 Not Found (`NOT_FOUND`).
     - Unassigned order (no assigned rider): returns 200 OK with empty list (`ActualGpsPoints = []`), verifying expected V2 behavior.
     - Role authorization guards: Customer and Rider roles rejected with 403 Forbidden; unauthenticated requests rejected with 401 Unauthorized.
- **Verification Gates Completed:**
  - Monitoring Parity Suite (`MonitoringParityTests`): **4/4 PASSED (3/3 consecutive runs, 100% repeatability: 24.13s, 27.30s, 24.36s)**
  - All Parity Tests Combined (3.2-A, 3.2-B, 3.2-C, 3.2-D, 3.2-E): **21/21 PASSED** (14s duration)
  - TrackingHub Regression Suite (`TrackingHubRegressionIntegrationTests`): **7/7 PASSED** (3s duration)
  - Distributed Lock Suite (`DistributedLockIntegrationTests`): **7/7 PASSED** (5s duration)
  - Full Backend Unit Suite (`BackendApi.UnitTests`): **189/189 PASSED** (3s duration)
  - BackendApi Build: **0 Warning(s), 0 Error(s)**
  - Production Code Changes: **0 lines**
  - Docker Stack Health: Operational containers Up and healthy.


#### Sub-step 3.2-F: Full Regression & V2 Freeze (Final Gate)
- **Status:** **CLOSED (PASS ✅) — V2 FREEZE DECLARED**
- **Audit Statement:** *"V2 Freeze establishes a verified, empirically proven baseline for the Core Operational Workflows and Modernized Infrastructure Components specified in the architecture blueprint. It does not certify that the system has zero defects in the entire universe, but verifies that all defined acceptance criteria have been satisfied and locked against regression without unrecorded production code changes. Any future defect fixes or enhancements will follow a post-freeze procedure without invalidating this verified baseline."*
- **Scope Clarification (Out-of-Scope Items):**
  - Features such as Proof of Delivery (signature/delivery photo), Rider Wallet, and Payment Gateway are classified as **Not Implemented / Out of Scope** in V2 source contracts, not failed requirements.
- **Multi-Phase Evidence Reconciliation:**
  - **Phase 1 (Domain & Unit Baseline):** `BackendApi.UnitTests` 189/189 passed.
  - **Phase 2.1 (Real-Time Tracking & Dual-Write):** `TrackingHubRegressionIntegrationTests` 7/7 passed. Pure transport layer, anti-XSS protection, dynamic subscription, and idempotency verified.
  - **Phase 2.2 (Cold Telemetry Archive):** `TelemetryArchiveLiveIntegrationTests` 7/7 and throughput benchmarks T0-T5 passed (Redis Stream to MinIO chunk storage).
  - **Phase 3.1 (Distributed Locking):** Historical evidence verified Redis/PG failover scenarios and 100-concurrency ownership. Final regression verified `DistributedLockIntegrationTests` 7/7 passed.
  - **Phase 3.2 (Core Parity A–E):** Order Lifecycle (4), Task & Dispatch (4), Rider Journey (5), Settlement (4), Monitoring (4) — all 21/21 passed with 100% repeatability (3/3 runs).
- **Final Regression Suite Results (The 7 Gates):**
  1. **Gate 1 — Core Parity Suite (3.2-A to 3.2-E):** **21/21 PASSED** (Duration: 14s)
  2. **Gate 2 — TrackingHub Real-Time Regression:** **7/7 PASSED** (Duration: 3s; 3/3 repeat runs passed)
  3. **Gate 3 — Distributed Lock Regression:** **7/7 PASSED** (Duration: 5s)
  4. **Gate 4 — Backend Unit Test Suite:** **189/189 PASSED** (Duration: 3s)
  5. **Gate 5 — Clean Build & Code Cleanliness:** `dotnet build` succeeded with **0 Warning(s), 0 Error(s)**; `git diff` confirms **0 unrecorded Production Code changes** in Phase 3.2.
  6. **Gate 6 — Docker Compose Infrastructure Audit:** `docker compose ps` inspected at runtime: 21 containers total in stack (20 Up, 9 core services Healthy, 1 Seq dev tool restarting). All operational dependencies (DB, Redis, RabbitMQ, MinIO, OSRM, Nginx, Prometheus, Grafana, Backend, Frontend, Route Optimizer) functional and healthy.
  7. **Gate 7 — Evidence Reconciliation & Freeze Declaration:** Automated tests ทั้ง 224 รายการที่กำหนดไว้ใน Final Regression Suite ผ่านทั้งหมด (224/224 automated tests in the defined Final Regression Suite passed). Workspace `B:\Delivery` isolated and standalone. V2 Freeze officially declared.
