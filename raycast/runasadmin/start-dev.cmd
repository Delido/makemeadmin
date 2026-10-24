@echo off
rem Startet die RunAsAdmin-Raycast-Extension im Dev-Modus (haelt sie in Raycast
rem registriert). Wird per Autostart beim Login gestartet. Fenster laeuft versteckt.
cd /d "%~dp0"
call npx ray develop
