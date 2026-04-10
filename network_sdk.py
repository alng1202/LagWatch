import socket
import time
import struct
import abc
import os
from collections import deque


ICMP_ECHO_REQUEST = 8
ICMP_ECHO_REPLY = 0


def checksum(data):
    if len(data) % 2:
        data += b'\x00'

    s = sum(struct.unpack("!%dH" % (len(data) // 2), data))
    s = (s >> 16) + (s & 0xffff)
    s += (s >> 16)
    return ~s & 0xffff


# -----------------------------
# Base Transport Interface
# -----------------------------
class BaseTransport(abc.ABC):
    @abc.abstractmethod
    def send_probe(self):
        '''
        Sends a probe and returns:
        (rtt_ms, bytes_sent, bytes_received)
        rtt_ms = None if timeout
        '''
        try:
            sock = socket.socket(socket.AF_INET, socket.SOCK_RAW, socket.IPPROTO_ICMP)
            sock.settimeout(self.timeout)
        except PermissionError:
            raise PermissionError("Raw sockets require root/admin privileges")

        # Increment sequence
        self.sequence += 1

        # Build ICMP header
        header = struct.pack("!BBHHH",
                             ICMP_ECHO_REQUEST,  # type
                             0,                  # code
                             0,                  # checksum placeholder
                             self.identifier,
                             self.sequence)
        # Add data (timestamp)
        data = struct.pack("!d", time.time())

        # Calculate checksum
        chksum = checksum(header + data)
        header = struct.pack("!BBHHH",
                             ICMP_ECHO_REQUEST,
                             0,
                             chksum,
                             self.identifier,
                             self.sequence)

        packet = header + data
        bytes_sent = len(packet)

        # Send packet
        start_time = time.time()
        sock.sendto(packet, (self.target, 1))

        # Wait for reply
        rtt_ms = None
        bytes_received = 0

        try:
            while True:
                recv_packet, addr = sock.recvfrom(1024)
                bytes_received = len(recv_packet)

                # Parse ICMP header (skip IP header)
                icmp_header = recv_packet[20:28]
                type_, code, recv_checksum, p_id, seq = struct.unpack("!BBHHH", icmp_header)

                if type_ == ICMP_ECHO_REPLY and p_id == self.identifier and seq == self.sequence:
                    end_time = time.time()
                    rtt_ms = (end_time - start_time) * 1000
                    break
        except socket.timeout:
            rtt_ms = None
            bytes_received = 0
        finally:
            sock.close()

        return rtt_ms, bytes_sent, bytes_received


# -----------------------------
# Example ICMP Transport
# -----------------------------
class ICMPTransport(BaseTransport):
    def __init__(self, target, timeout=1):
        self.target = target
        self.timeout = timeout
        self.identifier = os.getpid() & 0xFFFF

    def send_probe(self):
        try:
            sock = socket.socket(socket.AF_INET, socket.SOCK_RAW, socket.IPPROTO_ICMP)
            sock.settimeout(self.timeout)

            # Build ICMP header
            sequence = 1
            header = struct.pack("!BBHHH",
                                 ICMP_ECHO_REQUEST,  # type
                                 0,                  # code
                                 0,                  # checksum
                                 self.identifier,
                                 sequence)

            data = struct.pack("!d", time.time())
            chksum = checksum(header + data)

            header = struct.pack("!BBHHH",
                                 ICMP_ECHO_REQUEST,
                                 0,
                                 chksum,
                                 self.identifier,
                                 sequence)

            packet = header + data

            bytes_sent = len(packet)

            start = time.time()
            sock.sendto(packet, (self.target, 1))

            recv_packet, _ = sock.recvfrom(1024)
            end = time.time()

            bytes_received = len(recv_packet)

            rtt_ms = (end - start) * 1000

            sock.close()

            return rtt_ms, bytes_sent, bytes_received

        except socket.timeout:
            return None, 64, 0
        except Exception:
            return None, 0, 0


# -----------------------------
# Metrics Layer
# -----------------------------
class NetworkMetrics:
    def __init__(self, window_size=100):
        self.rtts = deque(maxlen=window_size)
        self.sent_packets = 0
        self.received_packets = 0

        self.bytes_sent = 0
        self.bytes_received = 0
        self.start_time = time.time()

    def record(self, rtt_ms, bytes_sent, bytes_received):
        self.sent_packets += 1
        self.bytes_sent += bytes_sent

        if rtt_ms is not None:
            self.received_packets += 1
            self.rtts.append(rtt_ms)
            self.bytes_received += bytes_received

    def avg_rtt(self):
        return sum(self.rtts) / len(self.rtts) if self.rtts else 0

    def jitter(self):
        if len(self.rtts) < 2:
            return 0
        diffs = [abs(self.rtts[i] - self.rtts[i - 1]) for i in range(1, len(self.rtts))]
        return sum(diffs) / len(diffs)

    def packet_loss(self):
        if self.sent_packets == 0:
            return 0
        return ((self.sent_packets - self.received_packets) / self.sent_packets) * 100

    def input_bandwidth(self):
        elapsed = time.time() - self.start_time
        return self.bytes_received / elapsed if elapsed else 0

    def output_bandwidth(self):
        elapsed = time.time() - self.start_time
        return self.bytes_sent / elapsed if elapsed else 0

    def reset(self):
        self.rtts.clear()
        self.sent_packets = 0
        self.received_packets = 0
        self.bytes_sent = 0
        self.bytes_received = 0
        self.start_time = time.time()


# -----------------------------
# Analytics Layer
# -----------------------------
from collections import deque

class LagAnalyzer:
    def __init__(self, metrics: NetworkMetrics):
        self.metrics = metrics

        # 🔥 SHORT WINDOW = fast reaction
        self.window = deque(maxlen=3)

        self.JITTER_THRESHOLD = 15
        self.LOSS_THRESHOLD = 5

    def health_score(self):
        rtt = self.metrics.avg_rtt()
        jitter = self.metrics.jitter()
        loss = self.metrics.packet_loss()

        score = 100
        score -= rtt * 0.2
        score -= jitter * 0.5
        score -= loss * 2

        return max(score, 0)

    def is_lag_spike(self):
        jitter = self.metrics.jitter()
        loss = self.metrics.packet_loss()

        is_bad = (
            jitter > self.JITTER_THRESHOLD or
            loss > self.LOSS_THRESHOLD
        )

        self.window.append(is_bad)

        # 🔥 SIMPLE RULE (no long memory)
        return sum(self.window) >= 2



# -----------------------------
# Engine
# -----------------------------
class NetworkEngine:
    def __init__(self, transport: BaseTransport):
        self.transport = transport
        self.metrics = NetworkMetrics()
        self.analyzer = LagAnalyzer(self.metrics)

    def probe_once(self):
        rtt, sent, received = self.transport.send_probe()
        self.metrics.record(rtt, sent, received)

    def get_status(self):
        return {
            "avg_rtt": self.metrics.avg_rtt(),
            "jitter": self.metrics.jitter(),
            "packet_loss": self.metrics.packet_loss(),
            "in_bw": self.metrics.input_bandwidth(),
            "out_bw": self.metrics.output_bandwidth(),
            "health_score": self.analyzer.health_score(),
            "lag_spike": self.analyzer.is_lag_spike()
        }

if __name__ == "__main__":
    from write_data_to_database import write_metrics

    transport = ICMPTransport("8.8.8.8")
    engine = NetworkEngine(transport)

    # for _ in range(5):
    while True:
        engine.probe_once()

        data = engine.get_status()
        write_metrics(data)

        print(data)
        time.sleep(1)