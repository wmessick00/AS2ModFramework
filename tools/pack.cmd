@echo off
REM Double-clickable wrapper for pack.ps1.
REM
REM Explorer runs a .ps1 in a console that closes the instant it exits, and PowerShell's default
REM execution policy refuses to run unsigned scripts at all -- so double-clicking pack.ps1 directly
REM flashes a window and vanishes before the error can be read. This runs it with the policy
REM bypassed for that one process only, and holds the window open afterwards.
REM
REM Arguments are forwarded:  pack.cmd -AudiosurfDir "D:\...\Audiosurf 2"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0pack.ps1" %*

echo.
pause
