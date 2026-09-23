#!/usr/bin/env python3
"""
Phase 2.1-C Step C-B: Data Equivalence Verification Test (N = 100)
Verifies:
1. Layer 1: Identity Correspondence (Redis Stream.eventId <-> ProcessedEvents.EventId)
2. Layer 2: Historical Data Correspondence (RiderLocationHistories <-> Redis Stream via Natural Key)
3. Recomputed EventId Formula verification
4. Per-event checks: RiderId, Timestamp, Lat, Lng, Recomputed EventId
"""

import urllib.request
import json
import base64
import datetime
import hashlib
import struct
import subprocess
import time
import sys

RABBIT_HTTP = "http://127.0.0.1:15682"
RABBIT_USER = "guest"
RABBIT_PASS = "guest"
QUEUE_NAME = "gps_telemetry_queue"
TEST_RIDER = "rider_c_equiv_01"
N = 100

def get_auth_header():
    token = base64.b64encode(f"{RABBIT_USER}:{RABBIT_PASS}".encode()).decode()
    return {"Authorization": f"Basic {token}", "Content-Type": "application/json"}

def publish_point(rider_id, lat, lng, timestamp_iso):
    url = f"{RABBIT_HTTP}/api/exchanges/%2F/amq.default/publish"
    payload = {
        "properties": {
            "delivery_mode": 2,
            "type": "TrackPoint"
        },
        "routing_key": QUEUE_NAME,
        "payload": json.dumps({
            "RiderId": rider_id,
            "Lat": lat,
            "Lng": lng,
            "Timestamp": timestamp_iso
        }),
        "payload_encoding": "string"
    }
    req = urllib.request.Request(url, data=json.dumps(payload).encode(), headers=get_auth_header(), method="POST")
    with urllib.request.urlopen(req) as resp:
        res_data = json.loads(resp.read().decode())
        if not res_data.get("routed", False):
            raise RuntimeError(f"Message was not routed: {res_data}")

def compute_dotnet_guid(rider_id, dt):
    # .NET epoch: 0001-01-01T00:00:00Z
    epoch = datetime.datetime(1, 1, 1, tzinfo=datetime.timezone.utc)
    ticks = int((dt - epoch).total_seconds() * 10_000_000)
    key = f"{rider_id}_{ticks}"
    h = hashlib.sha256(key.encode("utf-8")).digest()[:16]
    p1, p2, p3 = struct.unpack("<IHH", h[:8])
    p4 = h[8:10].hex()
    p5 = h[10:16].hex()
    return f"{p1:08x}-{p2:04x}-{p3:04x}-{p4}-{p5}", ticks

def query_postgres(sql):
    proc = subprocess.run(
        ["docker", "exec", "-i", "delivery-v2-db", "psql", "-U", "postgres", "-d", "delivery_db", "-t", "-A", "-F", "|"],
        input=sql.encode("utf-8"),
        capture_output=True
    )
    if proc.returncode != 0:
        raise RuntimeError(f"Postgres error: {proc.stderr.decode()}")
    return proc.stdout.decode("utf-8").strip().splitlines()

def query_redis_stream(rider_id):
    proc = subprocess.run(
        ["docker", "exec", "-i", "delivery-v2-redis", "redis-cli", "-a", "Password123!", "XRANGE", "telemetry:stream:gps", "-", "+"],
        capture_output=True
    )
    if proc.returncode != 0:
        raise RuntimeError(f"Redis error: {proc.stderr.decode()}")
    
    raw = proc.stdout.decode("utf-8").splitlines()
    entries = []
    i = 0
    while i < len(raw):
        line = raw[i].strip()
        if "-" in line and len(line) > 10 and i+1 < len(raw) and raw[i+1].strip() == "eventId":
            entry_id = line
            field_dict = {}
            j = i + 1
            while j < len(raw) and not ("-" in raw[j] and j+1 < len(raw) and raw[j+1].strip() == "eventId"):
                k = raw[j].strip()
                v = raw[j+1].strip() if j+1 < len(raw) else ""
                field_dict[k] = v
                j += 2
            if field_dict.get("riderId") == rider_id:
                entries.append((entry_id, field_dict))
            i = j
        else:
            i += 1
    return entries

