# HAND-TEST — M15 (+ the M14 leftovers)

**Why:** nothing below has ever been observed. No phone was attached during the session that built
it. Six fixes, all structurally verified, none seen working.

**Start the app from:**
`Linc/DESKTOP/Linc.Desktop/bin/x64/Debug/net8.0-windows10.0.19041.0/Linc.Desktop.exe`
(the agent killed it before each build, so it is not running).

**Expect one oddity on first launch:** Linc now uses its own ADB server on port **5038** instead of
the shared 5037. A phone that was already connected wirelessly on the old server is not inherited,
so you may need to reconnect once. **That is expected, not a bug.**

---

## BLOCK 1 — the second phone (needs two phones) · ~5 min

This is the complaint that started M15: a new phone never showed the "trust this computer" prompt.

1. Connect the **Pixel 7** as usual. Wait for the Device page to read **Connected**.
2. Look at the **Link** tile. It should now show a second line: **"chosen automatically"**.
3. With the Pixel 7 still connected, plug in the **second phone** by USB. Leave USB debugging OFF.
4. Now turn USB debugging **on** on the second phone.
   **→ Within ~3 seconds the Device page should say, in plain English:**
   *"<model> is asking whether to trust this PC. Unlock the phone and tap Allow…"*
   **This is the case that previously showed absolutely nothing.**
5. Unlock the second phone and tap **Allow**.
   **→ Within ~3 seconds, with no app restart and no wizard:** the message clears and a card offers
   the new phone, saying *"Linc talks to one phone at a time. Disconnecting Pixel 7 so it can
   connect to <model>."*
6. Activate the new phone from that card.
   **→ The Pixel 7 link should tear down first, then the new phone connects. No endless spinner.**
7. Switch back to the Pixel 7 from the device tabs — same clean switch in reverse.

**If step 4 shows nothing:** grab `%LOCALAPPDATA%\Linc/logs/linc-<today>.ndjson` and send it. That
silence is now a bug with a log line behind it.
**If the Allow prompt never appears on the phone at all:** that part is the phone, not Linc — on
that phone go to Developer options → **Revoke USB debugging authorisations**, unplug, replug, redo
from step 4.

---

## BLOCK 2 — the USB delay (Pixel 7 only) · ~3 min · **THIS IS THE ONE NUMBER I NEED**

8. Connect over **USB**. Wait for **Connected**, Link = *USB cable*.
9. Note the time and **pull the cable.**
10. Time how long until it stops saying Connected. **Expect 6–21 seconds.**
11. Time how long until it comes back on **wireless**. **This is the number that matters** — it
    should be seconds now, not "whenever the phone next advertises". Before this fix, nothing
    scheduled a reconnect after a cable pull at all.
12. Open `%LOCALAPPDATA%\Linc/logs/linc-<today>.ndjson`, find the line:
    `Connection dropped; will reconnect automatically. Detection took NNNN ms from the first failed probe…`
    **→ Paste that line back to me.** It is the only hardware measurement nobody has ever taken, and
    it decides whether the 15-second health poll is worth changing.

---

## BLOCK 3 — pick your own link · ~2 min

13. While connected, find the new **"Use this link now"** card on the Device page.
    Click **Use wireless now**.
    **→ The Link tile should change to *Wireless debugging* / "chosen by you".**
14. Now kill that link (turn off Wi-Fi or wireless debugging on the phone).
    **→ It should fall back AND tell you it did** — *"Linc is choosing the connection automatically
    again — a wireless link stopped answering."* **A silent fallback is a failure.**
15. Click **Back to automatic** and confirm the tile reads "chosen automatically" again.

---

## BLOCK 4 — the look · ~3 min

16. **Wallpaper.** Home's widgets background should be your actual wallpaper, **sharp**, not frosted.
17. **Text over it.** This is the risk: the blur was also what kept text legible, and the scrim is
    now off. Check both light and dark themes.
    **→ If text is hard to read, tell me and I change one number** — `ScrimStrength` in
    `Services/ThemeSyncService.cs`. 0.35 = light veil, 0.6 = strong, 1.0 = exactly how it looked
    before. No session needed, it's a one-line change.
18. **One disconnected message.** Disconnect the phone and look at Home.
    **→ Exactly ONE sentence about the connection**, in the phone card. The cache banner may also
    show, but it must only say *"Showing what was last synced at …"* — no second "disconnected".
    **→ Full brightness. Nothing dimmed.**
19. **Apps off.** Turn the Apps section off (3-dots → Sections).
    **→ The notifications area should expand to fill the space. No gap where Apps was.**
    Turn it back on and confirm the split returns to where you had it.

---

## BLOCK 5 — the M14 leftovers, still owed from two weeks ago · ~2 min

20. **Tray icon at 16 px** — check on a **light** taskbar and a **dark** one. Expect a clean white L.
21. **Mirror window icon** — start a mirror session and look at its title bar / taskbar entry.
22. **Android launcher icon** — black tile, white L.
23. **The 3-dots "Widget options" flyout** on Home still opens and matches the black-and-white theme.

---

## What to send back

- The **log line from step 12** (the important one).
- Anything from steps 4, 6, 11, 14, 18, 19 that did **not** behave as written.
- A yes/no on step 17 (text legible over the sharp wallpaper).

Anything that fails becomes the next task file. Anything that passes closes M15.
