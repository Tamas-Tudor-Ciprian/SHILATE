using UnityEngine;
using UnityEditor;



/// this will spawn a couple rectangles that the car can avoid




public static class ObstacleBuilder
{
	[MenuItem("SHILATE/Build Obstacles")]
	static void BuildObstacles()
	{
		GameObject obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
		obstacle.name = "Obstacle";
		obstacle.transform.position = new Vector3(0.0f,0.0f,10.0f);
		obstacle.transform.localScale = new Vector3(10.0f,5.0f,1.0f);
		obstacle.GetComponent<Renderer>().material = new Material(Shader.Find("Universal Render Pipeline/Lit")){color = Color.blue};
	}
}
