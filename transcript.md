# Claude Code Session Transcripts

## How to Access Chat History

### Resume a Previous Conversation
In VS Code Claude Code extension, previous conversations are listed in the sidebar. Click one to resume with full context preserved.

In CLI: use `/resume` to pick up a previous conversation.

### Raw Transcript Files
Full raw transcripts (JSONL format) are stored at:
```
C:\Users\michael.guber\.claude\projects\c--Users-michael-guber-polymarket-bot\
```

### Session Memory (Auto-loaded)
Memory files that persist across sessions are at:
```
C:\Users\michael.guber\.claude\projects\c--Users-michael-guber-polymarket-bot\memory\
```
- `MEMORY.md` — loaded into system prompt every session (wallet info, bugs fixed, user preferences)
- `patterns.md` — detailed technical patterns (py-clob-client quirks, API notes, risk calculation)

### Project Instructions
`CLAUDE.md` in the repo root is loaded every session automatically. Contains architecture, config, and design decisions.

## Session Log

| Date | Transcript | Summary |
|------|-----------|---------|
| 2026-02-15 | `3f258b56-9e8a-4622-b569-ceffed216c37.jsonl` | Initial build, .NET port, live trading setup, stop-loss fix, CLI args, git push |
