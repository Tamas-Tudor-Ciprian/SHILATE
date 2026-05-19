using UnityEngine;

/// <summary>
/// Smooth third-person follow camera for the vehicle.
/// Follows behind and above the target, looking at it.
/// </summary>
public class CameraFollow : MonoBehaviour
{
    [Tooltip("The transform to follow (car root)")]
    public Transform target;

    [Tooltip("Offset behind and above the target in local space")]
    public Vector3 offset = new Vector3(0f, 4f, -8f);

    [Tooltip("How quickly the camera catches up (lower = smoother)")]
    [Range(1f, 20f)]
    public float followSpeed = 5f;

    [Tooltip("How quickly the camera rotates to look at the target")]
    [Range(1f, 20f)]
    public float lookSpeed = 8f;

    void LateUpdate()
    {
        if (target == null) return;

        Vector3 desiredPosition = target.TransformPoint(offset);
        transform.position = Vector3.Lerp(transform.position, desiredPosition, followSpeed * Time.deltaTime);

        Quaternion desiredRotation = Quaternion.LookRotation(target.position - transform.position);
        transform.rotation = Quaternion.Slerp(transform.rotation, desiredRotation, lookSpeed * Time.deltaTime);
    }
}
