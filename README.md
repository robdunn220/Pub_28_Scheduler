# Public House 28 — Staff Scheduler

A desktop app for managing the employee shift schedule at the bar **Public House 28**,
with a built-in AI assistant: type plain English like *"Put Sarah on bar Friday 5pm to close"*
and Claude updates the schedule for you.

- **UI:** Avalonia (cross-platform .NET — develop on Linux/Mac, ship a native Windows `.exe`)
- **Data:** local SQLite file (`schedule.db`), created automatically next to the app
- **AI:** swappable — Anthropic Claude *or* Google Gemini (Flash), via tool/function calling.
  *Your* code makes every change either way (see `SchedulerTools.cs`)

## How the assistant works

You type a request → Claude decides which **tool** to call (`add_shift`, `assign_shift`,
`list_shifts`, etc.) → the app runs that tool against the SQLite database → Claude replies
with a plain-English confirmation, and the schedule view refreshes. Claude never touches the
database directly; it can only ask the app to perform the defined operations.

The assistant can also **export the weekly schedule** to a **PDF or PNG** (`export_schedule`):
ask *"download a PDF of this week's schedule"* and it renders the grid currently on screen,
saves it to your **Downloads** folder, and opens it. Say *"as a PNG"* for an image instead, or
name another week to export that one.

## Prerequisites

- **.NET 8 SDK** (already installed on this machine at `~/.dotnet`; `dotnet` is on your PATH
  via `~/.bashrc`). Verify with `dotnet --version`.
- An API key for **one** assistant provider:
  - **Anthropic Claude** — get one at https://console.anthropic.com/
  - **Google Gemini** — get one at https://aistudio.google.com/apikey

## Setup

Set the API key for whichever provider you want to use:

```bash
export ANTHROPIC_API_KEY="sk-ant-..."      # Claude
# or
export GEMINI_API_KEY="..."                # Gemini (Flash)
```

By default the app uses Claude; if only a Gemini key is present it uses Gemini. To force a
provider regardless of which keys are set:

```bash
export SCHEDULER_AI_PROVIDER=gemini        # or: claude
export GEMINI_MODEL=gemini-2.5-flash       # optional; this is the default Flash model
```

Without any key, the schedule view still works fully; only the assistant is disabled (it will
say so on startup, naming the provider and the env var it expects).

## Run

```bash
dotnet run
```

Headless data-layer sanity check (no GUI, no key needed):

```bash
dotnet run -- --selftest
```

## Build a Windows `.exe` for the bar's PC

From this Linux machine you can cross-compile a self-contained Windows build (no .NET install
needed on the target PC):

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The `.exe` lands in `bin/Release/net8.0/win-x64/publish/`. Copy that folder to the Windows PC.
Set `ANTHROPIC_API_KEY` as a Windows environment variable there too.

## Project layout

| File | Purpose |
|------|---------|
| `Models.cs` | `Employee` (incl. FOH/BOH area) and `Shift` data classes |
| `SchedulerService.cs` | SQLite storage + all schedule operations, incl. availability & conflict detection (the source of truth) |
| `ShiftTime.cs` | Parses free-text times ("5pm", "Close") to detect overlapping shifts |
| `SchedulerTools.cs` | Provider-agnostic tool definitions, system prompt, and execution (shared) |
| `SchedulePdfExporter.cs` | Renders a week to a PDF/PNG (QuestPDF) laid out like the on-screen grid, saved to Downloads |
| `IAssistant.cs` / `AssistantFactory.cs` | Assistant interface + provider selection (Claude vs Gemini) |
| `ClaudeAssistant.cs` | Anthropic SDK + Claude's tool-calling loop |
| `GeminiAssistant.cs` | Google Gemini REST + function-calling loop (no SDK dependency) |
| `MainWindow.axaml` / `.cs` | The window: weekly grid (days as rows, roles as columns) with week navigation + assistant chat |
| `SelfTest.cs` | Headless `--selftest` check of the data layer |

## Things to try once it's running

- "Add a bartender shift Saturday 6pm to close and put Marcus on it"
- "Who's working Friday?"
- "Show me the open shifts"
- "Add a new server named Dana" (front of house) or "Hire Alex in the kitchen" (back of house)
- "Move Marcus to back of house" — the grid regroups him under the BOH header
- "Move shift 3 to Priya"
- "Swap shifts 1 and 2" — exchanges who's on each (refused if it would double-book, unless you override)
- "Sarah's available Fridays 4pm to close" — then try to book her outside that, and it'll refuse
- "Put Sarah on a second shift Friday night" — blocked as a double-booking unless you say to override
- "Download a PDF of this week's schedule" (or "export it as a PNG") — saves to your Downloads folder and opens it
