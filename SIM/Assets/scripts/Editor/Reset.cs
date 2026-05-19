using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

// this is a editor script that will reset the scene so you can test diffs easily




public static class SceneReset
{
	[MenuItem("SHILATE/Reset current scene")]
	static void ResetScene(){


		if (EditorUtility.DisplayDialog("Reset Scene", "Are you sure? All progress will be discarded","Yes", "No"))
		{



			//get all the objects in the scene

			GameObject[] allObjects = EditorSceneManager.GetActiveScene().GetRootGameObjects();

		
			foreach (GameObject obj in allObjects)
			{
				Undo.DestroyObjectImmediate(obj);
			}

			// recreate the main camera


			GameObject camObj = new GameObject("Main Camera");
			camObj.AddComponent<Camera>();
			camObj.AddComponent<AudioListener>();
			camObj.tag = "MainCamera";
			camObj.transform.position = new Vector3(0, 1, -10);
			Undo.RegisterCreatedObjectUndo (camObj, "Create Default Camera");


			//recreate the directional camera


			GameObject lightObj = new GameObject("Directional Light");
			Light light = lightObj.AddComponent<Light>();
			light.type = LightType.Directional;
			lightObj.transform.rotation = Quaternion.Euler(50, -30, 0);
			Undo.RegisterCreatedObjectUndo(lightObj, "Create Default Light");


			// mark the scene as modified
			EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());


			Debug.Log("Just cleared the scene.");
	
		
			
		}
		
							}
		
		
}
