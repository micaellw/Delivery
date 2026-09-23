path = 'B:/Delivery/summaryTest.md'
with open(path, 'r', encoding='utf-8') as f:
    text = f.read()

# 1. Update status of 2.1-E to CLOSED
text = text.replace(
    '### Sub-step 2.1-E: Regression Verification (Admin Live Map + SignalR Hub)\n- **Status:** **PASS / READY FOR GATE REVIEW \u2705**',
    '### Sub-step 2.1-E: Regression Verification (Admin Live Map + SignalR Hub)\n- **Status:** **CLOSED (PASS \u2705)**'
)

# 2. Update roadmap checklist
text = text.replace(
    '- [x] **2.1-E Regression Verification** (Admin Live Map, SignalR Hub, Leaflet Dynamic Subscription) (READY FOR GATE REVIEW \u2705)',
    '- [x] **2.1-E Regression Verification** (Admin Live Map, SignalR Hub, Leaflet Dynamic Subscription) (CLOSED \u2705)'
)

text = text.replace(
    '- [ ] **Phase 3.1: Redis Distributed Lock with Fallback** (Implementation, 100-Concurrency Integration, Failure Recovery)',
    '- [ ] **Phase 3.1: Redis Distributed Lock with Fallback** (Implementation, 100-Concurrency Integration, Failure Recovery) \U0001f504 IN PROGRESS'
)

with open(path, 'w', encoding='utf-8') as f:
    f.write(text)
print("Updated summaryTest.md for 2.1-E CLOSED and Phase 3.1 IN PROGRESS")
