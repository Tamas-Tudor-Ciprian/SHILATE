using UnityEngine;
using System.IO;

/// <summary>
/// ScriptableObject storing all RL training configuration for the SHILATE
/// single-environment Editor training flow.
/// Create via Assets → Create → SHILATE → Training Settings.
/// </summary>
[CreateAssetMenu(fileName = "TrainingSettings", menuName = "SHILATE/Training Settings")]
public class TrainingSettings : ScriptableObject
{
    [Header("Python Environment")]
    [Tooltip("Relative path to Python venv from Unity project root (use forward slashes)")]
#if UNITY_EDITOR_WIN
    public string venvPath = "../leda/leda-controller/venv";
#else
    public string venvPath = "../leda/leda-controller/.venv";
#endif

    [Tooltip("Relative path to train.py from Unity project root")]
    public string trainScriptPath = "../leda/leda-controller/train.py";

    [Header("Training Parameters")]

    [Tooltip("PPO learning rate")]
    public float learningRate = 3e-4f;

    [Tooltip("Discount factor. 0.999 gives ~1000-step effective horizon, matching the episode length 
             "so the finish bonus is meaningful. Lower values (e.g. 0.99) cause the agent to ignore 
             "rewards near the end of the lap.")]
    public float gamma = 0.999f;

    [Tooltip("Steps per rollout")]
    public int nSteps = 2048;

    [Tooltip("Minibatch size")]
    public int batchSize = 64;

    [Header("MQTT")]
    [Tooltip("MQTT broker hostname")]
    public string mqttHost = "localhost";

    [Tooltip("MQTT broker port")]
    public int mqttPort = 1883;

    [Header("Output")]
    [Tooltip("Directory to save trained models")]
    public string savePath = "../models";

    [Tooltip("Directory for TensorBoard logs")]
    public string logDir = "../logs/tensorboard";

    [Header("Headless Training")]
    [Tooltip("Path to the headless Unity build executable (used by the Headless Orchestrator)")]
    public string unityBuildPath = "";

    [Tooltip("Number of parallel headless environments to launch")]
    public int numHeadlessEnvs = 2;

    [Tooltip("Launch Unity instances with graphics (windowed) instead of -nographics. " +
             "Useful for debugging — visual output lets you see what the agent is doing.")]
    public bool unityWithGraphics = false;

    [Tooltip("When true: one Python process trains one shared model using SubprocVecEnv across all N Unity instances. " +
             "When false: each Unity instance gets its own independent Python process and model.")]
    public bool headlessSingleModelMode = true;

    [Tooltip("Path to a .zip model to resume all headless envs from (leave empty to train from scratch)")]
    public string headlessResumeModelPath = "";

    [Header("Resume Training")]
    [Tooltip("Path to a .zip model file to resume from (leave empty to train from scratch)")]
    public string resumeModelPath = "";

    /// <summary>
    /// Returns the absolute path to the venv directory.
    /// Falls back to the platform-appropriate alternative folder name
    /// (.venv on Linux/Mac, venv on Windows) if the stored path does not exist.
    /// </summary>
    public string GetAbsoluteVenvPath()
    {
        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        string candidate = Path.GetFullPath(Path.Combine(projectRoot, venvPath.Replace('/', Path.DirectorySeparatorChar)));
        if (Directory.Exists(candidate))
            return candidate;

        string parentDir = Path.GetDirectoryName(candidate);
        string folderName = Path.GetFileName(candidate);
        string altName = folderName == ".venv" ? "venv" : folderName == "venv" ? ".venv" : null;
        if (altName != null)
        {
            string altPath = Path.Combine(parentDir, altName);
            if (Directory.Exists(altPath))
                return altPath;
        }
        return candidate; // return original so the error message is informative
    }

    /// <summary>Returns the absolute path to train.py.</summary>
    public string GetAbsoluteTrainScriptPath()
    {
        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        return Path.GetFullPath(Path.Combine(projectRoot, trainScriptPath.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>Returns the working directory for the Python process (directory containing train.py).</summary>
    public string GetWorkingDirectory()
    {
        return Path.GetDirectoryName(GetAbsoluteTrainScriptPath());
    }

    /// <summary>Returns the absolute path to ai_driver.py (same directory as train.py).</summary>
    public string GetAbsoluteAiDriverPath()
    {
        return Path.Combine(GetWorkingDirectory(), "ai_driver.py");
    }

    /// <summary>Builds the command-line arguments for ai_driver.py inference.</summary>
    public string BuildInferenceArgs(string modelPath)
    {
        var args = new System.Text.StringBuilder();
        args.Append($"--model \"{modelPath}\" ");
        args.Append($"--mqtt-host {mqttHost} ");
        args.Append($"--mqtt-port {mqttPort}");
        return args.ToString();
    }

    /// <summary>
    /// Builds the command-line arguments for train.py.
    /// Single env, real time — no parallel/timescale flags.
    /// </summary>
    public string BuildCommandLineArgs()
    {
        var args = new System.Text.StringBuilder();

        args.Append($"--learning-rate {learningRate.ToString(System.Globalization.CultureInfo.InvariantCulture)} ");
        args.Append($"--gamma {gamma.ToString(System.Globalization.CultureInfo.InvariantCulture)} ");
        args.Append($"--n-steps {nSteps} ");
        args.Append($"--batch-size {batchSize} ");
        args.Append($"--mqtt-host {mqttHost} ");
        args.Append($"--mqtt-port {mqttPort} ");

        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        string absSavePath = Path.GetFullPath(Path.Combine(projectRoot, savePath.Replace('/', Path.DirectorySeparatorChar)));
        string absLogDir = Path.GetFullPath(Path.Combine(projectRoot, logDir.Replace('/', Path.DirectorySeparatorChar)));

        args.Append($"--save-path \"{absSavePath}\" ");
        args.Append($"--log-dir \"{absLogDir}\"");

        if (!string.IsNullOrEmpty(resumeModelPath))
            args.Append($" --resume-model \"{resumeModelPath}\"");

        return args.ToString();
    }
}
