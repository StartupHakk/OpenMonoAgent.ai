# First run spec

1. Welcome: local, private, zero token cost after setup. Disk and time
   expectations, license and privacy note.
2. Hardware check: GPU name, VRAM, RAM, CPU cores, free disk. Flags for
   missing driver, under 12GB VRAM, under 20GB RAM on CPU path, low disk.
3. Recommended model: tier mapping from models.json (mirrors install.sh),
   preselected, with size, accuracy label, context, and vision note. The user
   may pick a smaller model for headroom.
4. Storage: models dir default (%LOCALAPPDATA%\OpenMono\models) plus free
   space, change drive button, disk impact summary (model plus mmproj plus
   10 percent).
5. Download: per file progress (model, mmproj, GPU flavor), pause, resume,
   cancel, checksum verification, ETA. Only the Start button is gated.
6. Docker step (skippable): detects Docker Desktop, offers install and start.
   Skipping shows what is lost (server backed search and scraping) and notes
   the built in DuckDuckGo and direct fetch fallback.
7. Workspace folder: picker for the default code folder, confinement note.
8. Settings: endpoint default, model alias, vision toggle, permission
   strictness, ACP loopback toggle (off by default).
9. Ready: Start OpenMono. Starts inference, waits for healthy, opens Chat.
