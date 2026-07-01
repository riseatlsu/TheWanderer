using UnityEngine;

public class DistanceReferencer : MonoBehaviour
{
    [Header("Point A")]
    [SerializeField] private Transform player;

    [Header("Point B")]
    [SerializeField] private GameObject mars;
    [SerializeField] private LayerMask marsLayer;

    private float playerHeight;
    private float targetHeight;

    private float distanceToGround;

    void Update()
    {
        if (player == null || mars == null) return;

        UpdateDistanceData();
    }

    private void UpdateDistanceData()
    {
        playerHeight = player.position.y;
        targetHeight = GetTerrainHeightAt(player.position) + player.lossyScale.y / 2f;
        distanceToGround = playerHeight - targetHeight;
    }

    public Vector3 getPlayerPosition()
    {
        return player.position;
    }

    public float GetDistanceToGround()
    {
        return distanceToGround;
    }

    public float GetGroundHeight()
    {
        return targetHeight;
    }

    public float GetTerrainHeightAt(Vector3 worldPosition)
    {

        float maxDistance = 1000f;
        Vector3 rayOrigin = new Vector3(worldPosition.x, worldPosition.y, worldPosition.z);

        if (Physics.Raycast(rayOrigin + Vector3.down * 100, Vector3.up, out RaycastHit hit2, maxDistance))
        {
            return hit2.point.y;
        }
        else if (Physics.Raycast(rayOrigin + Vector3.up * 100, Vector3.down, out RaycastHit hit, maxDistance))
        {
            return hit.point.y;
        }

        Debug.Log("Nothing hit.");
        return worldPosition.y;
    }

    public float GetHorizontalDistance(Vector3 a, Vector3 b)
    {
        Vector2 aXZ = new Vector2(a.x, a.z);
        Vector2 bXZ = new Vector2(b.x, b.z);
        return Vector2.Distance(aXZ, bXZ);
    }


}
