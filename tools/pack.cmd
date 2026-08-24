@echo off
REM Double-clickable wrapper for pack.ps1.
REM
REM Explorer runs a .ps1 in a console that closes the instant it exits, and the default execution
REM policy refuses an unsigned script. Double-clicking pack.ps1 therefore flashes a window and
REM vanishes before the error can be read.
REM
REM This bypasses the policy for that one process, and holds the window open afterwards.
REM
REM Arguments are forwarded:  pack.cmd -AudiosurfDir "D:\...\Audiosurf 2"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0pack.ps1" %*

echo.
pause
