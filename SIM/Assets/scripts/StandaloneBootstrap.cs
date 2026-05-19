using UnityEngine;

/// <summary>
/// Auto-configures the scene for headless/standalone training mode.
/// Reads --env-id, --timescale, --mqtt-host, --mqtt-port from the command line
/// and wires up all components for remote RL training.
/// Uses RuntimeInitializeOnLoadMethod so no GameObject attachment is needed.
/// </summary>
public static class StandaloneBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void OnSceneLoaded()
    {
#if !UNITY_EDITOR
        if (Application.isBatchMode || System.Array.Exists(
                System.Environment.GetCommandLineArgs(), a => a == "--env-id"))
        {
            Configure();
        }
#endif
    }

    static void Configure()
    {
        string[] args = System.Environment.GetCommandLineArgs();
        int envId = 0;
        float timescale = 1f;
        string mqttHost = "localhost";
        int mqttPort = 1883;

        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--env-id":
                    int.TryParse(args[i + 1], out envId);
                    break;
                case "--timescale":
                    float.TryParse(args[i + 1],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out timescale);
                    break;
                case "--mqtt-host":
                    mqttHost = args[i + 1];
                    break;
                case "--mqtt-port":
                    int.TryParse(args[i + 1], out mqttPort);
                    break;
            }
        }

        Application.targetFrameRate = -1;
        QualitySettings.vSyncCount = 0;

        var broker = Object.FindFirstObjectByType<LedaBroker>();
        var remoteInput = Object.FindFirstObjectByType<RemoteDriveInput>();
        var manualInput = Object.FindFirstObjectByType<ManualDriveInput>();
        var simRunner = Object.FindFirstObjectByType<SimulationRunner>();
        var trainingBridge = Object.FindFirstObjectByType<TrainingBridge>();
        var obstacleCourse = Object.FindFirstObjectByType<ObstacleCourse>();

        Debug.Log($"[StandaloneBootstrap] Found: broker={broker != null}, remote={remoteInput != null}, " +
            $"manual={manualInput != null}, training={trainingBridge != null}, course={obstacleCourse != null}");

        if (broker != null)
            broker.Configure(mqttHost, mqttPort, $"env{envId}");

        if (trainingBridge != null)
        {
            if (trainingBridge.broker == null) trainingBridge.broker = broker;
            if (trainingBridge.vehicle == null)
                trainingBridge.vehicle = Object.FindFirstObjectByType<VehicleController>();
            if (trainingBridge.raycastSensor == null)
                trainingBridge.raycastSensor = Object.FindFirstObjectByType<RaycastSensor>();
            if (trainingBridge.obstacleCourse == null)
                trainingBridge.obstacleCourse = obstacleCourse;
            trainingBridge.enabled = true;
        }

        if (obstacleCourse != null)
        {
            if (obstacleCourse.broker == null) obstacleCourse.broker = broker;
            if (obstacleCourse.trainingBridge == null) obstacleCourse.trainingBridge = trainingBridge;
        }

        if (remoteInput != null) remoteInput.enabled = true;
        if (manualInput != null) manualInput.enabled = false;
        if (simRunner != null) simRunner.enabled = false;

        Time.timeScale = Mathf.Clamp(timescale, 1f, 10f);
        Time.fixedDeltaTime = 0.02f * Time.timeScale;

        Debug.Log($"[StandaloneBootstrap] Configured: env{envId}, {timescale}x, mqtt={mqttHost}:{mqttPort}");
    }
}
