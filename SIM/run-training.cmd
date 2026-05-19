@ECHO OFF
:: run-training.cmd — Launch a single Unity headless instance for training
::
:: Usage:
::   run-training.cmd [ENV_ID] [TIMESCALE] [MQTT_HOST] [MQTT_PORT]
::
:: Defaults: ENV_ID=0, TIMESCALE=5, MQTT_HOST=localhost, MQTT_PORT=1883

SET ENV_ID=%1
SET TIMESCALE=%2
SET MQTT_HOST=%3
SET MQTT_PORT=%4

IF "%ENV_ID%"=="" SET ENV_ID=0
IF "%TIMESCALE%"=="" SET TIMESCALE=5
IF "%MQTT_HOST%"=="" SET MQTT_HOST=localhost
IF "%MQTT_PORT%"=="" SET MQTT_PORT=1883

:: Find Unity — adjust path to your installed version
SET UNITY_EXE=C:\Users\uiv80988\Documents\Software-Hardware-In-Loop-Automotive-Test-Enviroment-SHILATE\SHILATE-SIM-COMPONENT\build\SHILATE.exe
IF NOT EXIST "%UNITY_EXE%" (
    ECHO ERROR: Unity not found at %UNITY_EXE%
    ECHO Set UNITY_EXE to your Unity editor path.
    EXIT /B 1
)

SET PROJECT_DIR=%~dp0

ECHO [run-training] Launching Unity headless (env-id=%ENV_ID%, timescale=%TIMESCALE%, mqtt=%MQTT_HOST%:%MQTT_PORT%)

"%UNITY_EXE%" ^
    -batchmode ^
    -nographics ^
    -projectPath "%PROJECT_DIR%" ^
    -executeMethod TrainingBootstrap.Launch ^
    --env-id %ENV_ID% ^
    --timescale %TIMESCALE% ^
    --mqtt-host %MQTT_HOST% ^
    --mqtt-port %MQTT_PORT% ^
    -logFile "%PROJECT_DIR%\Logs\training-env%ENV_ID%.log"
