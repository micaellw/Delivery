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
**Status:** Not Started

### 1.1 Whitelabel Parameterization
- **Implementation:** Pass APP_NAME, APPLICATION_ID, API_BASE_URL dynamically.
- **Integration Test:** Build APK with APP_NAME="Test Delivery", APPLICATION_ID="com.test.delivery", API_BASE_URL="https://test.example.com".
- **Failure Test (Negative):** Test build behavior when APPLICATION_ID is invalid, or fields are empty.
- **Docker Verification:** Verify the output .apk installs correctly and metadata (Label, Package ID) matches the injected values.
- **Rollback Point:** Revert to hardcoded values.

### 1.2 MinIO Media Hardening
- **Implementation:** Move credentials to .env. Migrate initialization to IHostedService.
- **Integration Test:** Clear bucket, restart API, upload image -> Success.
- **Failure / Recovery Test:** Start backend while MinIO is UNAVAILABLE. Verify if API crashes, retries, or degrades gracefully without blocking startup.
- **Rollback Point:** Move logic back to constructor.

### 1.3 ProcessedEvents Lifecycle (Idempotency GC)
- **Implementation:** Background Worker to delete events older than IDEMPOTENCY_RETENTION_DAYS.
- **Data Integrity Test (Boundaries):** 
  - Event A (now - 8 days) -> SHOULD DELETE
  - Event B (now - 6 days) -> SHOULD KEEP
  - Event C (now) -> SHOULD KEEP
- **Integration Test (Idempotency):**
  - Replay recent duplicate -> REJECT/SKIP.
  - Replay old event (outside retention) -> Behavior adheres to retention policy.
- **Rollback Point:** Delete the cleanup worker.

---

## Phase 2: Telemetry Redesign (Hot/Cold GPS)
**Status:** Not Started

### 2.1 Hot/Cold Dual-Write (Parallel Testing)
- **Implementation:** Consumer writes Latest to PostgreSQL, streams all to Redis.
- **Data Integrity Test (Identity Match):** Simulate N GPS points.
  - PostgreSQL History Count = N
  - Redis Stream Count = N
  - Data Equivalence: eventId, iderId, 	imestamp, lat/lng MUST perfectly match between PostgreSQL History and Redis Stream (Postgres ? Redis = N).
  - Latest Position = perfectly matches the final GPS event.
- **Regression Test:** Admin Map still tracks correctly.
- **Rollback Point:** Disable Redis stream insertion.

### 2.2 Archive Worker & Cut-over
- **Implementation:** Background Worker archives Redis Stream to MinIO.
- **Data Integrity Test:** Download MinIO archive, parse JSON, verify records count = N, and all unique eventIds match the input stream perfectly.
- **Failure / Recovery Test (No-Loss Guarantee):** 
  - Simulate MinIO Upload Failure -> Worker MUST NOT ACK the stream -> MUST Retry -> On Success -> ACK/Trim.
- **Rollback Point:** Disable Archive Worker.

---

## Phase 3: Lock Architecture
**Status:** Not Started

### 3.1 Redis Distributed Lock with Fallback
- **Implementation:** Redis-first lock with PostgreSQL fallback.
- **Integration Test:** 100 concurrent requests -> 1 owner, 99 rejected/waited.
- **Failure / Recovery Test (Concurrency Ownership):**
  - Scenario B: Redis DOWN *before* acquire -> PostgreSQL fallback handles 100 concurrent requests safely.
  - Scenario C: Redis DOWN *during* operation -> Verify lock ownership behavior. System must not create two owners if the lock provider switches mid-operation.
- **Rollback Point:** Revert to PostgreSQL-only locks.

---
*(Phase 4 Optimization: Paused)*
