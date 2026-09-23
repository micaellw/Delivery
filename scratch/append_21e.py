path = 'B:/Delivery/summaryTest.md'
with open(path, 'r', encoding='utf-8') as f:
    text = f.read()

# 1. Update Roadmap checklist
old_check = '- [ ] **2.1-E Regression Verification** (Admin Live Map, SignalR Hub, Leaflet Dynamic Subscription)'
new_check = '- [x] **2.1-E Regression Verification** (Admin Live Map, SignalR Hub, Leaflet Dynamic Subscription) (READY FOR GATE REVIEW \u2705)'

if old_check in text:
    text = text.replace(old_check, new_check)
    print("Updated roadmap checklist")

# 2. Append 2.1-E section
section_21e = """

### Sub-step 2.1-E: Regression Verification (Admin Live Map + SignalR Hub)
- **Status:** **PASS / READY FOR GATE REVIEW \u2705**
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
"""

text = text.rstrip() + section_21e + '\n'
with open(path, 'w', encoding='utf-8') as f:
    f.write(text)
print("SUCCESS appending 2.1-E to summaryTest.md")
