using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

/// <summary>
/// Entry point for headless training mode. Invoked via:
///   Unity.exe -batchmode -nographics -executeMethod TrainingBootstrap.Launch --env-id 0 --timescale 5
/// Configures the scene for remote control from Leda.
/// </summary>
public static class TrainingBootstrap
{
#if UNITY_EDITOR
    static int _envId;
    static float _timescale;
    static string _mqttHost;
    static int _mqttPort;
#endif

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

        // Keep simulating even when the Editor/Player window loses focus.
        // Belt-and-suspenders: ProjectSettings already has runInBackground=1,
        // but force it here so training never silently pauses if that flag is
        // toggled off in the future.
        Application.runInBackground = true;

        // Uncap frame rate for maximum throughput
        Application.targetFrameRate = -1;
        QualitySettings.vSyncCount = 0;

#if UNITY_EDITOR
        // -executeMethod runs in editor context (not play mode).
        // Open the scene, enter play mode, then configure once playing.
        _envId = envId;
        _timescale = timescale;
        _mqttHost = mqttHost;
        _mqttPort = mqttPort;

        EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity");
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
        EditorApplication.EnterPlaymode();
#else
        // Standalone build: LoadScene works normally
        SceneManager.sceneLoaded += (scene, mode) => ConfigureScene(envId, timescale, mqttHost, mqttPort);
        SceneManager.LoadScene("SampleScene");
#endif
    }

#if UNITY_EDITOR
    static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.EnteredPlayMode)
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            ConfigureScene(_envId, _timescale, _mqttHost, _mqttPort);
        }
    }
#endif

    static void ConfigureScene(int envId, float timescale, string mqttHost, int mqttPort)
    {
        // Find components
        var broker = Object.FindFirstObjectByType<LedaBroker>();
        var remoteInput = Object.FindFirstObjectByType<RemoteDriveInput>();
        var manualInput = Object.FindFirstObjectByType<ManualDriveInput>();
        var simRunner = Object.FindFirstObjectByType<SimulationRunner>();
        var tsController = Object.FindFirstObjectByType<TimeScaleController>();
        var trainingBridge = Object.FindFirstObjectByType<TrainingBridge>();
        var obstacleCourse = Object.FindFirstObjectByType<ObstacleCourse>();

        // Configure broker with env prefix and MQTT settings
        if (broker != null)
        {
            broker.Configure(mqttHost, mqttPort, $"env{envId}");
        }

        // Wire up TrainingBridge references if not set in inspector
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

        // Wire up ObstacleCourse → TrainingBridge reference
        if (obstacleCourse != null)
        {
            if (obstacleCourse.broker == null) obstacleCourse.broker = broker;
            if (obstacleCourse.trainingBridge == null) obstacleCourse.trainingBridge = trainingBridge;
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
