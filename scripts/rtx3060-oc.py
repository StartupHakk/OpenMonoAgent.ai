#!/usr/bin/env python3
"""RTX 3060 12GB overclock helper. Run as root (the installer grants sudo only for this program).

Measured stable on echo21 (Ubuntu, driver 580, stock 170 W): power limit 212 W,
core +150 MHz, memory +2000 MHz. A 10-minute soak passed 235/235 checks with no
Xid and a 68 C peak. Core +200 MHz crashed (Xid 13 and 43) and is not used.

  openmono-rtx3060-oc status
  openmono-rtx3060-oc apply
  openmono-rtx3060-oc restore
  openmono-rtx3060-oc watch
  openmono-rtx3060-oc self-test

`watch` applies the measured point, then checks the card every 15 seconds.
It restores stock and stays there on an Xid in the kernel log, a GPU
temperature of 83 C or higher, or a failed check (the offsets did not stick,
or nvidia-smi cannot read the card). NVML offsets reset at reboot, so a boot
service runs `watch`. After an automatic backoff the next boot stays at stock
until `scripts/rtx3060-12gb.sh oc-enable` clears the latch.

The helper refuses to change clocks unless the card name is an RTX 3060 and
its memory is in the 12 GB range. It does not touch any other GPU.
"""

import ctypes
import os
import signal
import subprocess
import sys
import time

POWER_W = 212
GPC_MHZ = 150
MEM_MHZ = 2000
TEMP_LIMIT_C = 83
POLL_S = 15
# Caps match the measured point. apply() cannot ask for more than this.
GPC_CAP = 150
MEM_CAP = 2000
LOG_PATH = "/var/log/openmono-rtx3060-oc.log"
LATCH_PATH = "/var/lib/openmono/rtx3060-oc.backed-off"
DISABLED_PATH = "/etc/openmono/rtx3060-oc.disabled"


def is_rtx3060_12gb(name, memory_mib):
    text = (name or "").lower().replace("nvidia ", "").replace("geforce ", "")
    if "3060" not in text or "rtx" not in text:
        return False
    if "ti" in text or "super" in text:
        return False
    try:
        mem = int(memory_mib)
    except (TypeError, ValueError):
        return False
    return 11000 <= mem <= 14000


def trip_reason(temp_c, xid_seen, readback_ok):
    """Why a running overclock must return to stock. None means keep it."""
    if xid_seen:
        return "xid"
    if temp_c is not None and temp_c >= TEMP_LIMIT_C:
        return "temp-%sC" % temp_c
    if not readback_ok:
        return "readback"
    return None


def log(msg):
    line = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()) + " " + msg
    print(line, flush=True)
    try:
        os.makedirs(os.path.dirname(LOG_PATH), exist_ok=True)
        with open(LOG_PATH, "a", encoding="utf-8") as fh:
            fh.write(line + "\n")
    except OSError as ex:
        print("log write failed: %s" % ex, file=sys.stderr)


def latch(reason):
    os.makedirs(os.path.dirname(LATCH_PATH), exist_ok=True)
    with open(LATCH_PATH, "w", encoding="utf-8") as fh:
        fh.write(reason + "\n")
    log("LATCH %s — staying at stock until oc-enable" % reason)


def latched():
    try:
        with open(LATCH_PATH, encoding="utf-8") as fh:
            return fh.read().strip() or "backed-off"
    except OSError:
        return None


