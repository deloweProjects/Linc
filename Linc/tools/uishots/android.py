#!/usr/bin/env python3
"""Screenshots of the Android app on an emulator, for review without a phone in hand.

Run by .github/workflows/ui-test.yml inside android-emulator-runner, with the debug APK built.
Installs Linc, skips first-run onboarding, connects a fake PC (fakepc.py, the PC app's own USB
handshake over adb forward), walks every main screen by tapping what a person would tap (found by its text or accessibility label in a uiautomator dump), and saves a PNG and
the screen's UI tree per step under out/android/. Exits 1 only if Linc crashed; a screen it
could not reach is reported in summary.md, not failed, so one moved button doesn't hide the rest.

    python3 tools/uishots/android.py <apk> <out-dir>
"""
import os
import re
import subprocess
import sys
import time
import xml.etree.ElementTree as ET

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from fakepc import FakePc  # noqa: E402

PKG = "app.linc.android"
APK, OUT = sys.argv[1], sys.argv[2]
os.makedirs(OUT, exist_ok=True)
notes: list[str] = []
step = 0


def adb(*args: str, check: bool = True, capture: bool = True) -> str:
    res = subprocess.run(["adb", *args], capture_output=capture, text=True, timeout=120)
    if check and res.returncode != 0:
        raise RuntimeError(f"adb {' '.join(args)}: {res.stderr.strip()}")
    return res.stdout if capture else ""


def shell(cmd: str, check: bool = False) -> str:
    return adb("shell", cmd, check=check)


def dump() -> ET.Element | None:
    for _ in range(3):
        shell("uiautomator dump /sdcard/ui.xml >/dev/null 2>&1")
        xml = shell("cat /sdcard/ui.xml")
        if xml.strip().startswith("<?xml"):
            try:
                return ET.fromstring(xml)
            except ET.ParseError:
                pass
        time.sleep(1)
    return None


