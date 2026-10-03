# Isolated Windows IPC smoke

Run from the repository root:

```powershell
dotnet run --project tests/Eslee.TrayFolder.OsSmoke/Eslee.TrayFolder.OsSmoke.csproj
```

The harness starts only its own console host/secondary children. It exercises the current production `TrayHostServer`, `SingleInstanceManager`, and `ConfigService` with a random pipe/mutex/event namespace and a temporary data directory. Two host lifecycles cover config persistence, cross-process activation, client registration, hosted mode, menu and command responses, shutdown EOF, and reconnect to a new process. The temporary directory is deleted afterward.

It does not launch installed eslee apps, use the default user pipe or settings directory, render the tray UI, or verify icon visibility and window placement. Child modes are internal implementation details; invoke the harness without arguments.
