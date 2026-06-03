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
        CarFactory.BuildGround();
        GameObject car = CarFactory.BuildCar(new Vector3(32.5f, 0.5f, 0f));

        Selection.activeGameObject = car;
        Debug.Log("[CarBuilder] Car scene built. Manual drive (WASD) active by default.");
    }
}