class Nvml:
    def __init__(self):
        self.nv = ctypes.CDLL("libnvidia-ml.so.1")
        self.nv.nvmlInit_v2.restype = ctypes.c_int
        rc = self.nv.nvmlInit_v2()
        if rc != 0:
            raise SystemExit("nvmlInit failed rc=%s" % rc)
        self.handle = None
        self.index = None
        self.name = ""
        self.memory_mib = 0
        self._bind_target()

    def _call(self, fn, *args):
        rc = getattr(self.nv, fn)(*args)
        if rc != 0:
            raise SystemExit("%s failed rc=%s" % (fn, rc))

    def _bind_target(self):
        count = ctypes.c_uint()
        self._call("nvmlDeviceGetCount_v2", ctypes.byref(count))
        for index in range(count.value):
            handle = ctypes.c_void_p()
            self._call("nvmlDeviceGetHandleByIndex_v2", index, ctypes.byref(handle))
            buf = ctypes.create_string_buffer(96)
            self._call("nvmlDeviceGetName", handle, buf, 96)
            mem = self._memory(handle)
            name = buf.value.decode("utf-8", "replace")
            mib = int(mem.total / (1024 * 1024))
            if is_rtx3060_12gb(name, mib):
                self.handle = handle
                self.index = index
                self.name = name
                self.memory_mib = mib
                return
        raise SystemExit("no RTX 3060 12GB found; no clocks were changed")

    def _memory(self, handle):
        class Mem(ctypes.Structure):
            _fields_ = [
                ("total", ctypes.c_ulonglong),
                ("free", ctypes.c_ulonglong),
                ("used", ctypes.c_ulonglong),
            ]

        mem = Mem()
        self._call("nvmlDeviceGetMemoryInfo", handle, ctypes.byref(mem))
        return mem

    def _u(self, fn, *extra):
        value = ctypes.c_uint()
        rc = getattr(self.nv, fn)(self.handle, *extra, ctypes.byref(value))
        return value.value if rc == 0 else None

    def _i(self, fn):
        value = ctypes.c_int()
        rc = getattr(self.nv, fn)(self.handle, ctypes.byref(value))
        return value.value if rc == 0 else None

    def status(self):
        lo, hi = ctypes.c_uint(), ctypes.c_uint()
        self.nv.nvmlDeviceGetPowerManagementLimitConstraints(
            self.handle, ctypes.byref(lo), ctypes.byref(hi)
        )
        limit = self._u("nvmlDeviceGetPowerManagementLimit") or 0
        default = self._u("nvmlDeviceGetPowerManagementDefaultLimit") or 0
        return {
            "index": self.index,
            "name": self.name,
            "memory_mib": self.memory_mib,
            "power_limit_w": limit / 1000.0,
            "default_w": default / 1000.0,
            "min_w": lo.value / 1000.0,
            "max_w": hi.value / 1000.0,
            "gpc_offset": self._i("nvmlDeviceGetGpcClkVfOffset"),
            "mem_offset": self._i("nvmlDeviceGetMemClkVfOffset"),
            "temp_c": self._u("nvmlDeviceGetTemperature", 0),
        }

    def apply(self):
        st = self.status()
        power = min(float(POWER_W), st["max_w"])
        power = max(power, st["min_w"])
        gpc = max(-GPC_CAP, min(GPC_CAP, GPC_MHZ))
        mem = max(-MEM_CAP, min(MEM_CAP, MEM_MHZ))
        self._call(
            "nvmlDeviceSetPowerManagementLimit",
            self.handle,
            ctypes.c_uint(int(power * 1000)),
        )
        self._call("nvmlDeviceSetGpcClkVfOffset", self.handle, ctypes.c_int(int(gpc)))
        self._call("nvmlDeviceSetMemClkVfOffset", self.handle, ctypes.c_int(int(mem)))
        log("APPLY power=%sW gpc=+%s mem=+%s -> %s" % (power, gpc, mem, self.status()))
        return power, gpc, mem

    def readback_ok(self, power, gpc, mem):
        st = self.status()
        if st["gpc_offset"] != int(gpc) or st["mem_offset"] != int(mem):
            return False
        if abs(st["power_limit_w"] - power) > 2:
            return False
        return True

    def restore(self):
        st = self.status()
        self.nv.nvmlDeviceSetGpcClkVfOffset(self.handle, ctypes.c_int(0))
        self.nv.nvmlDeviceSetMemClkVfOffset(self.handle, ctypes.c_int(0))
        if st["default_w"] > 0:
            self.nv.nvmlDeviceSetPowerManagementLimit(
                self.handle, ctypes.c_uint(int(st["default_w"] * 1000))
            )
        subprocess.run(["nvidia-smi", "-rgc"], capture_output=True)
        subprocess.run(["nvidia-smi", "-rmc"], capture_output=True)
        log("RESTORE stock -> %s" % self.status())