def main():
    print("=" * 70)
    print("Phase 2.1-C Step C-B: Data Equivalence Test (N = 100)")
    print(f"Target Rider ID: {TEST_RIDER}")
    print("=" * 70)

    # 1. Prepare 100 distinct points
    base_dt = datetime.datetime(2026, 9, 21, 2, 0, 0, tzinfo=datetime.timezone.utc)
    points_plan = []

    for idx in range(N):
        dt = base_dt + datetime.timedelta(seconds=idx)
        timestamp_iso = dt.strftime("%Y-%m-%dT%H:%M:%S.0000000Z")
        lat = round(13.750000 + idx * 0.000500, 6)
        lng = round(100.500000 + idx * 0.000500, 6)
        expected_guid, ticks = compute_dotnet_guid(TEST_RIDER, dt)
        points_plan.append({
            "index": idx,
            "dt": dt,
            "timestamp_iso": timestamp_iso,
            "lat": lat,
            "lng": lng,
            "expected_guid": expected_guid,
            "ticks": ticks
        })

    # 2. Clean previous test data for this specific test rider across Postgres and Redis
    print("[1/5] Cleaning pre-existing test data for rider across Postgres and Redis...")
    expected_guid_list = "','".join(p["expected_guid"] for p in points_plan)
    clean_sql = f"""
    DELETE FROM "RiderLocationHistories" WHERE "RiderId" = '{TEST_RIDER}';
    DELETE FROM "ProcessedEvents" WHERE "HandlerName" = 'GpsConsumer' AND "EventId"::text IN ('{expected_guid_list}');
    """
    query_postgres(clean_sql)

    # Clean Redis seen keys for this test batch
    for p in points_plan:
        subprocess.run(
            ["docker", "exec", "-i", "delivery-v2-redis", "redis-cli", "-a", "Password123!", "DEL", f"telemetry:stream:seen:{p['expected_guid']}"],
            capture_output=True
        )

    print(f"[2/5] Generated {len(points_plan)} distinct GPS points planned for publishing.")
    print(f"      Point 0 : Lat={points_plan[0]['lat']}, Lng={points_plan[0]['lng']}, Time={points_plan[0]['timestamp_iso']}, ExpectedGUID={points_plan[0]['expected_guid']}")
    print(f"      Point 99: Lat={points_plan[99]['lat']}, Lng={points_plan[99]['lng']}, Time={points_plan[99]['timestamp_iso']}, ExpectedGUID={points_plan[99]['expected_guid']}")

    # 3. Publish to RabbitMQ
    print(f"[3/5] Publishing {N} points to RabbitMQ queue '{QUEUE_NAME}'...")
    start_publish = time.time()
    for p in points_plan:
        publish_point(TEST_RIDER, p["lat"], p["lng"], p["timestamp_iso"])
    publish_duration = time.time() - start_publish
    print(f"      Published {N} messages in {publish_duration:.2f}s.")

    # 4. Wait for consumer to process
    print("[4/5] Waiting for GpsRabbitMqConsumerWorker to process all points...")
    time.sleep(3.0)

    # 5. Fetch records from PostgreSQL and Redis
    print("[5/5] Fetching and verifying records across all three stores...")
    
    # ProcessedEvents
    pe_sql = f"""
    SELECT "EventId"::text, "HandlerName", "ProcessedAt"::text 
    FROM "ProcessedEvents" 
    WHERE "HandlerName" = 'GpsConsumer' AND "EventId"::text IN ('{expected_guid_list}');
    """
    pe_rows = query_postgres(pe_sql)
    pe_dict = {}
    for r in pe_rows:
        parts = r.split("|")
        if len(parts) >= 3:
            pe_dict[parts[0].lower()] = {"handler": parts[1], "processed_at": parts[2]}

    # RiderLocationHistories
    rlh_sql = f"""
    SELECT "Id", "RiderId", "RecordedAt"::text, ST_X("Location") AS lng, ST_Y("Location") AS lat 
    FROM "RiderLocationHistories" 
    WHERE "RiderId" = '{TEST_RIDER}' 
    ORDER BY "RecordedAt" ASC;
    """
    rlh_rows = query_postgres(rlh_sql)
    rlh_list = []
    for r in rlh_rows:
        parts = r.split("|")
        if len(parts) >= 5:
            rlh_list.append({
                "id": parts[0],
                "rider_id": parts[1],
                "recorded_at": parts[2],
                "lng": float(parts[3]),
                "lat": float(parts[4])
            })

    # Redis Stream entries
    redis_entries = query_redis_stream(TEST_RIDER)
    # Take the latest N entries for this rider
    redis_dict = {}
    for entry_id, fields in redis_entries[-N:]:
        ev_id = fields.get("eventId", "").lower()
        redis_dict[ev_id] = {
            "stream_entry_id": entry_id,
            "fields": fields
        }

    # Audit Counts
    count_pe = len(pe_dict)
    count_rlh = len(rlh_list)
    count_redis = len(redis_dict)

    print("\n" + "=" * 70)
    print("COUNT AUDIT SUMMARY:")
    print(f"- ProcessedEvents target=100       : actual = {count_pe}")
    print(f"- RiderLocationHistories target=100: actual = {count_rlh}")
    print(f"- Redis Stream target=100          : actual = {count_redis}")
    print("=" * 70)

    # Detailed Per-Event Equivalence Check
    pe_redis_matches = 0
    rider_id_matches = 0
    timestamp_matches = 0
    lat_lng_matches = 0
    recomputed_guid_matches = 0
    missing_points = 0
    extra_points = max(0, count_redis - N) + max(0, count_rlh - N) + max(0, count_pe - N)

    mismatches = []

    for p in points_plan:
        guid = p["expected_guid"].lower()
        idx = p["index"]
        
        # Check Layer 1: Redis eventId <-> ProcessedEvents.EventId
        has_pe = guid in pe_dict
        has_redis = guid in redis_dict
        
        if has_pe and has_redis:
            pe_redis_matches += 1
        else:
            missing_points += 1
            mismatches.append(f"Point {idx} (GUID={guid}) missing in: " + 
                              ("ProcessedEvents " if not has_pe else "") + 
                              ("Redis " if not has_redis else ""))
            continue

        redis_data = redis_dict[guid]["fields"]

        # Check RiderId
        if redis_data.get("riderId") == TEST_RIDER:
            rider_id_matches += 1
        else:
            mismatches.append(f"Point {idx} RiderId mismatch: expected {TEST_RIDER}, got {redis_data.get('riderId')}")

        # Check Timestamp in Redis (ISO format)
        redis_ts = redis_data.get("timestamp")
        if redis_ts == p["timestamp_iso"]:
            timestamp_matches += 1
        else:
            mismatches.append(f"Point {idx} Timestamp mismatch: expected {p['timestamp_iso']}, got {redis_ts}")

        # Check Lat/Lng in Redis
        r_lat = float(redis_data.get("lat", 0))
        r_lng = float(redis_data.get("lng", 0))
        if abs(r_lat - p["lat"]) < 1e-5 and abs(r_lng - p["lng"]) < 1e-5:
            lat_lng_matches += 1
        else:
            mismatches.append(f"Point {idx} Lat/Lng mismatch in Redis: expected ({p['lat']}, {p['lng']}), got ({r_lat}, {r_lng})")

        # Check Recomputed EventId
        recomputed, _ = compute_dotnet_guid(TEST_RIDER, p["dt"])
        if recomputed.lower() == guid:
            recomputed_guid_matches += 1
        else:
            mismatches.append(f"Point {idx} Recomputed GUID mismatch: expected {guid}, got {recomputed.lower()}")

    # Check Layer 2: RiderLocationHistories matches Redis & Plan
    rlh_layer2_matches = 0
    for idx, r in enumerate(rlh_list):
        if idx >= len(points_plan):
            break
        p = points_plan[idx]
        guid = p["expected_guid"].lower()
        redis_data = redis_dict.get(guid, {}).get("fields", {})

        lat_ok = abs(r["lat"] - p["lat"]) < 1e-5 and abs(r["lat"] - float(redis_data.get("lat", 0))) < 1e-5
        lng_ok = abs(r["lng"] - p["lng"]) < 1e-5 and abs(r["lng"] - float(redis_data.get("lng", 0))) < 1e-5
        rider_ok = (r["rider_id"] == TEST_RIDER and redis_data.get("riderId") == TEST_RIDER)
        
        # Check recomputed GUID from RLH RecordedAt
        pg_dt_str = r["recorded_at"].split("+")[0]
        pg_dt = datetime.datetime.fromisoformat(pg_dt_str).replace(tzinfo=datetime.timezone.utc)
        rlh_recomputed, _ = compute_dotnet_guid(r["rider_id"], pg_dt)
        guid_ok = (rlh_recomputed.lower() == guid)

        if lat_ok and lng_ok and rider_ok and guid_ok:
            rlh_layer2_matches += 1
        else:
            mismatches.append(f"RLH row {idx} mismatch: lat_ok={lat_ok}, lng_ok={lng_ok}, rider_ok={rider_ok}, guid_ok={guid_ok}")

    print("\nPER-EVENT AUDIT CHECKLIST (Target: 100/100):")
    print(f"- ProcessedEvents.EventId <-> Redis eventId: {pe_redis_matches} / {N}")
    print(f"- RiderId Match                            : {rider_id_matches} / {N}")
    print(f"- Timestamp Match                          : {timestamp_matches} / {N}")
    print(f"- Lat/Lng Match (Redis vs Expected)        : {lat_lng_matches} / {N}")
    print(f"- Recomputed EventId Match                 : {recomputed_guid_matches} / {N}")
    print(f"- Layer 2 History Data Tuple Correspondence: {rlh_layer2_matches} / {N}")
    print(f"- Missing Points                           : {missing_points}")
    print(f"- Extra Points                             : {extra_points}")

    # Check sample points for visual proof
    print("\nSAMPLE POINT PROOFS (Points 0, 49, 99):")
    sample_indices = [0, 49, 99]
    for si in sample_indices:
        if si < len(points_plan):
            p = points_plan[si]
            guid = p["expected_guid"].lower()
            r = rlh_list[si] if si < len(rlh_list) else None
            red = redis_dict.get(guid, {}).get("fields", {})
            print(f"--- Point {si} ---")
            print(f"  Plan        : EventId={guid} | Rider={TEST_RIDER} | Lat={p['lat']} | Lng={p['lng']} | Time={p['timestamp_iso']}")
            print(f"  Redis       : EventId={red.get('eventId')} | Rider={red.get('riderId')} | Lat={red.get('lat')} | Lng={red.get('lng')} | Time={red.get('timestamp')}")
            print(f"  ProcessedEv : EventId={guid} | Handler={pe_dict.get(guid, {}).get('handler')}")
            if r:
                print(f"  Postgres RLH: Id={r['id']} (Random UUID) | Rider={r['rider_id']} | Lat={r['lat']} | Lng={r['lng']} | RecordedAt={r['recorded_at']}")

    print("\n" + "=" * 70)
    all_passed = (
        count_pe == N and
        count_rlh == N and
        count_redis == N and
        pe_redis_matches == N and
        rider_id_matches == N and
        timestamp_matches == N and
        lat_lng_matches == N and
        recomputed_guid_matches == N and
        rlh_layer2_matches == N and
        missing_points == 0 and
        extra_points == 0
    )

    if all_passed:
        print("GATE C-B OVERALL STATUS: >>> PASS <<<")
        print("All 100 GPS points satisfy exact Identity & Data Tuple Equivalence across all stores!")
    else:
        print("GATE C-B OVERALL STATUS: >>> FAIL <<<")
        print(f"Total discrepancies found: {len(mismatches)}")
        for m in mismatches[:10]:
            print("  *", m)
    print("=" * 70)

    sys.exit(0 if all_passed else 1)

if __name__ == "__main__":
    main()
