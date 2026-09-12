@echo off
setlocal
pushd %~dp0
powershell -ExecutionPolicy Bypass -File "scripts\test\test-game.ps1"
popd
endlocal