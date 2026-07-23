# storage/

Runtime capture files live here at runtime. Empty at rest.

- `*.mmap` — memory-mapped append-only event log per session.
- `*.sqlite` — per-session index database (WAL mode).

Both are excluded from git (`.gitignore`).
