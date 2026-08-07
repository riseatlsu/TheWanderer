using UnityEngine;

public class Tracker : MonoBehaviour
{
    [SerializeField] private DistanceReferencer dReference;

    void Update()
    {
        Vector3 direction = dReference.getPlayerPosition() - transform.position;

        transform.rotation = Quaternion.LookRotation(direction);
    }
}
