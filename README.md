# kite

Lightweight, high-performance terminal agent.

## Principles

- Trust model intelligence: No over-engineered prompt scaffolding or runtime patches without a reproducible failure.
- Append-only context: Maximizes provider prompt prefix caching.
- Explicit non-goals: No sandbox, no permission approvals, no web UI.

## Install

### macOS / Linux

```sh
curl -fsSL https://raw.githubusercontent.com/realloon/kite/main/install.sh | sh
```

### Windows (PowerShell)

```powershell
irm https://raw.githubusercontent.com/realloon/kite/main/install.ps1 | iex
```

## Quick Start

Launch `kite` inside any workspace:

```sh
kite
```

## Commands

- `/connect` - Connect or update provider credentials
- `/model` - Switch model
- `/variants` - Adjust reasoning effort
- `/undo` - Revert last turn and file modifications
- `/compact` - Compact context
- `/new` - Start a new session
- `/sessions` - Switch between sessions
- `/stats` - Show session statistics
- `/exit` - Exit kite
