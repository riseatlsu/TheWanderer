using System;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Gives Wanderer a coarse subjective view of the surrounding terrain.
///
/// This component DOES NOT choose destinations.
/// It only describes the eight compass directions to the high-level brain.
///
/// Important architecture:
/// - Perception may be approximate.
/// - The LLM expresses intent.
/// - WandererMotor owns final NavMesh validity.
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
public class WandererPerception : MonoBehaviour
{
    [Header("Directional Perception")]

    [SerializeField, Min(1f)]
    [Tooltip("Maximum distance inspected in each compass direction.")]
    private float scanDistance = 3000f;

    [SerializeField, Min(0.1f)]
    [Tooltip("Smallest directional probe tested during a scan.")]
    private float minimumProbeDistance = 10f;

    [SerializeField, Range(3, 16)]
    [Tooltip("Number of far-to-near probe distances tested per direction.")]
    private int probeAttempts = 8;

    [SerializeField, Min(0.1f)]
    [Tooltip("Maximum radius used to project a directional probe onto the NavMesh.")]
    private float sampleRadius = 200f;

    [SerializeField, Range(-1f, 1f)]
    [Tooltip("Minimum directional alignment after NavMesh projection.")]
    private float minimumDirectionAlignment = 0.25f;

    private NavMeshAgent agent;
    private NavMeshPath path;
    private NavMeshQueryFilter filter;

    public float ScanDistance => scanDistance;

    private void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        path = new NavMeshPath();
        RebuildFilter();
    }

    /// <summary>
    /// Scans all eight headings and returns the farthest complete path found in each.
    /// </summary>
    public WandererPerceptionSnapshot Scan()
    {
        RebuildFilter();

        WandererPerceptionSnapshot snapshot = new WandererPerceptionSnapshot
        {
            scanDistance = scanDistance
        };

        foreach (WandererDirection direction in Enum.GetValues(typeof(WandererDirection)))
        {
            snapshot.directions.Add(EvaluateDirection(direction));
        }

        return snapshot;
    }

    private WandererDirectionSense EvaluateDirection(WandererDirection direction)
    {
        WandererDirectionSense sense = new WandererDirectionSense
        {
            direction = direction.ToString().ToLowerInvariant(),
            headingDegrees = direction.ToHeadingDegrees(),
            reachable = false,
            requestedDistance = scanDistance,
            actualDistance = -1f,
            pathLength = -1f,
            elevationChange = 0f,
            averageSlope = 0f,
            maximumSlope = 0f
        };

        if (agent == null || !agent.enabled || !agent.isOnNavMesh)
        {
            return sense;
        }

        Vector3 start = agent.nextPosition;
        Vector3 directionVector = direction.ToWorldVector();

        int attempts = Mathf.Max(2, probeAttempts);

        for (int i = 0; i < attempts; i++)
        {
            float t = attempts == 1
                ? 0f
                : (float)i / (attempts - 1);

            // Geometric interpolation gives useful coverage at both long and short range.
            float far = Mathf.Max(minimumProbeDistance, scanDistance);
            float near = Mathf.Min(minimumProbeDistance, far);

            float probeDistance;

            if (near <= 0f || far <= near)
            {
                probeDistance = far;
            }
            else
            {
                // Equivalent to exponentially descending from far to near.
                probeDistance = far * Mathf.Pow(near / far, t);
            }

            if (TryEvaluateProbe(
                    start,
                    direction,
                    directionVector,
                    probeDistance,
                    out WandererDirectionSense reachable))
            {
                return reachable;
            }
        }

        return sense;
    }

    private bool TryEvaluateProbe(
        Vector3 start,
        WandererDirection direction,
        Vector3 directionVector,
        float probeDistance,
        out WandererDirectionSense sense)
    {
        sense = null;

        Vector3 requestedPosition =
            start + directionVector * probeDistance;

        float projectionRadius = Mathf.Min(
            sampleRadius,
            Mathf.Max(2f, probeDistance * 0.35f)
        );

        if (!NavMesh.SamplePosition(
                requestedPosition,
                out NavMeshHit hit,
                projectionRadius,
                filter))
        {
            return false;
        }

        Vector3 displacement = hit.position - start;
        Vector3 horizontal = new Vector3(
            displacement.x,
            0f,
            displacement.z
        );

        if (horizontal.sqrMagnitude < 0.01f)
        {
            return false;
        }

        float alignment = Vector3.Dot(
            directionVector,
            horizontal.normalized
        );

        if (alignment < minimumDirectionAlignment)
        {
            return false;
        }

        // Use the agent's own path calculation so perception and locomotion
        // use the same agent type, area mask and NavMesh binding.
        bool calculated = agent.CalculatePath(
            hit.position,
            path
        );

        if (!calculated ||
            path.status != NavMeshPathStatus.PathComplete)
        {
            return false;
        }

        float pathLength = CalculatePathLength(path);

        if (pathLength <= 0.01f)
        {
            return false;
        }

        CalculateSlopeStatistics(
            path,
            out float averageSlope,
            out float maximumSlope
        );

        sense = new WandererDirectionSense
        {
            direction = direction.ToString().ToLowerInvariant(),
            headingDegrees = direction.ToHeadingDegrees(),
            reachable = true,
            requestedDistance = probeDistance,
            actualDistance = horizontal.magnitude,
            pathLength = pathLength,
            elevationChange = hit.position.y - start.y,
            averageSlope = averageSlope,
            maximumSlope = maximumSlope
        };

        return true;
    }

    private void CalculateSlopeStatistics(
        NavMeshPath navPath,
        out float averageSlope,
        out float maximumSlope)
    {
        averageSlope = 0f;
        maximumSlope = 0f;

        if (navPath == null ||
            navPath.corners == null ||
            navPath.corners.Length < 2)
        {
            return;
        }

        float totalSlope = 0f;
        int segments = 0;

        for (int i = 1; i < navPath.corners.Length; i++)
        {
            Vector3 delta =
                navPath.corners[i] - navPath.corners[i - 1];

            float horizontal =
                new Vector2(delta.x, delta.z).magnitude;

            if (horizontal < 0.001f)
            {
                continue;
            }

            float slope = Mathf.Atan2(
                Mathf.Abs(delta.y),
                horizontal
            ) * Mathf.Rad2Deg;

            totalSlope += slope;
            maximumSlope = Mathf.Max(maximumSlope, slope);
            segments++;
        }

        if (segments > 0)
        {
            averageSlope = totalSlope / segments;
        }
    }

    private static float CalculatePathLength(NavMeshPath navPath)
    {
        if (navPath == null ||
            navPath.corners == null ||
            navPath.corners.Length < 2)
        {
            return 0f;
        }

        float total = 0f;

        for (int i = 1; i < navPath.corners.Length; i++)
        {
            total += Vector3.Distance(
                navPath.corners[i - 1],
                navPath.corners[i]
            );
        }

        return total;
    }

    private void RebuildFilter()
    {
        if (agent == null)
        {
            return;
        }

        filter = new NavMeshQueryFilter
        {
            agentTypeID = agent.agentTypeID,
            areaMask = agent.areaMask
        };
    }
}
