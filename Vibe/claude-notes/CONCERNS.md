# My concerns at handoff (2026-10-20)

Things that are **not proven**, or that I'd watch, in rough order of worry. The owner tested the
basics on hardware and says "everything works", but much of 1.0.0.17 has only been seen in CI
screenshots.

## Not yet tested on the owner's hardware

1. **Phone as Play everywhere's source** (v31, "Whose sound: This phone"). Built from reading code
   and scrcpy's source, never heard. Watch for:
   - echo or feedback (it relies on Linc's own copy being `USAGE_GAME`, which scrcpy's playback
     capture skips);
   - Spotify pausing;
   - doubled sound if Mirror screen or "Phone sound on PC" runs at the same time;
   - a PC headset getting the sound out of step until the cable becomes the default output again.
2. **Phone frosted overlays.**
   - Blur behind a dialog or sheet window works only where the phone maker allows cross-window blur.
   - The hook sits in the dialog's confirm-button slot (TIDY-UP A10).
   - The light-theme wash and the white-wallpaper case haven't been seen on a real phone.
3. **PC frosted flyouts, dialogs and side bar.** Translucent brushes sit over WinUI's own popup
   acrylic; light theme before the phone palette arrives is near-white (#F7F7F7). High-contrast mode
   gets these brushes too, untested.
4. **PC fonts.**
   - Variable-font family names: `#Manrope` / `#Outfit` may not match, so the text silently stays
     Segoe.
   - Live switching misses open popups (TIDY-UP B8).
   - Phone fonts are variable TTFs with weights set explicitly; untested on a real device.
5. **PC Home FitScale.** Splitter drags under the scale may not track the pointer exactly. There is
   possible layout flip-flop near the width thresholds (TIDY-UP B9).
6. **Terminal sideways pan.** Full-screen programs (vim, htop) draw 160 columns wide. Constant output
   pulls the view back to the cursor 1.5 s after each pan.

## Known weak spots in shipped code

- **Sound quality on the phone** (owner-reported). See `SOUND-QUALITY.md`. Top suspect: sample
  drop/repeat drift correction at up to 0.5 %, driven by a jittery clock.
- ~~**Child processes:** scrcpy surviving a Linc crash (B2), PhoneSoundOnPc's handle leak (B1).~~
  Fixed 2026-10-27 (`937366e`, TIDY-UP "Done"); adb's own server deliberately still outlives Linc.
- **Swallowed errors** on both apps (189 `runCatching` on the phone, ~119 fire-and-forget tasks on
  the PC). A future "it just stopped" bug may leave **no log line**. Add logging before guessing.
  2026-10-27: the async-void crashes are guarded and the phone has `runLogged`; most of the rest
  is still silent.
- ~~**The settings file** written non-atomically with positional fields (B6).~~ Fixed 2026-10-27:
  written via a temp file and a rename, passed by name.
- **The UI test barely asserts** (TIDY-UP B15). Green does not mean every page rendered; open the
  PNGs.

## Process risks

- **Releases:** run them on main. From a feature branch the PR step fails, which is what happened to
  1.0.0.17 (see CLAUDE.md, HANDOFF §3).
- **TEST-NOTES.md** holds shipped notes; rewrite it before the next test build.
- **Signing key:**
  - The release APK is a *debug* build signed with the owner's debug key (in a repo secret). Losing
    or leaking that key is the biggest long-term risk: no more updates over installed copies, or
    someone else can ship "updates".
  - Moving to Play App Signing, or at least a dedicated release key kept offline, should come before
    the user base grows.
- **Licence:** MIT allows rebranded copies as long as the notice is kept. If the owner wants more,
  change it early (AUTHORSHIP.md).
- **Old docs contradict reality** (DOCS-STATE.md). A session that reads `Vibe/master/HANDOVER.md`
  first will think the project is at v18.

## What I'd do first next time (only when the owner asks for work)

1. Add the sound counters (`SOUND-QUALITY.md` › Measuring) and run the `MAX_PPM = 0` A/B test build.
   That decides the sound fix.
2. Small safe fixes: B1, B3, A1, A2/B10 (logging), A3.
3. Make uishots fail on missing pages, and add the Appearance, no-phone, maximised and dark shots.
4. Refresh the stale docs listed in DOCS-STATE.md.
