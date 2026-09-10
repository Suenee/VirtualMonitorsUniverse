@echo off
rem Backward-compatible launcher retained for existing shortcuts and older scripts.
echo NOTE: vmu-server.cmd is deprecated. Use run.cmd.
call "%~dp0run.cmd" %*
exit /b %ERRORLEVEL%
