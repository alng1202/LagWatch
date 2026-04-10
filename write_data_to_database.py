import sqlite3

def write_metrics(data: dict):
    conn = sqlite3.connect("lagwatch.db")  # ✅ thread-safe
    cursor = conn.cursor()

    cursor.execute("""
        INSERT INTO metrics (
            avg_rtt, jitter, packet_loss,
            in_bw, out_bw,
            health_score, lag_spike
        )
        VALUES (?, ?, ?, ?, ?, ?, ?)
    """, (
        data["avg_rtt"],
        data["jitter"],
        data["packet_loss"],
        data["in_bw"],
        data["out_bw"],
        data["health_score"],
        int(data["lag_spike"])
    ))

    conn.commit()
    conn.close()

    print("✅ DB write successful")