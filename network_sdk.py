"""Adaptive LagWatch telemetry core. ICMP is a reference signal, not game RTT."""
from __future__ import annotations
import abc, os, socket, statistics, struct, time
from collections import deque
from dataclasses import asdict, dataclass
from enum import Enum
from typing import Optional

def checksum(data):
    if len(data)%2: data += b'\0'
    total=sum(struct.unpack('!%dH'%(len(data)//2),data)); total=(total>>16)+(total&65535); total+=total>>16
    return ~total&65535

class BaseTransport(abc.ABC):
    measurement_source='unknown'
    @abc.abstractmethod
    def send_probe(self): pass

class ICMPTransport(BaseTransport):
    measurement_source='icmp_reference'
    def __init__(self,target,timeout=1): self.target=target; self.timeout=timeout; self.identifier=os.getpid()&65535; self.sequence=0
    def send_probe(self):
        self.sequence=(self.sequence+1)&65535
        header=struct.pack('!BBHHH',8,0,0,self.identifier,self.sequence); payload=struct.pack('!d',time.time())
        packet=struct.pack('!BBHHH',8,0,checksum(header+payload),self.identifier,self.sequence)+payload
        sock=socket.socket(socket.AF_INET,socket.SOCK_RAW,socket.IPPROTO_ICMP); sock.settimeout(self.timeout); started=time.perf_counter()
        try:
            sock.sendto(packet,(self.target,0))
            while True:
                response,_=sock.recvfrom(2048); ihl=(response[0]&15)*4
                if len(response)>=ihl+8:
                    kind,_,_,ident,seq=struct.unpack('!BBHHH',response[ihl:ihl+8])
                    if kind==0 and ident==self.identifier and seq==self.sequence: return (time.perf_counter()-started)*1000,len(packet),len(response)
        except (socket.timeout,OSError): return None,len(packet),0
        finally: sock.close()

@dataclass
class AnalyzerConfig:
    sample_window:int=60; baseline_window:int=120; baseline_min_samples:int=8
    enter_inflation_ms:float=8; exit_inflation_ms:float=4; enter_jitter_ms:float=5; exit_jitter_ms:float=3
    min_utilization_pct:float=70; enter_samples:int=3; exit_samples:int=5; cooldown_seconds:float=10

class CongestionState(str,Enum):
    LEARNING='learning'; HEALTHY='healthy'; SUSPECTED='suspected'; CONGESTED='congested'; RECOVERING='recovering'

@dataclass
class NetworkSample:
    timestamp:float; measurement_source:str; target:str; rtt_ms:Optional[float]; avg_rtt_ms:float
    baseline_rtt_ms:Optional[float]; rtt_inflation_ms:Optional[float]; queue_delay_estimate_ms:Optional[float]
    jitter_ms:float; packet_loss_pct:float; loss_kind:str; in_bps:float; out_bps:float
    upload_utilization_pct:Optional[float]; download_utilization_pct:Optional[float]
    congestion_state:str; health_score:float; lag_spike:bool; gaming_flow:bool=False
    def to_dict(self):
        result=asdict(self); result.update(avg_rtt=self.avg_rtt_ms,jitter=self.jitter_ms,packet_loss=self.packet_loss_pct,in_bw=self.in_bps,out_bw=self.out_bps)
        return result

class NetworkMetrics:
    def __init__(self,config=None):
        self.config=config or AnalyzerConfig(); self.observations=deque(maxlen=self.config.sample_window); self.healthy_rtts=deque(maxlen=self.config.baseline_window)
    def record(self,rtt,healthy):
        self.observations.append(rtt)
        if rtt is not None and healthy: self.healthy_rtts.append(rtt)
    def values(self): return [v for v in self.observations if v is not None]
    def average(self): return statistics.fmean(self.values()) if self.values() else 0
    def baseline(self): return float(statistics.median(self.healthy_rtts)) if len(self.healthy_rtts)>=self.config.baseline_min_samples else None
    def jitter(self):
        values=self.values()
        return float(statistics.median(abs(b-a) for a,b in zip(values,values[1:]))) if len(values)>=3 else 0
    def probe_loss(self): return 100*sum(v is None for v in self.observations)/len(self.observations) if self.observations else 0

class InterfaceRateMonitor:
    def __init__(self): self.previous=None
    def sample(self):
        try:
            import psutil
            c=psutil.net_io_counters(); current=(time.monotonic(),c.bytes_recv,c.bytes_sent)
        except (ImportError,OSError): return 0,0
        if self.previous is None: self.previous=current; return 0,0
        elapsed=max(current[0]-self.previous[0],.001); result=(max(0,current[1]-self.previous[1])*8/elapsed,max(0,current[2]-self.previous[2])*8/elapsed); self.previous=current
        return result

class CongestionDetector:
    def __init__(self,config): self.config=config; self.state=CongestionState.LEARNING; self.bad=self.good=0; self.changed=0
    def update(self,inflation,jitter,upload_util=None,download_util=None):
        now=time.monotonic()
        if inflation is None: self.state=CongestionState.LEARNING; return self.state
        utils=[v for v in (upload_util,download_util) if v is not None]; loaded=bool(utils) and max(utils)>=self.config.min_utilization_pct
        bad=inflation>=self.config.enter_inflation_ms and (jitter>=self.config.enter_jitter_ms or loaded)
        good=inflation<=self.config.exit_inflation_ms and jitter<=self.config.exit_jitter_ms
        self.bad=self.bad+1 if bad else 0; self.good=self.good+1 if good else 0
        if self.state in (CongestionState.LEARNING,CongestionState.HEALTHY,CongestionState.SUSPECTED):
            if self.bad>=self.config.enter_samples: self.state=CongestionState.CONGESTED; self.changed=now
            else: self.state=CongestionState.SUSPECTED if bad else CongestionState.HEALTHY
        elif self.state==CongestionState.CONGESTED and self.good>=self.config.exit_samples and now-self.changed>=self.config.cooldown_seconds: self.state=CongestionState.RECOVERING; self.changed=now
        elif self.state==CongestionState.RECOVERING:
            self.state=CongestionState.CONGESTED if bad else (CongestionState.HEALTHY if self.good>=self.config.exit_samples else self.state)
        return self.state

class TrafficController:
    """Fail-open policy only; no shaping backend is claimed or applied."""
    def __init__(self,enabled=False): self.enabled=enabled; self.kill_switch=False; self.level=0
    def evaluate(self,state):
        if not self.enabled or self.kill_switch: self.level=0; return {'action':'monitor_only','level':0,'applied':False}
        if state==CongestionState.CONGESTED: self.level=min(5,self.level+1); return {'action':'request_background_pacing','level':self.level,'applied':False}
        if state in (CongestionState.HEALTHY,CongestionState.RECOVERING): self.level=max(0,self.level-1)
        return {'action':'hold','level':self.level,'applied':False}

class NetworkEngine:
    def __init__(self,transport,config=None,upload_capacity_bps=None,download_capacity_bps=None):
        self.transport=transport; self.config=config or AnalyzerConfig(); self.metrics=NetworkMetrics(self.config); self.detector=CongestionDetector(self.config); self.rate_monitor=InterfaceRateMonitor(); self.controller=TrafficController(); self.upload_capacity_bps=upload_capacity_bps; self.download_capacity_bps=download_capacity_bps; self.latest=None
    @staticmethod
    def utilization(rate,capacity): return None if not capacity or capacity<=0 else min(100,rate*100/capacity)
    def probe_once(self):
        oldbase=self.metrics.baseline(); rtt,_,_=self.transport.send_probe(); in_bps,out_bps=self.rate_monitor.sample(); oldinfl=None if rtt is None or oldbase is None else rtt-oldbase
        eligible=oldbase is None or (oldinfl is not None and oldinfl<=self.config.exit_inflation_ms and self.detector.state not in (CongestionState.CONGESTED,CongestionState.SUSPECTED))
        self.metrics.record(rtt,eligible); baseline=self.metrics.baseline(); inflation=None if rtt is None or baseline is None else rtt-baseline; jitter=self.metrics.jitter(); up=self.utilization(out_bps,self.upload_capacity_bps); down=self.utilization(in_bps,self.download_capacity_bps); state=self.detector.update(inflation,jitter,up,down); loss=self.metrics.probe_loss(); health=max(0,min(100,100-max(0,inflation or 0)*2-jitter*1.5-loss*1.5))
        self.latest=NetworkSample(time.time(),self.transport.measurement_source,getattr(self.transport,'target',''),rtt,self.metrics.average(),baseline,inflation,None if inflation is None else max(0,inflation),jitter,loss,'measured_active_probe_loss',in_bps,out_bps,up,down,state.value,health,state in (CongestionState.SUSPECTED,CongestionState.CONGESTED))
        return self.latest
    def get_status(self): return self.latest.to_dict() if self.latest else {}

# Compatibility alias for callers that imported the old analyzer.
LagAnalyzer=CongestionDetector
