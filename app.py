from flask import Flask, jsonify
import threading
import time
import sqlite3

from network_sdk import NetworkEngine, ICMPTransport
from write_data_to_database import write_metrics

app = Flask(__name__)


# --------------------------
# BACKGROUND LOOP (SDK + DB)
# --------------------------
def network_loop():
    transport = ICMPTransport("8.8.8.8")
    engine = NetworkEngine(transport)

    while True:
        engine.probe_once()
        data = engine.get_status()

        write_metrics(data)   # ✅ write to DB

        print("Logged:", data)
        time.sleep(1)


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

    cursor.execute("SELECT * FROM metrics ORDER BY timestamp DESC LIMIT 10")
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

    document.getElementById("rtt").textContent = m.avg_rtt.toFixed(2);
    document.getElementById("jitter").textContent = m.jitter.toFixed(2);
    document.getElementById("loss").textContent = m.packet_loss.toFixed(2);
    document.getElementById("in_bw").textContent = m.in_bw.toFixed(2);
    document.getElementById("out_bw").textContent = m.out_bw.toFixed(2);
    document.getElementById("health").textContent = m.health_score.toFixed(2);
    document.getElementById("lag").textContent = m.lag_spike ? "YES ⚠️" : "NO ✅";

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

    conn = sqlite3.connect("lagwatch.db")
    cursor = conn.cursor()

    cursor.execute("""
    CREATE TABLE IF NOT EXISTS metrics (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        timestamp DATETIME DEFAULT CURRENT_TIMESTAMP,
        avg_rtt REAL,
        jitter REAL,
        packet_loss REAL,
        in_bw REAL,
        out_bw REAL,
        health_score REAL,
        lag_spike INTEGER
    )
    """)

    conn.commit()
    conn.close()
    start_background_thread()   # ✅ starts SDK + DB loop
    app.run(debug=False)