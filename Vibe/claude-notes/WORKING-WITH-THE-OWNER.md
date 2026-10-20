# Working with the owner (deloweProjects / delowe, @delow3)

From many rounds of messages, 2026-10-17 to 2026-10-20. Read this before you reply to them.

## How they talk

- Short, fast messages with typos ("bro", "n" = and, "therz" = there's, "wait for more").
  - "Wait for more" means **don't start yet**: more screenshots or requests are coming.
  - Collect everything, then act on all of it.
- They send phone and PC screenshots and point at things ("look here", "the one in red"). Look
  closely and describe back what you see before you fix it.
- **They are not a programmer by trade.** Explain in plain words what changed and what they will see.
  - Say what to test, by ear or by eye, in a short list.
  - Don't dump file names on them; those go in commits and notes.

## What they care about

1. **Don't break what works.** Asked again and again ("don't break anything anymore, we want a
   stable version"). New options go behind a setting that defaults to today's behaviour. Verify with
   the UI test before handing anything over.
2. **Honesty about status.**
   - Never say "sent" or "done" until the workflow run is green and its log shows it.
   - Never present a guess as a cause.
   - They noticed when the UI got reverted by mistake ("you completely reversed the UI"), and they
     noticed when sound broke after it had worked.
3. **Low data.** They are often on mobile data.
   - Test builds go out as one heavily packed 7z.
   - Don't post the 170 MB zip.
   - Ask before posting extra files.
4. **Looks:** slick, modern, "classy"; frosted and tinted rather than flat white or black. They like
   designs they can approve first (mockup → "approved").
5. **Credit.** They want it recognised that Linc is theirs, but in a clean, non-suspicious way (see
   `AUTHORSHIP.md`).
6. **Notes.** Their standing rule: "always write down protocols and how they work so you don't get
   lost." Update SOUND.md, PROTOCOL.md and FEATURES.md with the code.

## Hard rules they set (don't relitigate)

- **Telegram (t.me/LincApp) is their public supporter channel.**
  - Post only when they say "send" / "send to telegram".
  - Never ask for the bot token; it lives in repo secrets used only by workflows.
- **Never commit a keystore.** Never use `release.yml` for tests (use `test-build/*` / `ui-test/*`
  branches).
- **No PR unless asked.** A release counts as asked; its PR to main is part of the release flow.
- **When they say stop or "no more fixing", stop.** Write notes instead (as on 2026-10-20).

## Their setup

- Windows PC (taskbar sometimes auto-hide, high-res, often maximised), Android phone (Android 13+;
  sometimes USB, often home Wi-Fi). They use VB-Cable for the speaker modes.
- They test on real hardware and send logs: phone Settings › Logs › Copy log, PC Settings › Logs.
