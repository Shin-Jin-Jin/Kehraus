@echo off
rem Kehraus starten (ohne Konsolenfenster)
start "" powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "%~dp0Kehraus.ps1"
