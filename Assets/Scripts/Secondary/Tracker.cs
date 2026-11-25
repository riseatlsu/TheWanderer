using UnityEngine;

public class Tracker : MonoBehaviour
{
    [SerializeField] private DistanceReferencer dReference;
    [SerializeField, Min(1f)] private float followDistance = 400f;

    private void Awake()
    {
        if (dReference == null)
        {
            enabled = false;
            Debug.LogError("Camera Tracker needs a DistanceReferencer.", this);
            return;
        }

        // Put the sphere in the center of view without changing the camera's
        // rotation. The camera then translates with the sphere at this distance.
        transform.position = dReference.getPlayerPosition() - transform.forward * followDistance;
    }

    private void LateUpdate()
    {
        if (dReference == null)
        {
            return;
        }

        transform.position = dReference.getPlayerPosition() - transform.forward * followDistance;
    }
}
