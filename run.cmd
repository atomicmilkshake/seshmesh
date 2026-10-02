@echo off
rem Always rebuild Release before launching: the taskbar icon, this script and the
rem desktop shortcut all run the Release exe, while `dotnet build` / `dotnet test`
rem compile Debug — which the user never opens. Delegating to run.ps1 keeps a single
rem build-then-launch code path instead of two that drift apart.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" %*