def find(label: str, exact: bool = True, lowest: bool = False) -> tuple[int, int] | None:
    """The centre of the first element showing `label`, or with `lowest` the one nearest the
    bottom: a tab's name also appears on Home (the "Control" heading, the Clipboard tile)."""
    root = dump()
    if root is None:
        return None
    best = None
    for node in root.iter("node"):
        for attr in ("text", "content-desc"):
            value = node.get(attr, "")
            if (value == label) if exact else (label.lower() in value.lower()):
                m = re.match(r"\[(\d+),(\d+)\]\[(\d+),(\d+)\]", node.get("bounds", ""))
                if m:
                    x1, y1, x2, y2 = map(int, m.groups())
                    spot = ((x1 + x2) // 2, (y1 + y2) // 2)
                    if not lowest:
                        return spot
                    if best is None or spot[1] > best[1]:
                        best = spot
    return best


# Android's own prompts that can sit over Linc: answered as a person would, so the screen under
# them is what gets checked. ("access all device logs" comes from READ_LOGS, which the PC grants
# for background clipboard sync.)
PROMPTS = ("Allow one-time access", "Allow", "Not now")


def dismiss_prompts() -> None:
    root = dump()
    if root is None:
        return
    texts = {n.get("text", "") for n in root.iter("node")}
    if not ({"Allow one-time access", "Don’t allow", "Don't allow"} & texts or "Let app always run in background?" in texts):
        return
    for label in PROMPTS:
        if label in texts:
            spot = find(label)
            if spot:
                shell(f"input tap {spot[0]} {spot[1]}")
                time.sleep(1.5)
                notes.append(f"- answered Android's prompt with **{label}**")
                return


def tap(label: str, exact: bool = True, lowest: bool = False) -> bool:
    dismiss_prompts()
    spot = find(label, exact, lowest)
    if spot is None:
        notes.append(f"- couldn't find **{label}** to tap (step {step + 1})")
        return False
    shell(f"input tap {spot[0]} {spot[1]}")
    time.sleep(1.5)
    return True


def shot(name: str) -> None:
    global step
    dismiss_prompts()
    step += 1
    base = os.path.join(OUT, f"{step:02d}-{name}")
    with open(base + ".png", "wb") as f:
        f.write(subprocess.run(["adb", "exec-out", "screencap", "-p"], capture_output=True, timeout=60).stdout)
    shell("uiautomator dump /sdcard/ui.xml >/dev/null 2>&1")
    with open(base + ".xml", "w", encoding="utf-8") as f:
        f.write(shell("cat /sdcard/ui.xml"))
    if not alive():
        notes.append(f"- **Linc was not running** at {name}")


def alive() -> bool:
    return shell(f"pidof {PKG}").strip() != ""


def scroll() -> None:
    """One slow drag up, slow enough to scroll rather than fling or be taken for a tap."""
    shell("input swipe 540 1700 540 700 900")
    time.sleep(1.5)


def back() -> None:
    shell("input keyevent 4")
    time.sleep(1)


# ---- set up --------------------------------------------------------------------------------
adb("wait-for-device")
while shell("getprop sys.boot_completed").strip() != "1":
    time.sleep(2)
shell("settings put global window_animation_scale 0")
shell("settings put global transition_animation_scale 0")
shell("settings put global animator_duration_scale 0")
shell("input keyevent 82")  # wake and unlock
adb("install", "-r", "-g", APK)
# Exempt from battery optimisation up front, or Android's own "always run in background?" prompt
# sits over the first screens.
shell(f"dumpsys deviceidle whitelist +{PKG}")
shell("cmd uimode night yes")
# First run's onboarding is its own flow; the screens behind it are what's under review.
shell(f"run-as {PKG} sh -c 'mkdir -p shared_prefs && printf \"%s\" "
      f"\"<?xml version=\\\"1.0\\\" encoding=\\\"utf-8\\\" standalone=\\\"yes\\\" ?><map><boolean name=\\\"onboarded\\\" value=\\\"true\\\" /></map>\""
      f" > shared_prefs/linc.xml'")
shell("logcat -c")


def launch() -> None:
    shell(f"am force-stop {PKG}")
    shell(f"monkey -p {PKG} -c android.intent.category.LAUNCHER 1 >/dev/null 2>&1")
    # The companion service, as the PC app starts it over adb when it isn't running yet: on a
    # fresh install nothing else does, and its socket is what the fake PC dials.
    shell(f"am start-foreground-service -n {PKG}/{PKG}.service.CompanionService")
    time.sleep(6)
    # A first-run system dialog (notifications, battery) would sit over everything.
    for label in ("Allow", "Not now", "Skip"):
        if find(label):
            tap(label)


pc = FakePc(log=print)


def walk(theme: str) -> None:
    launch()
    if not pc.wait_connected(45):
        notes.append(f"- the fake PC wasn't connected for the {theme} walk")
    time.sleep(3)  # the PC's answers land and get drawn
    shot(f"{theme}-home")
    scroll()
    shot(f"{theme}-home-scrolled")
    if tap("Control", lowest=True):
        shot(f"{theme}-control-remote")
        for page in ("Sound", "PC", "Files", "Terminal"):
            if tap("Control menu"):
                if page == "Sound":
                    shot(f"{theme}-control-menu")
                if tap(page):
                    shot(f"{theme}-control-{page.lower()}")
                    if page == "Sound":
                        scroll()
                        shot(f"{theme}-control-sound-scrolled")
            else:
                break
    if tap("Clipboard", lowest=True):
        shot(f"{theme}-clipboard")
    if tap("Share", lowest=True):
        shot(f"{theme}-share")
    if tap("Home", lowest=True) and tap("Settings"):
        shot(f"{theme}-settings")
        back()


pc.start()
walk("dark")
shell("cmd uimode night no")
time.sleep(2)
walk("light")
shell("cmd uimode night yes")

# The same Home with no PC, for the disconnected state.
pc.stop()
shell(f"am force-stop {PKG}")
time.sleep(2)
launch()
time.sleep(4)
shot("dark-home-no-pc")

# ---- report --------------------------------------------------------------------------------
crashes = [l for l in shell("logcat -d -b crash").splitlines() if PKG in l or "FATAL" in l]
with open(os.path.join(OUT, "logcat.txt"), "w", encoding="utf-8") as f:
    f.write(shell("logcat -d -t 4000"))
with open(os.path.join(OUT, "summary.md"), "w", encoding="utf-8") as f:
    f.write(f"## Android screens\n\n{step} screenshots · fake PC answered {pc.requests} messages.\n\n")
    if crashes:
        f.write("### Crash\n\n```\n" + "\n".join(crashes[:80]) + "\n```\n\n")
    if notes:
        f.write("### Notes\n\n" + "\n".join(notes) + "\n")
print(open(os.path.join(OUT, "summary.md"), encoding="utf-8").read())
sys.exit(1 if crashes else 0)
