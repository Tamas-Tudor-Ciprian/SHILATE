@ECHO OFF
:: run-training.cmd — Thin wrapper around run-training.ps1.
::
:: All real logic (Unity launch + Windows sleep prevention via
:: SetThreadExecutionState) lives in the PowerShell script so the sleep
:: inhibitor is held for exactly the lifetime of the Unity process and is
:: released on Ctrl+C / errors via PowerShell's try/finally.
::
:: Usage:
::   run-training.cmd [ENV_ID] [TIMESCALE] [MQTT_HOST] [MQTT_PORT]
::
:: Defaults: ENV_ID=0, TIMESCALE=5, MQTT_HOST=localhost, MQTT_PORT=1883
::
:: Set the UNITY_EXE environment variable to override the default Unity path.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-training.ps1" %*
EXIT /B %ERRORLEVEL%

