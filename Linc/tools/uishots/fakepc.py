"""A PC as far as the phone app can tell, for android.py's screenshots.

Connects the way the PC app does over USB: `adb forward` to the phone's `linc` socket, then the
desktop's hello and the four subscriptions. It answers what the Control pages ask (PC state and
volume, Task Manager figures, files, shells, sound outputs, Bluetooth sound) with canned data and
probes `status.get` like the real PC's health loop. No TLS exchange and no channel dial-backs: the
pages are under review, not the streams.
"""
import json
import socket
import struct
import subprocess
import threading
import time
import uuid

LOCAL_PORT = 47123
HOST = "DESKTOP-UITEST"


class FakePc:
    def __init__(self, log=print):
        self.log = log
        self.connected = False
        self.requests = 0
        self._stop = threading.Event()
        self._sock: socket.socket | None = None
        self._lock = threading.Lock()
        self._thread = threading.Thread(target=self._run, daemon=True)

    # The PC's sync setup (v30), as the phone's Sync page shows it.
    sync = {
        "lanes": {"folders": True, "photos": True, "messages": False, "calls": False},
        "pairs": [
            {"id": "pair-docs", "name": "Documents", "pcPath": "C:\\Users\\Sam\\Documents", "phonePath": "/sdcard/Documents", "direction": "both", "enabled": True},
            {"id": "pair-music", "name": "Music", "pcPath": "C:\\Users\\Sam\\Music", "phonePath": "/sdcard/Music", "direction": "toPhone", "enabled": True},
            {"id": "pair-shots", "name": "Screenshots", "pcPath": "D:\\Phone shots", "phonePath": "/sdcard/Pictures/Screenshots", "direction": "toPc", "enabled": False},
        ],
        "status": "Synced 3 folders · 2 min ago",
        "photosPcPath": "C:\\Users\\Sam\\Pictures\\Linc\\Photos",
    }

    def start(self) -> None:
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        if self._sock:
            try:
                self._sock.close()
            except OSError:
                pass

    def wait_connected(self, seconds: float) -> bool:
        until = time.time() + seconds
        while time.time() < until and not self.connected:
            time.sleep(0.5)
        return self.connected

    # ---- the link ------------------------------------------------------------------------------

    def _run(self) -> None:
        while not self._stop.is_set():
            try:
                subprocess.run(["adb", "forward", f"tcp:{LOCAL_PORT}", "localabstract:linc"], capture_output=True, timeout=30)
                with socket.create_connection(("127.0.0.1", LOCAL_PORT), timeout=10) as sock:
                    self._sock = sock
                    sock.settimeout(None)
                    self._serve(sock)
            except (OSError, ValueError, json.JSONDecodeError) as e:
                if self.connected:
                    self.log(f"fake PC: link ended ({e})")
            self.connected = False
            self._stop.wait(2)

    def _serve(self, sock: socket.socket) -> None:
        hello_id = self._send(sock, "hello", None, {
            "app": "linc-desktop", "appVersion": "1.0.0", "host": HOST,
            "pcId": "0123456789abcdef", "minV": 0, "maxV": 30,
        })
        reply = self._read(sock)
        if reply is None or reply.get("type") != "hello" or reply.get("replyTo") != hello_id:
            raise ValueError(f"no hello back: {reply}")
        for topic in ("status", "notifications", "clipboard", "media"):
            self._send(sock, "subscribe", None, {"topic": topic})
        self.connected = True
        self.log(f"fake PC: connected (phone negotiated v{reply.get('payload', {}).get('v')})")
        self._send(sock, "pc.media.state", None, {
            "title": "INtombi Entsha", "artist": "TNS", "playing": True,
            "positionMs": 74000, "durationMs": 213000, "app": "Winamp",
        })
        threading.Thread(target=self._probe, args=(sock,), daemon=True).start()
        while not self._stop.is_set():
            msg = self._read(sock)
            if msg is None:
                raise OSError("the phone closed the link")
            self.requests += 1
            self._answer(sock, msg)

    def _probe(self, sock: socket.socket) -> None:
        """The PC's health loop: status.get every 15 s."""
        while not self._stop.is_set() and self.connected:
            try:
                self._send(sock, "status.get", None, {})
            except OSError:
                return
            self._stop.wait(15)

    # ---- answers ---------------------------------------------------------------------------------

    def _answer(self, sock: socket.socket, msg: dict) -> None:
        t, rid, p = msg.get("type", ""), msg.get("id"), msg.get("payload") or {}
        replies = {
            "pc.state.get": ("pc.state", {
                "volume": 42, "muted": False, "brightness": 70, "canBrightness": True, "canSleep": True,
                "canShutdown": True, "battery": {"percent": 64, "charging": False}, "network": "Wi-Fi",
            }),
            "pc.info.get": ("pc.info", {
                "name": HOST, "os": "Windows 11 Pro 24H2", "cpuPct": 23, "memUsedMb": 9800, "memTotalMb": 16000,
                "uptimeMin": 1320, "disks": [{"name": "C:", "freeGb": 212.4, "totalGb": 476.3}],
                "battery": {"percent": 64, "charging": False},
                "cpuName": "AMD Ryzen 7 7735HS", "cores": 8, "threads": 16, "cpuMhz": 3200, "processes": 214,
                "handles": 98000, "diskPct": 6, "diskReadKBps": 1200, "diskWriteKBps": 640,
                "netSendKbps": 180, "netRecvKbps": 2400, "netName": "Wi-Fi", "gpuPct": 12, "gpuName": "AMD Radeon Graphics",
                "memCommittedMb": 12100, "memCommitLimitMb": 24000,
            }),
            "pc.apps.get": ("pc.apps", {"apps": [
                {"pid": 4120, "name": "chrome.exe", "title": "YouTube — Google Chrome", "memMb": 1450},
                {"pid": 8812, "name": "Code.exe", "title": "Linc — Visual Studio Code", "memMb": 820},
                {"pid": 2204, "name": "winamp.exe", "title": "Winamp", "memMb": 96},
            ]}),
            "pc.term.get": ("pc.term.state", {"shells": ["cmd", "powershell", "winps", "wsl:Ubuntu", "gitbash"]}),
            "pc.audio.outputs.get": ("pc.audio.outputs", {"outputs": [
                {"id": "spk", "name": "Speakers (Realtek(R) Audio)", "bluetooth": False, "default": True},
                {"id": "bud", "name": "Galaxy Buds2", "bluetooth": True, "default": False},
            ]}),
            "pc.btaudio.get": ("pc.btaudio.state", {"on": False, "supported": True}),
            "pc.look.get": ("pc.look.state", {
                "appsDark": True, "systemDark": True, "transparency": True, "accent": "#6352A3", "autoAccent": False,
            }),
            "pc.curtain.set": ("pc.curtain.state", {"on": bool(p.get("on")), "available": True}),
        }
        if t == "sync.state.get":
            self._send(sock, "sync.state", rid, self.sync)
        elif t == "sync.pairs.set":
            self.sync["pairs"] = p.get("pairs") or []
            self._send(sock, "sync.state", None, self.sync)
        elif t == "sync.lane.set":
            self.sync["lanes"][p.get("lane", "")] = bool(p.get("on"))
            self._send(sock, "sync.state", None, self.sync)
        elif t == "pc.files.list":
            self._send(sock, "pc.files.listing", rid, self._listing(p.get("path") or ""))
        elif t == "pc.btaudio.set":
            on = bool(p.get("on"))
            self._send(sock, "pc.btaudio.state", rid, {"on": on, "supported": True, "device": "Pixel 7" if on else None})
        elif t in replies:
            reply_type, payload = replies[t]
            self._send(sock, reply_type, rid, payload)
        elif t in ("pc.control", "pc.media.control", "clipboard.changed", "share.item", "continue.url"):
            self._send(sock, "ok", rid, {})
        # status, notifications and the rest are the phone talking; a PC just listens.

    @staticmethod
    def _listing(path: str) -> dict:
        now = int(time.time() * 1000)
        if not path:
            return {"path": "", "parent": None, "entries": [
                {"name": "Desktop", "path": "C:\\Users\\delowe\\Desktop", "dir": True, "place": True},
                {"name": "Documents", "path": "C:\\Users\\delowe\\Documents", "dir": True, "place": True},
                {"name": "Downloads", "path": "C:\\Users\\delowe\\Downloads", "dir": True, "place": True},
                {"name": "Pictures", "path": "C:\\Users\\delowe\\Pictures", "dir": True, "place": True},
                {"name": "Local Disk (C:)", "path": "C:\\", "dir": True, "drive": True, "freeGb": 212.4, "totalGb": 476.3},
            ]}
        return {"path": path, "parent": "", "entries": [
            {"name": "Linc", "path": path + "\\Linc", "dir": True, "modified": now - 3_600_000},
            {"name": "Photos 2026", "path": path + "\\Photos 2026", "dir": True, "modified": now - 86_400_000},
            {"name": "notes.txt", "path": path + "\\notes.txt", "dir": False, "size": 2_048, "modified": now - 600_000},
            {"name": "holiday.jpg", "path": path + "\\holiday.jpg", "dir": False, "size": 3_400_000, "modified": now - 7_200_000},
            {"name": "song.mp3", "path": path + "\\song.mp3", "dir": False, "size": 8_100_000, "modified": now - 172_800_000},
            {"name": "report.pdf", "path": path + "\\report.pdf", "dir": False, "size": 640_000, "modified": now - 259_200_000},
        ]}

    # ---- framing: 4-byte big-endian length, then UTF-8 JSON --------------------------------------

    def _send(self, sock: socket.socket, type_: str, reply_to, payload: dict) -> str:
        mid = str(uuid.uuid4())
        body = json.dumps({"v": 30, "type": type_, "id": mid, "replyTo": reply_to, "payload": payload}).encode()
        with self._lock:
            sock.sendall(struct.pack(">i", len(body)) + body)
        return mid

    @staticmethod
    def _read(sock: socket.socket) -> dict | None:
        def exactly(n: int) -> bytes | None:
            buf = b""
            while len(buf) < n:
                chunk = sock.recv(n - len(buf))
                if not chunk:
                    return None
                buf += chunk
            return buf
        header = exactly(4)
        if header is None:
            return None
        (length,) = struct.unpack(">i", header)
        if length <= 0 or length > 4 * 1024 * 1024:
            raise ValueError(f"bad frame length {length}")
        body = exactly(length)
        return None if body is None else json.loads(body.decode())
