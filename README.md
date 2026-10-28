# QuickGrid dispose race repro

This repro shows that `QuickGrid` leaks its `document.body` event listeners when it is disposed while its JS `init` call is still pending.

The fix for dotnet/aspnetcore#47173 (dotnet/aspnetcore#62840, shipped in .NET 10) checks for disposal after the module `import`, but not after `init`. So if the grid is disposed while the `init` reply is on its way back, `stop` is never called.

## What is in it

- `GridLab/` is a Blazor Web App (Interactive Server) that uses the QuickGrid 10.0.14 package. The `/grid` page has a grid and a link to `/other`.
- `Runner/` drives the app with Microsoft.Playwright.
  - It counts the QuickGrid listeners that are really on `document.body` by wrapping `EventTarget.prototype.addEventListener` and `removeEventListener`.
  - It appends a small wrapper to `QuickGrid.razor.js`. The wrapper runs the original `init` unchanged and can delay its reply. This stands in for network latency, so the timing does not depend on luck.
- `results/` holds the output of a run on 2026-10-27.

## How to run

```powershell
dotnet build Runner/Runner.csproj
pwsh Runner/bin/Debug/net10.0/playwright.ps1 install chromium

# Terminal 1
dotnet run --project GridLab/GridLab.csproj --launch-profile http

# Terminal 2
dotnet run --project Runner/Runner.csproj -- http://localhost:5181 chromium
```

## Result

| Scenario | `init` calls | `stop` calls | Listeners left on `body` |
| --- | --- | --- | --- |
| 1. Leave after `init` has replied | 1 | 1 | 0 |
| 2. Leave while the `init` reply is in flight | 1 | 0 | 3 |
| 3. Same as 2, 3 times in one circuit | 3 | 0 | 9 |
| 4. Leave while the module is loading (fixed by dotnet/aspnetcore#62840) | 0 | 0 | 0 |

In scenarios 2 and 3, the leaked listeners also keep the removed `<table>` element alive.
