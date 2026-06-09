using UnityEngine;
using UnityEditor;

/// <summary>
/// Editor utility that builds the car scene via CarFactory — the same path used
/// by RuntimeSceneBuilder at runtime. Access via menu: SHILATE → Build Car Scene.
/// </summary>
public static class CarBuilder
{
    [MenuItem("SHILATE/Build Car Scene")]
    static void BuildCarScene()
    {
        if (Object.FindFirstObjectByType<VehicleController>() != null)
        {
            Debug.LogWarning("[CarBuilder] Scene already contains a VehicleController — reset the scene before building again.");
            EditorUtility.DisplayDialog("Build Car Scene", "Scene is not empty.\nReset the scene before building again.", "OK");
            return;
        }

        CarFactory.BuildGround();
        GameObject car = CarFactory.BuildCar(new Vector3(32.5f, 0.5f, 0f));

        Selection.activeGameObject = car;
        Debug.Log("[CarBuilder] Car scene built. Manual drive (WASD) active by default.");
    }
}
