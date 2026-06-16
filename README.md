# Public House 28 — Staff Scheduler

A desktop app for managing the employee shift schedule at the bar **Public House 28**,
with a built-in AI assistant: type plain English like *"Put Sarah on bar Friday 5pm to close"*
and Claude updates the schedule for you.

- **UI:** Avalonia (cross-platform .NET — develop on Linux/Mac, ship a native Windows `.exe`)
- **Data:** local SQLite file (`schedule.db`), created automatically next to the app
- **AI:** Anthropic Claude (`claude-opus-4-8`) via tool calling — *your* code makes every change

## How the assistant works

You type a request → Claude decides which **tool** to call (`add_shift`, `assign_shift`,
`list_shifts`, etc.) → the app runs that tool against the SQLite database → Claude replies
with a plain-English confirmation, and the schedule view refreshes. Claude never touches the
database directly; it can only ask the app to perform the defined operations.

## Prerequisites

- **.NET 8 SDK** (already installed on this machine at `~/.dotnet`; `dotnet` is on your PATH
  via `~/.bashrc`). Verify with `dotnet --version`.
- An **Anthropic API key** for the assistant. Get one at https://console.anthropic.com/.

## Setup

Set your API key (the app reads the `ANTHROPIC_API_KEY` environment variable):

```bash
export ANTHROPIC_API_KEY="sk-ant-..."      # add to ~/.bashrc to make it permanent
```

Without a key, the schedule view still works fully; only the assistant is disabled (it will
say so on startup).

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
| `Models.cs` | `Employee` and `Shift` data classes |
| `SchedulerService.cs` | SQLite storage + all schedule operations, incl. availability & conflict detection (the source of truth) |
| `ShiftTime.cs` | Parses free-text times ("5pm", "Close") to detect overlapping shifts |
| `ClaudeAssistant.cs` | Anthropic SDK, tool definitions, and the tool-calling loop |
| `MainWindow.axaml` / `.cs` | The window: weekly grid (days as rows, roles as columns) with week navigation + assistant chat |
| `SelfTest.cs` | Headless `--selftest` check of the data layer |

## Things to try once it's running

- "Add a bartender shift Saturday 6pm to close and put Marcus on it"
- "Who's working Friday?"
- "Show me the open shifts"
- "Add a new server named Dana who can also host"
- "Move shift 3 to Priya"
- "Swap shifts 1 and 2" — exchanges who's on each (refused if it would double-book, unless you override)
- "Sarah's available Fridays 4pm to close" — then try to book her outside that, and it'll refuse
- "Put Sarah on a second shift Friday night" — blocked as a double-booking unless you say to override
