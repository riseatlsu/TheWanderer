// custom methods for getting vector3 positions and float distances from thos positions


using UnityEngine;
using UnityEngine.AI;

public class DistanceReferencer : MonoBehaviour
{
    [Header("Point A")]
    [SerializeField] private Transform player;

    [Header("Point B")]
    [SerializeField] private GameObject mars;

    private float playerHeight;
    private float targetHeight;

    private float distanceToGround;
    private MeshCollider[] terrainColliders;
    private bool hasGroundHeight;

    private void Awake()
    {
        if (mars != null)
        {
            // The Surface object also has a large bounds BoxCollider. Only the
            // actual terrain mesh represents ground for height sampling.
            terrainColliders = mars.GetComponents<MeshCollider>();
        }
    }

    void Update()
    {
        if (player == null || mars == null) return;

        UpdateDistanceData();
    }

    private void UpdateDistanceData()
    {
        playerHeight = player.position.y;

        if (TryGetTerrainHeightAt(player.position, out float terrainHeight))
        {
            targetHeight = terrainHeight + player.lossyScale.y / 2f;
            hasGroundHeight = true;
        }
        else if (!hasGroundHeight)
        {
            // Keep the initial value finite until the first terrain sample.
            targetHeight = playerHeight;
        }

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

    public float GetPlayerSpeed()
    {
        if (player == null)
        {
            return 0f;
        }

        NavMeshAgent agent = player.GetComponent<NavMeshAgent>();
        return agent != null && agent.enabled && agent.isOnNavMesh
            ? agent.velocity.magnitude
            : 0f;
    }

    public float GetTerrainHeightAt(Vector3 worldPosition)
    {
        return TryGetTerrainHeightAt(worldPosition, out float height)
            ? height
            : worldPosition.y;
    }

    public bool TryGetTerrainHeightAt(Vector3 worldPosition, out float height)
    {
        height = 0f;

        if (terrainColliders == null || terrainColliders.Length == 0)
        {
            return false;
        }

        RaycastHit closestHit = default;
        bool foundHit = false;
        float closestDistance = float.PositiveInfinity;

        foreach (MeshCollider terrainCollider in terrainColliders)
        {
            if (terrainCollider == null || !terrainCollider.enabled || !terrainCollider.gameObject.activeInHierarchy)
            {
                continue;
            }

            Bounds bounds = terrainCollider.bounds;
            float margin = Mathf.Max(1f, bounds.size.y * 0.01f);
            Vector3 origin = new Vector3(worldPosition.x, bounds.max.y + margin, worldPosition.z);
            float rayDistance = bounds.size.y + margin * 2f;

            if (terrainCollider.Raycast(new Ray(origin, Vector3.down), out RaycastHit hit, rayDistance) &&
                hit.distance < closestDistance)
            {
                closestHit = hit;
                closestDistance = hit.distance;
                foundHit = true;
            }
        }

        if (!foundHit)
        {
            return false;
        }

        height = closestHit.point.y;
        return true;
    }

    public float GetHorizontalDistance(Vector3 a, Vector3 b)
    {
        Vector2 aXZ = new Vector2(a.x, a.z);
        Vector2 bXZ = new Vector2(b.x, b.z);
        return Vector2.Distance(aXZ, bXZ);
    }


}
