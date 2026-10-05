# SeshMesh — Cross-Agent Session Resumer (.NET 8 WPF)

SeshMesh is a native Windows app for discovering, searching, inspecting, exporting, cross-converting, and resuming coding conversations across AI coding agents and harnesses. Discovery uses bounded background concurrency; search results remain separate from the conversation library. The program file stays `Casr.App.exe`. The index stays in `%LOCALAPPDATA%\Casr`.

Based on the core specification and Rosetta Stone architecture of Jeffrey Emanuel's [`cross_agent_session_resumer`](https://github.com/Dicklesworthstone/cross_agent_session_resumer) (`casr`). His license is reproduced in [NOTICE](NOTICE). SeshMesh's own source is under the MIT License in [LICENSE](LICENSE).

![Platform](https://img.shields.io/badge/Platform-Windows-blue)
![Framework](https://img.shields.io/badge/.NET-8.0%20WPF-purple)
![Tests](https://img.shields.io/badge/Tests-hermetic_suite_passing-brightgreen)

---

## Key Features

1. **Cross-Agent Session Discovery**:
   - Auto-detects and discovers conversations across installed agents:
     - **Grok Build CLI** (`grok` / `grk`)
     - **Antigravity CLI** (`agy` / Gemini family)
     - **Cursor** (`cursor` / `cur`)
     - **OpenClaude** (`openclaude`)
     - **Pi** (`pi`)
     - **Hermes** (`hermes`)
     - **OpenCode** (`opencode`)
     - **OpenAI Codex** (`codex`): active and archived rollout discovery, transcript search,
       export, and native resume. Codex is read-only for imported history: transferring a
       Codex session to a writable provider is supported; targeting Codex from another
       provider opens a new Codex session and does not import that provider's transcript.

2. **In-App Provider Management**:
   - Easily enable or disable specific coding agents directly from the `⚙ Providers` dropdown in the top bar.
   - Each provider row shows live CLI detection (version or `Missing`) with the checked-path evidence as a
     tooltip, plus a **↻ Refresh detection** button — the GUI analogue of `casr providers`.
   - The same dropdown persists the **Preview before converting** choice: with it unchecked, Resume-With
     converts and launches in one click using the saved conversion options.
   - Settings persist automatically across launches in `%LOCALAPPDATA%\Casr\settings.json`.
    - Fresh installs enable all eight providers. An existing `settings.json` keeps its
      explicit choices: a provider added by a later update stays disabled until you opt
      in (the app log notes the missing slugs at startup).
    - The backup folder and custom index-database path persist only when chosen via
      their Browse/Save actions — typing a path without saving does not persist it.

3. **Background Discovery and Indexing**:
   - Summary discovery reads provider stores across parallel background workers.
   - Discovered items stream onto the UI in smooth 50ms batches without locking or freezing the WPF interface.
   - Embedded SQLite storage operates in **WAL mode** with busy timeouts to avoid file locks.
   - Cached list: the previous index is loaded before the live rescan
     (newest first), then the live rescan updates rows in place; the status bar
     always separates cached vs live counts.
   - Change-gated rescan: per-provider store fingerprints (path + size + mtime) in
     `%LOCALAPPDATA%\Casr\scan-fingerprints.json`; byte-identical providers log
     `skipped unchanged store`. First run or a provider-toggle change scans everything.
   - While the window is open, that same discovery and incremental transcript
     index run every 2 minutes. A tick during a scan waits for the next interval.
     The open transcript reloads only when that session's size, message count,
     last activity, or path changed, and the text stays on screen during the re-read.
   - Startup log line (`STARTUP CASR v... db=... user_version=... sessions=...
     messages=... session_emb=... message_emb=... providers=[...]`) shows index health
     at a glance; log rotation preserves history in `casr_debug.1.log`, never deletes it.

4. **Ordered Conversation Grid**:
   - Ordered **newest to oldest** by default based on session recency and last activity.
   - **Index self-healing**: each scan drops rows for providers that no longer exist in the build and
     rows whose session file has been deleted from disk (the check only fires while the provider's
     session root is present, so an offline drive never wipes the index). Rows with no messages —
     empty shells and catalog-only entries — are kept out of the list entirely.
   - **Multi-Column Sorting**: Click any column header (Provider, Date, Topic/Title, Workspace, Turns, Tools, Size, Session ID) to toggle ascending/descending order.
   - **Column Filtering**: Live dropdown filters by Provider, Workspace repository folder, and Date Range (All Time, Today, Past 7 Days, Past 30 Days).
   - **Row Highlighting & Quick Navigation**: Full keyboard up/down navigation and distinct row highlighting.

5. **Independent Search Results & Deep Content Search**:
   - Metadata search matches conversation title, native name, session ID, workspace, model, and provider into a separate ranked **Search Results** surface. Searching does not remove rows from the **Conversations** library; its provider, workspace, date, and subagent filters remain independent, and library selection is preserved.
   - Selecting a hit opens its conversation in the inspector. Deep-content results show their source, excerpt, role, and message position, and navigate to the matched transcript message without changing the library selection.
   - Turn on `📑 Deep Content (FTS5)` to search locally indexed transcript messages. Hybrid, Keyword, Keyword+, Semantic, Exact, and Regex modes include result attribution; provider/workspace/date filters are pushed into the query where applicable. `All Providers` is limited to providers currently enabled in settings. Searches are cancellable and newer queries supersede stale work.
   - `Ctrl+F` focuses and selects the search text; `Esc` clears it. Saved searches, recent search history, and evidence-report export are available in the search controls.
   - Semantic search currently uses the bundled lightweight hashing embedder. The optional ONNX MiniLM model is not bundled; see [`docs/neural-embeddings.md`](docs/neural-embeddings.md) and `scripts/Download-EmbeddingModel.ps1` to install it.

6. **Interactive Session Inspector & Transcript Viewer**:
   - Split-pane layout with collapsible inspector.
   - **Transcript Preview Tab**: Formatted conversational bubbles with user prompts in distinct blue, assistant reasoning in clean cards, and tool calls in tagged badges. Large transcripts are windowed and virtualized.
   - **Session Details Tab**: Session UUID, Workspace directory, Git repository + branch (resolved read-only from the workspace's `.git` — the `casr --enrich-fs` analogue), Model name, Started At, Last Active At, Message counts, Tool counts, and File path.

7. **One-Click Terminal Resumption**:
   - **Resume in Agent**: Immediately opens a terminal window configured to the session's workspace directory and fires up the exact resume command (e.g. `agy --conversation <uuid> --model "Gemini 3.1 Pro (High)"` or `openclaude --resume <id>`).
   - **Double-Click to Resume**: Double-click any row in the DataGrid to resume immediately.
   - **Conversion Preview (dry-run)**: Resume-With opens a preview dialog first — the GUI form of
     `casr resume --dry-run`. It shows the source → target providers, message/tool/token counts before
     and after packaging, validation warnings, the git repository, the workspace override, and the
     resume command, **without writing anything**. Options live in the same dialog:
     - **Add conversion context (enrich)** — prepends a conversion notice plus a recent-conversation
       snapshot, both marked as synthetic (`casr --enrich`).
     - **Keep reasoning traces** — off by default; another agent's hidden reasoning is dropped.
     - **Verify written session (read-back)** — re-reads the written session and compares it with what
       was written before the terminal opens (`casr` verification step). A message-count mismatch is a
       failure and the conversion is rolled back where safe; format folds (e.g. tool history rendered
       as text) are reported as warnings.
     - **Max context tokens / Max tool output** — context budget (oldest middle turns dropped first,
       goal + recent turns preserved) and per-tool-result truncation, mirroring `--max-context-tokens`
       and `--max-tool-output`.
     - **Launch after converting** — uncheck to only write the converted session; the resume command
       (with a `cd` hint) is copied to the clipboard instead of opening a terminal.
     - **📋 Copy report** — copies the whole preview as a JSON report (the `casr --json` analogue).
     Changing an option re-runs the dry run against the real source; the final write always re-reads the
     source, so it can never be based on a stale preview. The choice of whether to show the dialog at all
     persists via **⚙ Providers → Conversion → Preview before converting**.
   - **Terminal Flexibility**: Choose your preferred terminal in the top bar:
     - **Windows Terminal** (`wt.exe`)
     - **Alacritty** (`alacritty.exe`)
     - **WezTerm** (`wezterm.exe`)
     - **noctty** (`noctty.exe`)
     - **PowerShell 7** (`pwsh.exe`)
     - **Windows PowerShell** (`powershell.exe`)
     - **Command Prompt** (`cmd.exe`)

     Alacritty, WezTerm and noctty host an inner PowerShell that runs the resume command; each is
     located via `PATH` and the usual install directories, and a missing binary degrades to the
     best available shell instead of failing the resume.
   - **Cross-Resume**: Convert an existing session from one agent harness (e.g. Antigravity or Cursor) into another agent's native format (e.g. Grok or OpenClaude) and launch it. Codex can be a read-only source; selecting Codex as a destination launches a new session because native history import is unavailable.
   - **Context Menu Actions**: Right-click any row for `Resume Session Immediately`, `Cross-Resume into Other Agent...`, `Copy CLI Command`, `Open Workspace Folder`, and `Open in VS Code`.
   - The disaster-recovery window can include Codex active/archive rollouts and `session_index.jsonl`; it excludes `auth.json` and other credentials/caches. Existing settings files retain their explicit provider choices, so users with an existing `settings.json` must enable Codex in the Providers menu if they want to discover it.

---

## Quick Start

### Option 1: Run via Launcher Script
Double-click `run.cmd` in the root folder, or run in PowerShell:
```pwsh
.\run.ps1
```
Both **rebuild in Release on every launch** before starting the app, so what you open always
matches the source you just changed (`dotnet build`/`dotnet test` only produce Debug, which
the app never runs). Use `.\run.ps1 -SkipBuild` for a fast relaunch — never to verify a code
change (it also bypasses the ReleaseBuildGuard staleness test).

### Option 2: Run with `dotnet`
```pwsh
dotnet run --project src\Casr.App\Casr.App.csproj -c Release
```

### Option 3: Run the Precompiled Binary
```pwsh
.\src\Casr.App\bin\Release\net8.0-windows\Casr.App.exe
```

### Option 4: Desktop Shortcut
```pwsh
pwsh -File .\scripts\Create-Shortcut.ps1   # optional -SkipBuild
```
Creates a `CASR.lnk` on your Desktop (and re-run it any time to re-create/refresh it). The shortcut targets the Release exe and picks up the lightning-bolt icon embedded in the binary.
Note: the shortcut points at the exe on disk — it does not rebuild. After changing or
pulling source, launch via `run.cmd`/`run.ps1` (which rebuild first); a stale shortcut
opens stale code, and only the ReleaseBuildGuard test will tell you.

---

## Running Automated Tests

### Unit Tests
The suite covers parsing, provider CLI support, user settings persistence, canonical
model normalization, FTS5 SQLite search, export, backup guards and live system
detection. Test counts live here once: the fast hermetic suite
(`Category!=LiveSystem`; current run counts are recorded in `DEV_JOURNAL.md`) excludes tests that touch real
user agent stores on this machine; the full suite adds the `LiveSystem` category.

The fast, hermetic suite (excludes tests that touch real user agent stores on this machine):
```pwsh
dotnet test Casr.sln --filter Category!=LiveSystem
```

The full test suite, including the `LiveSystem` category (requires the real agents installed — Pi, OpenCode, Hermes, etc. — and reads their live session stores; one test builds a real backup archive and alone takes ~2.5 minutes):
```pwsh
dotnet test Casr.sln
```

### Iterative Automated GUI Test Loop
Run the automated testing loop to verify compilation, test suite, GUI window responsiveness, memory bounds, UI automation controls, and clean shutdown across multiple iterations:
```pwsh
pwsh -File .\scripts\test-loop.ps1 -Iterations 3
```

### Conversion Dialog UI Test
Drives the actual Release app through the conversion preview: selects a conversation, opens
Resume-With → Grok Build, asserts the dialog's stats/options/warnings/resume command/repository,
toggles Enrich and confirms the dry run re-runs, uses the write-only path, and requires the app
log to report `verification=passed` when the written session is read back. `GROK_HOME` is
redirected to a temp directory for the run (never the live `~/.grok` store) and cleaned up after.
```pwsh
pwsh -File .\scripts\test-conversion-ui.ps1   # exit 0 = pass
```

---

## Architecture

- **`Casr.sln`**: Solution container.
- **`src/Casr.Core`**:
  - `Configuration/`: `UserSettings` persisted to `%LOCALAPPDATA%\Casr\settings.json`.
  - `Logging/`: Thread-safe file logger `CasrLogger` writing to `%LOCALAPPDATA%\Casr\logs\casr_debug.log`.
  - `Models/`: Canonical IR (`CanonicalSession`, `CanonicalMessage`, `MessageRole`, `ToolCall`, `SessionSummary`).
  - `Providers/`: Native readers and writers (`GrokProvider`, `AntigravityProvider`, `CursorProvider`, `OpenClaudeProvider`, `PiProvider`, `HermesProvider`, `OpenCodeProvider`, read-only `CodexProvider`, `ProviderRegistry`).
  - `Storage/`: `SessionDatabase` in SQLite WAL mode with FTS5 for full-text search indexing.
  - `Services/`: `SessionDiscoveryService`, `SessionResumerService`, `TerminalLauncher`.
- **`src/Casr.App`**: Modern WPF MVVM application with dark Fluent theme, reactive sorting and filtering, transcript viewer, provider management popup, and resumption dialogs.
- **`tests/Casr.Core.Tests`**: Unit and integration test suite.
- **`scripts/test-loop.ps1`**: Automated iterative loop verifying GUI responsiveness and stability.
