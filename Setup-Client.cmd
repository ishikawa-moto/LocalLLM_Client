@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -NoExit -File "%~dp0Setup-Client.ps1" -CertificatePfx "%~dp0certs\client.pfx"
