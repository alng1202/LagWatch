import socket
import struct
import time
import os
import select
from datetime import datetime

ICMP_ECHO_REQUEST = 8
TARGET = "210.143.117.170"     # Single target
INTERVAL = 1          # Seconds between pings
TIMEOUT = 1           # Timeout in seconds

def checksum(data):
    sum = 0
    for i in range(0, len(data), 2):
        if i + 1 < len(data):
            sum += (data[i] << 8) + data[i+1]
        else:
            sum += (data[i] << 8)
    sum = (sum >> 16) + (sum & 0xffff)
    sum += (sum >> 16)
    return ~sum & 0xffff

def create_packet(pid):
    header = struct.pack('bbHHh', ICMP_ECHO_REQUEST, 0, 0, pid, 1)
    data = struct.pack('d', time.time())
    chksum = checksum(header + data)
    header = struct.pack('bbHHh', ICMP_ECHO_REQUEST, 0, socket.htons(chksum), pid, 1)
    return header + data

def log(timestamp, rtt=None, success=False):
    if success:
        print(f"[{timestamp}] SUCCESS | RTT: {rtt:.2f} ms")
    else:
        print(f"[{timestamp}] FAILURE | Request timed out")

def ping():
    try:
        sock = socket.socket(socket.AF_INET, socket.SOCK_RAW, socket.IPPROTO_ICMP)
    except PermissionError:
        print("Run as administrator/root.")
        return

    pid = os.getpid() & 0xFFFF

    while True:
        packet = create_packet(pid)
        send_time = time.time()
        sock.sendto(packet, (TARGET, 1))

        ready = select.select([sock], [], [], TIMEOUT)
        timestamp = datetime.now().strftime("%Y-%m-%d %H:%M:%S")

        if ready[0] == []:
            log(timestamp, success=False)
        else:
            recv_packet, addr = sock.recvfrom(1024)
            recv_time = time.time()
            rtt = (recv_time - send_time) * 1000
            log(timestamp, rtt=rtt, success=True)

        time.sleep(INTERVAL)


# Uncomment to test ping function. Returns RTT
if __name__ == "__main__":
    ping()
