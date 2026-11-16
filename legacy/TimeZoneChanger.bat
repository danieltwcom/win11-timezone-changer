@echo off
REM Double-click this file to launch the Time Zone Changer GUI.
start "" powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0TimeZoneChanger.ps1"
