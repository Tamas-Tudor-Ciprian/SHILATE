using UnityEngine;

/// <summary>
/// Auto-spawns the car scene at runtime using CarFactory — the same factory
/// used by the SHILATE → Build Car Scene editor menu.
/// Runs automatically via [RuntimeInitializeOnLoadMethod] after the scene loads.
/// </summary>
public static class RuntimeSceneBuilder
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Init()
    {
        UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
    }

    static void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
    {
        UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;

        if (Object.FindFirstObjectByType<VehicleController>() != null)
        {
            Debug.Log("[RuntimeSceneBuilder] Scene already has a VehicleController, skipping build.");
            return;
        }

        Debug.Log("[RuntimeSceneBuilder] Scene is empty, building car scene via CarFactory...");
        CarFactory.BuildGround();
        CarFactory.BuildCar(new Vector3(32.5f, 0.5f, 0f));
    }
}
