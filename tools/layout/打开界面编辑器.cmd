@echo off
chcp 65001 >nul
cd /d "%~dp0..\.."
node tools\layout\build-editor.mjs . tools\layout\editor.html
for /f "tokens=5" %%a in ('netstat -ano ^| findstr ":8788" ^| findstr "LISTENING"') do taskkill /f /pid %%a >nul 2>nul
start "" /b node tools\layout\serve.mjs 8788
timeout /t 2 >nul
start "" http://127.0.0.1:8788/editor.html
