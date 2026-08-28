from flask import Flask, jsonify
import threading
import time
import sqlite3

from network_sdk import NetworkEngine, ICMPTransport
from write_data_to_database import LagWatchDatabase

app = Flask(__name__)


# --------------------------
# BACKGROUND LOOP (SDK + DB)
# --------------------------
def network_loop():
    transport = ICMPTransport("8.8.8.8")
    engine = NetworkEngine(transport)
    database = LagWatchDatabase()
    session_id = database.start_session(transport.target, transport.measurement_source)
    try:
        while True:
            data = engine.probe_once().to_dict()
            database.write_sample(session_id, data)
            print("Logged:", data)
            time.sleep(1)
    finally:
        database.end_session(session_id)


# --------------------------
# START THREAD ONCE
# --------------------------
def start_background_thread():
    thread = threading.Thread(target=network_loop, daemon=True)
    thread.start()


# --------------------------
# API ROUTE
# --------------------------
@app.route("/metrics")
def get_metrics():
    conn = sqlite3.connect("lagwatch.db")
    conn.row_factory = sqlite3.Row
    cursor = conn.cursor()

    cursor.execute("SELECT * FROM network_samples ORDER BY timestamp DESC LIMIT 10")
    rows = cursor.fetchall()
    conn.close()

    return jsonify([dict(row) for row in rows])

@app.route("/")
def dashboard():
    return """
<!DOCTYPE html>
<html>
<head>
    <title>LagWatch Dashboard</title>
    <style>
        body {
            font-family: Arial, sans-serif;
            background: #0f172a;
            color: white;
            margin: 0;
            padding: 20px;
        }

        h1 {
            text-align: center;
            margin-bottom: 30px;
        }

        .grid {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
            gap: 20px;
        }

        .card {
            background: #1e293b;
            padding: 20px;
            border-radius: 16px;
            box-shadow: 0 4px 20px rgba(0,0,0,0.3);
            text-align: center;
        }

        .label {
            font-size: 14px;
            color: #94a3b8;
        }

        .value {
            font-size: 28px;
            margin-top: 10px;
            font-weight: bold;
        }

        .good { color: #22c55e; }
        .warn { color: #facc15; }
        .bad  { color: #ef4444; }

    </style>
</head>
<body>

<h1>LagWatch Live Dashboard</h1>

<div class="grid">
    <div class="card">
        <div class="label">RTT (ms)</div>
        <div id="rtt" class="value">--</div>
    </div>

    <div class="card">
        <div class="label">Jitter</div>
        <div id="jitter" class="value">--</div>
    </div>

    <div class="card">
        <div class="label">Packet Loss (%)</div>
        <div id="loss" class="value">--</div>
    </div>

    <div class="card">
        <div class="label">Download BW</div>
        <div id="in_bw" class="value">--</div>
    </div>

    <div class="card">
        <div class="label">Upload BW</div>
        <div id="out_bw" class="value">--</div>
    </div>

    <div class="card">
        <div class="label">Health Score</div>
        <div id="health" class="value">--</div>
    </div>

    <div class="card">
        <div class="label">Lag Spike</div>
        <div id="lag" class="value">--</div>
    </div>
</div>

<script>
async function fetchData() {
    const res = await fetch('/metrics');
    const data = await res.json();

    if (!data.length) return;

    const m = data[0];

    document.getElementById("rtt").textContent = m.avg_rtt_ms.toFixed(2);
    document.getElementById("jitter").textContent = m.jitter_ms.toFixed(2);
    document.getElementById("loss").textContent = m.packet_loss_pct.toFixed(2);
    document.getElementById("in_bw").textContent = (m.in_bps / 1000000).toFixed(2) + " Mbps";
    document.getElementById("out_bw").textContent = (m.out_bps / 1000000).toFixed(2) + " Mbps";
    document.getElementById("health").textContent = m.health_score.toFixed(2);
    document.getElementById("lag").textContent = m.congestion_state.toUpperCase();

    // Color logic for health
    let healthEl = document.getElementById("health");
    healthEl.classList.remove("good", "warn", "bad");

    if (m.health_score > 80) healthEl.classList.add("good");
    else if (m.health_score > 50) healthEl.classList.add("warn");
    else healthEl.classList.add("bad");
}

setInterval(fetchData, 1000);
fetchData();
</script>

</body>
</html>
"""
# --------------------------
# RUN APP
# --------------------------
if __name__ == "__main__":
    LagWatchDatabase()
    start_background_thread()   # ✅ starts SDK + DB loop
    app.run(debug=False)
