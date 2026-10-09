@echo off
rem run-chat.cmd                                  -> server + alice + bob
rem run-chat.cmd -Users alice,bob,carol -Room dev -Name my-chat
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-chat.ps1" %*
