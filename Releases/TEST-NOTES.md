# What's new in the current test release (shown in the Telegram post; plain lines, no headings below)
• Mirror "Fill this monitor": if the link drops, the phone now puts its own screen size and rotation back by itself after a few seconds; a "Reset screen" card on the phone and a button in PC Tools › System do it at once
• The phone and the PC now always agree on how they are connected (USB or Wi-Fi) and whether ADB is up
• ADB turns itself back on: after a restart or a Wi-Fi change Linc switches wireless debugging on again (needs the cable once, if it says so)
• Tools says "No ADB" when the phone is connected without it, and gets ADB back by itself
• Connection fixes from your test: no more endless connect-and-drop loop, the cable no longer breaks after a Wi-Fi switch, no false "install Linc?" prompt, clearer messages
• Test builds are now "Linc Test" and update themselves: when a newer test build is out, Linc Test offers it (PC restarts into it; the phone asks Android to update). No more reinstalling from Telegram
• New connection engine (on by default; Settings › Connection on both apps turns it off): the PC and phone find each other faster on any Wi-Fi, the PC can dial the phone itself, and a short drop or a cable pulled with Wi-Fi around doesn't show
• PC Home: "Connected over USB / Wi-Fi", plus an ADB chip that turns green when ADB is up too; if Linc on the phone isn't running, the PC starts it
• Pair without a cable: PC › Add a phone › Wi-Fi shows a Linc code; point the phone's camera at it and tap the link
• The phone starts Linc after a restart and when you open the app; the installer offers a firewall rule (Windows asks once)
• Mirror PC on the phone: a "Mute PC" button next to Sound. PC: the Tools page (Type, Rescue, Apps, Capture, Health, System, 331 Commands, Console)
