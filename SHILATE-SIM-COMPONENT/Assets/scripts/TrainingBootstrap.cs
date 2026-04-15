using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Entry point for headless training mode. Invoked via:
///   Unity.exe -batchmode -nographics -executeMethod TrainingBootstrap.Launch --env-id 0 --timescale 5
/// Configures the scene for remote control from Leda.
/// </summary>
public static class TrainingBootstrap
{
    public static void Launch()
    {
        // Parse command-line arguments
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
                    float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
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

        Debug.Log($"[TrainingBootstrap] env-id={envId}, timescale={timescale}, mqtt={mqttHost}:{mqttPort}");

        // Uncap frame rate for maximum throughput
        Application.targetFrameRate = -1;
        QualitySettings.vSyncCount = 0;

        // Load the scene then configure
        SceneManager.sceneLoaded += (scene, mode) => ConfigureScene(envId, timescale, mqttHost, mqttPort);
        SceneManager.LoadScene("SampleScene");
    }

    static void ConfigureScene(int envId, float timescale, string mqttHost, int mqttPort)
    {
        // Find components
        var broker = Object.FindFirstObjectByType<LedaBroker>();
        var remoteInput = Object.FindFirstObjectByType<RemoteDriveInput>();
        var manualInput = Object.FindFirstObjectByType<ManualDriveInput>();
        var simRunner = Object.FindFirstObjectByType<SimulationRunner>();
        var tsController = Object.FindFirstObjectByType<TimeScaleController>();

        // Configure broker with env prefix and MQTT settings
        if (broker != null)
        {
            broker.Configure(mqttHost, mqttPort, $"env{envId}");
        }

        // Enable remote input, disable manual
        if (remoteInput != null) remoteInput.enabled = true;
        if (manualInput != null) manualInput.enabled = false;
        if (simRunner != null) simRunner.enabled = false;

        // Set timescale
        if (tsController != null)
        {
            // TimeScaleController reads --timescale from command line in Awake()
            // but we force it here too in case scene loaded after Awake
            Time.timeScale = Mathf.Clamp(timescale, 1f, 10f);
            Time.fixedDeltaTime = 0.02f * Time.timeScale;
        }

        Debug.Log($"[TrainingBootstrap] Scene configured for training (env{envId}, {timescale}x)");
    }
}
