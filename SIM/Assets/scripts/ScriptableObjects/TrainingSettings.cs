using UnityEngine;
using System.IO;

/// <summary>
/// ScriptableObject storing all RL training configuration.
/// Create via Assets → Create → SHILATE → Training Settings.
/// </summary>
[CreateAssetMenu(fileName = "TrainingSettings", menuName = "SHILATE/Training Settings")]
public class TrainingSettings : ScriptableObject
{
    [Header("Python Environment")]
    [Tooltip("Relative path to Python venv from Unity project root (use forward slashes)")]
    public string venvPath = "../leda/leda-controller/venv";

    [Tooltip("Relative path to train.py from Unity project root")]
    public string trainScriptPath = "../leda/leda-controller/train.py";

    [Header("Training Parameters")]
    [Tooltip("Number of parallel Unity environments (ignored in debug mode)")]
    [Range(1, 16)]
    public int numEnvs = 4;

    [Tooltip("Unity Time.timeScale for training speed")]
    [Range(1f, 10f)]
    public float timescale = 5f;

    [Tooltip("Total training timesteps")]
    public int totalTimesteps = 100000;

    [Tooltip("PPO learning rate")]
    public float learningRate = 3e-4f;

    [Tooltip("Steps per rollout per environment")]
    public int nSteps = 2048;

    [Tooltip("Minibatch size")]
    public int batchSize = 64;

    [Header("Sensor Configuration")]
    [Tooltip("Number of raycast sensors (0 = auto-detect from RaycastSensor in scene)")]
    public int rayCount = 0;

    [Header("Debug Mode")]
    [Tooltip("Timescale to use in debug mode (Editor Play mode)")]
    [Range(1f, 5f)]
    public float debugTimescale = 1f;

    [Header("MQTT")]
    [Tooltip("MQTT broker hostname")]
    public string mqttHost = "localhost";

    [Tooltip("MQTT broker port")]
    public int mqttPort = 1883;

    [Header("Output")]
    [Tooltip("Directory to save trained models")]
    public string savePath = "../leda/leda-controller/models";

    [Tooltip("Directory for TensorBoard logs")]
    public string logDir = "../leda/leda-controller/logs";

    /// <summary>
    /// Returns the absolute path to the venv directory.
    /// </summary>
    public string GetAbsoluteVenvPath()
    {
        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        return Path.GetFullPath(Path.Combine(projectRoot, venvPath.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>
    /// Returns the absolute path to train.py.
    /// </summary>
    public string GetAbsoluteTrainScriptPath()
    {
        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        return Path.GetFullPath(Path.Combine(projectRoot, trainScriptPath.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>
    /// Returns the working directory for the Python process (directory containing train.py).
    /// </summary>
    public string GetWorkingDirectory()
    {
        return Path.GetDirectoryName(GetAbsoluteTrainScriptPath());
    }

    /// <summary>
    /// Builds the command-line arguments for train.py.
    /// </summary>
    public string BuildCommandLineArgs(bool debugMode = false)
    {
        var args = new System.Text.StringBuilder();

        int envCount = debugMode ? 1 : numEnvs;
        float scale = debugMode ? debugTimescale : timescale;

        args.Append($"--num-envs {envCount} ");
        args.Append($"--timescale {scale.ToString(System.Globalization.CultureInfo.InvariantCulture)} ");
        args.Append($"--total-timesteps {totalTimesteps} ");
        args.Append($"--learning-rate {learningRate.ToString(System.Globalization.CultureInfo.InvariantCulture)} ");
        args.Append($"--n-steps {nSteps} ");
        args.Append($"--batch-size {batchSize} ");
        args.Append($"--mqtt-host {mqttHost} ");
        args.Append($"--mqtt-port {mqttPort} ");

        int rays = GetEffectiveRayCount();
        if (rays > 0)
            args.Append($"--ray-count {rays} ");

        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        string absSavePath = Path.GetFullPath(Path.Combine(projectRoot, savePath.Replace('/', Path.DirectorySeparatorChar)));
        string absLogDir = Path.GetFullPath(Path.Combine(projectRoot, logDir.Replace('/', Path.DirectorySeparatorChar)));

        args.Append($"--save-path \"{absSavePath}\" ");
        args.Append($"--log-dir \"{absLogDir}\"");

        return args.ToString();
    }

    /// <summary>
    /// Returns the effective ray count: from settings if > 0, otherwise from RaycastSensor in scene.
    /// </summary>
    public int GetEffectiveRayCount()
    {
        if (rayCount > 0)
            return rayCount;

        // Try to find RaycastSensor in scene and read its rayCount via reflection
        var sensor = Object.FindFirstObjectByType<RaycastSensor>();
        if (sensor != null)
        {
            var field = typeof(RaycastSensor).GetField("rayCount",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field != null)
                return (int)field.GetValue(sensor);
        }

        return 21; // fallback default
    }
}
