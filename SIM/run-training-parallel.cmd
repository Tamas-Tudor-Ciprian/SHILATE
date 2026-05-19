@ECHO OFF
:: run-training-parallel.cmd — Launch N Unity headless instances for parallel RL training
::
:: Usage:
::   run-training-parallel.cmd [NUM_ENVS] [TIMESCALE] [MQTT_HOST] [MQTT_PORT]
::
:: Defaults: NUM_ENVS=4, TIMESCALE=5, MQTT_HOST=localhost, MQTT_PORT=1883

SET NUM_ENVS=%1
SET TIMESCALE=%2
SET MQTT_HOST=%3
SET MQTT_PORT=%4

IF "%NUM_ENVS%"=="" SET NUM_ENVS=4
IF "%TIMESCALE%"=="" SET TIMESCALE=5
IF "%MQTT_HOST%"=="" SET MQTT_HOST=localhost
IF "%MQTT_PORT%"=="" SET MQTT_PORT=1883

ECHO [parallel] Launching %NUM_ENVS% Unity headless instances (timescale=%TIMESCALE%)

SET /A LAST_ENV=%NUM_ENVS%-1
FOR /L %%i IN (0,1,%LAST_ENV%) DO (
    ECHO [parallel] Starting env%%i ...
    START "SHILATE-env%%i" /MIN "%~dp0run-training.cmd" %%i %TIMESCALE% %MQTT_HOST% %MQTT_PORT%
    TIMEOUT /T 2 /NOBREAK >NUL
)

ECHO [parallel] All %NUM_ENVS% instances launched.
ECHO [parallel] Close this window or press Ctrl+C to stop.
PAUSE
