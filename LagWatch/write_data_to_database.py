"""Normalized SQLite storage for samples, flows, events, and interventions."""
import json, sqlite3, time
from contextlib import contextmanager

class LagWatchDatabase:
    def __init__(self,path='lagwatch.db'): self.path=str(path); self.initialize()
    @contextmanager
    def connect(self):
        conn=sqlite3.connect(self.path,timeout=10); conn.execute('PRAGMA journal_mode=WAL'); conn.execute('PRAGMA foreign_keys=ON')
        try:
            yield conn
            conn.commit()
        except Exception:
            conn.rollback()
            raise
        finally:
            conn.close()
    def initialize(self):
        with self.connect() as c: c.executescript('''
        CREATE TABLE IF NOT EXISTS sessions(id INTEGER PRIMARY KEY,started_at REAL NOT NULL,ended_at REAL,target TEXT,measurement_source TEXT,notes TEXT);
        CREATE TABLE IF NOT EXISTS network_samples(id INTEGER PRIMARY KEY,session_id INTEGER NOT NULL,timestamp REAL NOT NULL,measurement_source TEXT NOT NULL,target TEXT,rtt_ms REAL,avg_rtt_ms REAL,baseline_rtt_ms REAL,rtt_inflation_ms REAL,queue_delay_estimate_ms REAL,jitter_ms REAL NOT NULL,packet_loss_pct REAL NOT NULL,loss_kind TEXT NOT NULL,in_bps REAL NOT NULL,out_bps REAL NOT NULL,upload_utilization_pct REAL,download_utilization_pct REAL,congestion_state TEXT NOT NULL,health_score REAL NOT NULL,gaming_flow INTEGER NOT NULL DEFAULT 0,FOREIGN KEY(session_id) REFERENCES sessions(id));
        CREATE INDEX IF NOT EXISTS ix_samples_session_time ON network_samples(session_id,timestamp);
        CREATE TABLE IF NOT EXISTS flows(id INTEGER PRIMARY KEY,session_id INTEGER NOT NULL,first_seen REAL,last_seen REAL,process_name TEXT,process_id INTEGER,protocol TEXT,src_ip TEXT,src_port INTEGER,dst_ip TEXT,dst_port INTEGER,direction TEXT,gaming_flow INTEGER NOT NULL DEFAULT 0,FOREIGN KEY(session_id) REFERENCES sessions(id));
        CREATE TABLE IF NOT EXISTS congestion_events(id INTEGER PRIMARY KEY,session_id INTEGER NOT NULL,started_at REAL,ended_at REAL,peak_inflation_ms REAL,peak_jitter_ms REAL,trigger TEXT,FOREIGN KEY(session_id) REFERENCES sessions(id));
        CREATE TABLE IF NOT EXISTS controller_actions(id INTEGER PRIMARY KEY,session_id INTEGER NOT NULL,timestamp REAL,action TEXT,target_flow TEXT,old_limit REAL,new_limit REAL,reason TEXT,rtt_before REAL,rtt_after REAL,jitter_before REAL,jitter_after REAL,throughput_before REAL,throughput_after REAL,applied INTEGER NOT NULL DEFAULT 0,details_json TEXT,FOREIGN KEY(session_id) REFERENCES sessions(id));''')
    def start_session(self,target,source,notes=None):
        with self.connect() as c: return c.execute('INSERT INTO sessions(started_at,target,measurement_source,notes) VALUES(?,?,?,?)',(time.time(),target,source,notes)).lastrowid
    def end_session(self,session_id):
        with self.connect() as c: c.execute('UPDATE sessions SET ended_at=? WHERE id=?',(time.time(),session_id))
    def write_sample(self,session_id,d):
        fields=('timestamp','measurement_source','target','rtt_ms','avg_rtt_ms','baseline_rtt_ms','rtt_inflation_ms','queue_delay_estimate_ms','jitter_ms','packet_loss_pct','loss_kind','in_bps','out_bps','upload_utilization_pct','download_utilization_pct','congestion_state','health_score','gaming_flow'); values=[d.get(x) for x in fields]; values[-1]=int(bool(values[-1]))
        with self.connect() as c: c.execute('INSERT INTO network_samples(session_id,'+','.join(fields)+') VALUES('+','.join('?' for _ in range(len(fields)+1))+')',[session_id]+values)
    def write_action(self,session_id,action):
        with self.connect() as c: c.execute('INSERT INTO controller_actions(session_id,timestamp,action,reason,applied,details_json) VALUES(?,?,?,?,?,?)',(session_id,time.time(),action.get('action'),action.get('reason'),int(bool(action.get('applied'))),json.dumps(action,sort_keys=True)))

_session=None
def write_metrics(data):
    global _session
    db=LagWatchDatabase()
    if _session is None: _session=db.start_session(data.get('target',''),data.get('measurement_source','unknown'),'compatibility API')
    db.write_sample(_session,data)
