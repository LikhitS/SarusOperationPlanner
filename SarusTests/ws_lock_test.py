"""Checks the raw MAVLink websocket on port 56781: accepted from this PC, refused from a network address."""
import base64
import os
import socket


def handshake(host, origin=None):
    key = base64.b64encode(os.urandom(16)).decode()
    req = ("GET /websocket/raw HTTP/1.1\r\nHost: %s:56781\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
           "Sec-WebSocket-Key: %s\r\nSec-WebSocket-Version: 13\r\n" % (host, key))
    if origin:
        req += "Origin: %s\r\n" % origin
    req += "\r\n"
    s = socket.create_connection((host, 56781), timeout=5)
    s.sendall(req.encode())
    data = s.recv(200).decode(errors="replace")
    s.close()
    return data.split("\r\n")[0]


lan = [a for a in socket.gethostbyname_ex(socket.gethostname())[2] if not a.startswith("127.")]
results = {
    "from this PC (127.0.0.1)": handshake("127.0.0.1"),
    "from this PC, page from another website": handshake("127.0.0.1", "http://evil.example.com"),
}
for a in lan:
    results["from network address %s" % a] = handshake(a)
for k, v in results.items():
    print("%-45s -> %s" % (k, v))
ok = ("101" in results["from this PC (127.0.0.1)"]
      and all("403" in v for k, v in results.items() if k != "from this PC (127.0.0.1)"))
print("RESULT", "PASS" if ok and lan else "FAIL" if lan else "NO NETWORK ADDRESS TO TEST")
