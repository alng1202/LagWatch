import tempfile, unittest
from pathlib import Path
from network_sdk import *
from write_data_to_database import LagWatchDatabase

class FakeTransport(BaseTransport):
    measurement_source='test_probe'; target='test'
    def __init__(self,values): self.values=iter(values)
    def send_probe(self): return next(self.values),16,16

class Tests(unittest.TestCase):
    def test_baseline_resists_congestion(self):
        e=NetworkEngine(FakeTransport([13,14,13,14,13,35,36,37]),AnalyzerConfig(baseline_min_samples=5,baseline_window=20)); s=[e.probe_once() for _ in range(8)]
        self.assertEqual(13,s[-1].baseline_rtt_ms); self.assertGreater(s[-1].rtt_inflation_ms,20)
    def test_robust_jitter(self):
        m=NetworkMetrics(); [m.record(v,True) for v in [10,11,10,100,10,11,10]]; self.assertEqual(1,m.jitter())
    def test_loss_label(self):
        e=NetworkEngine(FakeTransport([10,None,10])); s=[e.probe_once() for _ in range(3)][-1]; self.assertEqual('measured_active_probe_loss',s.loss_kind); self.assertAlmostEqual(100/3,s.packet_loss_pct)
    def test_hysteresis(self):
        d=CongestionDetector(AnalyzerConfig(enter_samples=3,cooldown_seconds=0)); self.assertNotEqual(CongestionState.CONGESTED,d.update(12,6,90)); self.assertNotEqual(CongestionState.CONGESTED,d.update(12,6,90)); self.assertEqual(CongestionState.CONGESTED,d.update(12,6,90))
    def test_controller_fail_open(self): self.assertFalse(TrafficController(True).evaluate(CongestionState.CONGESTED)['applied'])
    def test_database(self):
        with tempfile.TemporaryDirectory() as p:
            db=LagWatchDatabase(Path(p)/'test.db'); sid=db.start_session('x','test'); db.write_sample(sid,NetworkEngine(FakeTransport([12])).probe_once().to_dict())
            with db.connect() as c: self.assertEqual(1,c.execute('SELECT count(*) FROM network_samples').fetchone()[0])
if __name__=='__main__': unittest.main()