def xid_lines():
    try:
        proc = subprocess.run(
            ["journalctl", "-k", "-b", "-o", "cat", "--no-pager"],
            capture_output=True,
            text=True,
            timeout=20,
        )
    except (OSError, subprocess.TimeoutExpired):
        return []
    return [ln for ln in proc.stdout.splitlines() if "Xid" in ln]


def _pause():
    while True:
        time.sleep(3600)


def watch(gpu):
    if os.path.exists(DISABLED_PATH):
        log("overclock opt-out file present (%s); not applying" % DISABLED_PATH)
        _pause()
    reason = latched()
    if reason:
        log("previous backoff (%s); staying at stock. Clear it with: bash scripts/rtx3060-12gb.sh oc-enable" % reason)
        _pause()
    power, gpc, mem = gpu.apply()
    if not gpu.readback_ok(power, gpc, mem):
        log("APPLY readback failed")
        latch("readback")
        gpu.restore()
        _pause()
    seen = len(xid_lines())
    while True:
        time.sleep(POLL_S)
        try:
            st = gpu.status()
        except SystemExit as ex:
            latch("readback")
            log("status failed: %s" % ex)
            try:
                gpu.restore()
            except SystemExit:
                pass
            _pause()
        lines = xid_lines()
        xid = len(lines) > seen
        if xid:
            seen = len(lines)
        ok = gpu.readback_ok(power, gpc, mem)
        why = trip_reason(st.get("temp_c"), xid, ok)
        if why:
            # Latch before restore so a reset during restore cannot reapply next boot.
            latch(why)
            if xid:
                log("Xid: %s" % lines[-1])
            try:
                gpu.restore()
            except SystemExit as ex:
                log("restore after %s failed: %s" % (why, ex))
            _pause()


def self_test():
    assert is_rtx3060_12gb("NVIDIA GeForce RTX 3060", 12288)
    assert not is_rtx3060_12gb("NVIDIA GeForce RTX 3060 Ti", 8192)
    assert not is_rtx3060_12gb("NVIDIA GeForce RTX 3060", 8192)
    assert not is_rtx3060_12gb("NVIDIA GeForce RTX 4060", 8188)
    assert trip_reason(68, False, True) is None
    assert trip_reason(83, False, True).startswith("temp")
    assert trip_reason(60, True, True) == "xid"
    assert trip_reason(60, False, False) == "readback"
    print("rtx3060-oc self-test ok")


def main(argv):
    cmd = argv[1] if len(argv) > 1 else "status"
    if cmd == "self-test":
        self_test()
        return 0
    if os.geteuid() != 0:
        raise SystemExit("run as root: sudo /usr/local/sbin/openmono-rtx3060-oc %s" % cmd)
    gpu = Nvml()
    if cmd == "status":
        print(gpu.status())
    elif cmd == "apply":
        gpu.apply()
    elif cmd == "restore":
        gpu.restore()
    elif cmd == "watch":
        def _stop(_signum, _frame):
            try:
                gpu.restore()
            except SystemExit:
                pass
            raise SystemExit(0)

        signal.signal(signal.SIGTERM, _stop)
        signal.signal(signal.SIGINT, _stop)
        watch(gpu)
    else:
        raise SystemExit(__doc__)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv))
    except SystemExit as ex:
        if ex.code not in (0, None):
            print(ex, file=sys.stderr)
        raise
