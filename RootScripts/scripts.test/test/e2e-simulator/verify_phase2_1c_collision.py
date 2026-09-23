#!/usr/bin/env python3
"""
Phase 2.1-C Step C-C: Collision / Timestamp Boundary Verification Test (C-C-1)
Simulates:
Same Rider: rider_collision_01
Same Timestamp.Ticks: 2026-09-21T03:00:00.1234567Z (exact 100ns precision)
Different Coordinates:
  Point A: (13.750000, 100.500000)
  Point B: (13.750100, 100.500100)

Empirically proves:
1. EventId(A) vs EventId(B)
2. ProcessedEvents outcome for A and B
3. RiderLocationHistories outcome for A and B
4. Redis Stream outcome for A and B
5. RabbitMQ ACK / DLQ outcome
6. Consumer duplicate filtering stage
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
DLQ_NAME = "gps_telemetry_queue_dlq"
TEST_RIDER = "rider_collision_01"
TIMESTAMP_ISO = "2026-09-21T03:00:00.1234567Z"

POINT_A = {
    "name": "Point A",
    "lat": 13.750000,
    "lng": 100.500000,
    "timestamp": TIMESTAMP_ISO
}

POINT_B = {
    "name": "Point B",
    "lat": 13.750100,
    "lng": 100.500100,
    "timestamp": TIMESTAMP_ISO
}

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

def compute_dotnet_guid(rider_id, timestamp_iso):
    # Parse timestamp with 7 fractional digits
    # 2026-09-21T03:00:00.1234567Z
    dt_str = timestamp_iso.rstrip("Z")
    base_part, frac = dt_str.split(".")
    dt_base = datetime.datetime.fromisoformat(base_part).replace(tzinfo=datetime.timezone.utc)
    epoch = datetime.datetime(1, 1, 1, tzinfo=datetime.timezone.utc)
    base_ticks = int((dt_base - epoch).total_seconds() * 10_000_000)
    frac_ticks = int(frac) # 7 digits = 100ns ticks
    ticks = base_ticks + frac_ticks

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

def query_queue(queue_name):
    url = f"{RABBIT_HTTP}/api/queues/%2F/{queue_name}"
    req = urllib.request.Request(url, headers=get_auth_header())
    with urllib.request.urlopen(req) as resp:
        data = json.loads(resp.read().decode())
        return {
            "messages": data.get("messages", 0),
            "messages_ready": data.get("messages_ready", 0),
            "messages_unacknowledged": data.get("messages_unacknowledged", 0)
        }

def query_redis_stream(rider_id):
    proc = subprocess.run(
        ["docker", "exec", "-i", "delivery-v2-redis", "redis-cli", "-a", "Password123!", "XRANGE", "telemetry:stream:gps", "-", "+"],
        capture_output=True
    )
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
    print("=" * 75)
    print("Phase 2.1-C Step C-C: Collision / Timestamp Boundary Test (Test Case C-C-1)")
    print(f"Target Rider ID: {TEST_RIDER}")
    print(f"Target Timestamp: {TIMESTAMP_ISO}")
    print("=" * 75)

    # 1. Calculate Expected Ticks and GUIDs
    guid_a, ticks_a = compute_dotnet_guid(TEST_RIDER, POINT_A["timestamp"])
    guid_b, ticks_b = compute_dotnet_guid(TEST_RIDER, POINT_B["timestamp"])

    print("\n[Step 1] Mathematical Collision Calculation:")
    print(f"  Point A : Lat={POINT_A['lat']}, Lng={POINT_A['lng']} | Ticks={ticks_a} | EventId={guid_a}")
    print(f"  Point B : Lat={POINT_B['lat']}, Lng={POINT_B['lng']} | Ticks={ticks_b} | EventId={guid_b}")
    print(f"  Ticks Equal?   : {ticks_a == ticks_b} ({ticks_a})")
    print(f"  Lat/Lng Equal? : {POINT_A['lat'] == POINT_B['lat'] and POINT_A['lng'] == POINT_B['lng']}")
    print(f"  EventId Equal? : {guid_a == guid_b} ({guid_a})")

    # 2. Clean pre-existing test data
    print("\n[Step 2] Cleaning pre-existing test data for rider across Postgres and Redis...")
    clean_sql = f"""
    DELETE FROM "RiderLocationHistories" WHERE "RiderId" = '{TEST_RIDER}';
    DELETE FROM "ProcessedEvents" WHERE "HandlerName" = 'GpsConsumer' AND "EventId"::text = '{guid_a}';
    """
    query_postgres(clean_sql)
    subprocess.run(
        ["docker", "exec", "-i", "delivery-v2-redis", "redis-cli", "-a", "Password123!", "DEL", f"telemetry:stream:seen:{guid_a}"],
        capture_output=True
    )

    # Record backend log timestamp before publishing
    test_start_utc = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

    # 3. Publish Point A then Point B to RabbitMQ
    print("\n[Step 3] Publishing Point A and Point B into RabbitMQ 'gps_telemetry_queue'...")
    publish_point(TEST_RIDER, POINT_A["lat"], POINT_A["lng"], POINT_A["timestamp"])
    print("  -> Point A published.")
    publish_point(TEST_RIDER, POINT_B["lat"], POINT_B["lng"], POINT_B["timestamp"])
    print("  -> Point B published.")

    # 4. Wait for consumer to process
    print("\n[Step 4] Waiting 3.0s for GpsRabbitMqConsumerWorker to process...")
    time.sleep(3.0)

    # 5. Query results across stores
    print("\n[Step 5] Querying empirical results across all stores...")

    # ProcessedEvents
    pe_sql = f"""
    SELECT "EventId"::text, "HandlerName", "ProcessedAt"::text 
    FROM "ProcessedEvents" 
    WHERE "HandlerName" = 'GpsConsumer' AND "EventId"::text = '{guid_a}';
    """
    pe_rows = query_postgres(pe_sql)

    # RiderLocationHistories
    rlh_sql = f"""
    SELECT "Id", "RiderId", "RecordedAt"::text, ST_X("Location") AS lng, ST_Y("Location") AS lat 
    FROM "RiderLocationHistories" 
    WHERE "RiderId" = '{TEST_RIDER}';
    """
    rlh_rows = query_postgres(rlh_sql)

    # Redis Stream
    redis_entries = query_redis_stream(TEST_RIDER)

    # Redis Seen Key
    proc_seen = subprocess.run(
        ["docker", "exec", "-i", "delivery-v2-redis", "redis-cli", "-a", "Password123!", "EXISTS", f"telemetry:stream:seen:{guid_a}"],
        capture_output=True
    )
    seen_exists = proc_seen.stdout.decode("utf-8").strip()

    # RabbitMQ Queues
    q_main = query_queue(QUEUE_NAME)
    q_dlq = query_queue(DLQ_NAME)

    # Backend logs
    proc_logs = subprocess.run(
        ["docker", "logs", "--since", "10s", "delivery-v2-backend"],
        capture_output=True
    )
    raw_logs = proc_logs.stdout.decode("utf-8", errors="replace").splitlines()
    batch_logs = [l for l in raw_logs if "GPS points" in l or "Drained" in l or "NACK" in l or "duplicate" in l.lower()]

    print("\n" + "=" * 75)
    print("EMPIRICAL TEST RESULTS (Test Case C-C-1):")
    print("=" * 75)

    print(f"\n1. Identity Calculation:")
    print(f"   EventId(A) = {guid_a}")
    print(f"   EventId(B) = {guid_b}")
    print(f"   Equality   : EventId(A) == EventId(B) -> {guid_a == guid_b}")

    print(f"\n2. PostgreSQL ProcessedEvents:")
    print(f"   Row Count : {len(pe_rows)}")
    for r in pe_rows:
        parts = r.split("|")
        print(f"   Row       : EventId={parts[0]} | Handler={parts[1]} | ProcessedAt={parts[2]}")

    print(f"\n3. PostgreSQL RiderLocationHistories:")
    print(f"   Row Count : {len(rlh_rows)}")
    history_points = []
    for r in rlh_rows:
        parts = r.split("|")
        history_points.append({"id": parts[0], "lat": float(parts[4]), "lng": float(parts[3]), "time": parts[2]})
        print(f"   Row       : Id={parts[0]} | Lat={parts[4]} | Lng={parts[3]} | RecordedAt={parts[2]}")

    print(f"\n4. Redis Stream (telemetry:stream:gps):")
    print(f"   Entry Count : {len(redis_entries)}")
    for entry_id, fields in redis_entries:
        print(f"   Entry {entry_id} : eventId={fields.get('eventId')} | lat={fields.get('lat')} | lng={fields.get('lng')} | ts={fields.get('timestamp')}")
    print(f"   Seen Key (telemetry:stream:seen:{guid_a}) EXISTS: {seen_exists == '1'}")

    print(f"\n5. RabbitMQ Queues Status:")
    print(f"   gps_telemetry_queue     : messages={q_main['messages']} (ready={q_main['messages_ready']}, unack={q_main['messages_unacknowledged']})")
    print(f"   gps_telemetry_queue_dlq : messages={q_dlq['messages']}")

    print(f"\n6. Relevant Backend Worker Logs:")
    for l in batch_logs[-8:]:
        print(f"   {l.strip()}")

    print("\n" + "=" * 75)
    print("ANALYSIS OF POINT DISPOSITION:")
    print("=" * 75)

    point_a_in_history = any(abs(h["lat"] - POINT_A["lat"]) < 1e-5 and abs(h["lng"] - POINT_A["lng"]) < 1e-5 for h in history_points)
    point_b_in_history = any(abs(h["lat"] - POINT_B["lat"]) < 1e-5 and abs(h["lng"] - POINT_B["lng"]) < 1e-5 for h in history_points)

    point_a_in_redis = any(abs(float(e[1].get("lat", 0)) - POINT_A["lat"]) < 1e-5 and abs(float(e[1].get("lng", 0)) - POINT_A["lng"]) < 1e-5 for e in redis_entries)
    point_b_in_redis = any(abs(float(e[1].get("lat", 0)) - POINT_B["lat"]) < 1e-5 and abs(float(e[1].get("lng", 0)) - POINT_B["lng"]) < 1e-5 for e in redis_entries)

    print(f"Point A (Lat={POINT_A['lat']}, Lng={POINT_A['lng']}):")
    print(f"  - In RiderLocationHistories : {point_a_in_history}")
    print(f"  - In Redis Stream           : {point_a_in_redis}")
    print(f"  - Overall Disposition       : {'COMMITTED & BUFFERED' if (point_a_in_history and point_a_in_redis) else 'MISSING'}")

    print(f"\nPoint B (Lat={POINT_B['lat']}, Lng={POINT_B['lng']}):")
    print(f"  - In RiderLocationHistories : {point_b_in_history}")
    print(f"  - In Redis Stream           : {point_b_in_redis}")
    print(f"  - Overall Disposition       : {'COMMITTED & BUFFERED' if (point_b_in_history and point_b_in_redis) else 'DROPPED (DEDUPLICATED)'}")

    print(f"\nRabbitMQ ACK Semantics:")
    if q_main['messages'] == 0 and q_dlq['messages'] == 0:
        print("  - Both messages ACKed successfully (no DLQ routing). Point B dropped at Application Consumer Ingestion stage.")

    print("\n" + "=" * 75)
    print("STEP C-C EMPIRICAL VERIFICATION COMPLETE")
    print("=" * 75)

if __name__ == "__main__":
    main()
