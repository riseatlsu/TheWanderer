using System;
using System.Collections;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Owns all physical movement for Wanderer.
///
/// The LLM never supplies XYZ coordinates.
/// It supplies a preferred direction and travel character.
/// This component converts that intent into a complete NavMesh path.
///
/// Destination search order:
/// 1. Preferred heading, from desired distance down to a tiny valid movement.
/// 2. Neighboring headings, also from far to near.
/// 3. Random reachable samples.
/// 4. Deterministic local radial search.
/// 5. NavMesh triangulation fallback.
///
/// If any normal destination fails while moving, the motor replans locally without
/// making another LLM request. A new LLM decision is requested only after a physical
/// destination is actually reached.
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
public class WandererMotor : MonoBehaviour
{
    [Header("Initialization")]

    [SerializeField, Min(0.1f)]
    [Tooltip("Maximum distance used to snap Wanderer onto the NavMesh at startup.")]
    private float startupSnapDistance = 100f;


    [Header("Movement Envelope")]

    [SerializeField, Min(1f)]
    [Tooltip("Maximum direct distance normally considered for one movement.")]
    private float maximumMovementDistance = 10000f;

    [SerializeField, Min(0.05f)]
    [Tooltip("Absolute smallest displacement accepted as real movement.")]
    private float absoluteMinimumMovement = 1f;

    [SerializeField, Min(0.1f)]
    [Tooltip("Maximum endpoint projection radius.")]
    private float destinationSampleRadius = 500f;

    [SerializeField, Range(-1f, 1f)]
    [Tooltip("Minimum alignment used only for the preferred directional search.")]
    private float minimumPreferredAlignment = 0.20f;

    [SerializeField, Range(4, 24)]
    [Tooltip("Number of far-to-near distances tested for each compass heading.")]
    private int directionalDistanceSamples = 10;

    [SerializeField, Range(8, 256)]
    [Tooltip("Random candidates tested before deterministic emergency search.")]
    private int randomSearchAttempts = 64;

    [Header("Mini-Mode (short-step) Support")]
    [SerializeField, Min(0.1f)]
    [Tooltip("Distance threshold (meters) below which movement is treated as a mini-step.")]
    private float miniModeDistanceThreshold = 10f;

    [SerializeField, Min(0.1f)]
    [Tooltip("Maximum fallback radius (meters) used for mini-step recovery searches.")]
    private float miniModeMaxFallbackDistance = 50f;

    [SerializeField, Min(0.1f)]
    [Tooltip("Maximum allowed mini-step distance when long-term goals are very large. This caps mini = longTermDistance/5.")]
    private float miniModeMaxMiniDistance = 75f;

    [SerializeField, Range(1f, 3f)]
    [Tooltip("Maximum allowed detour factor for mini-mode candidate path length compared to the straight requested distance. Lower values make minis avoid long detours.")]
    private float miniModeDetourFactor = 1.2f;

    [Header("Debug / Gizmos")]
    [SerializeField]
    [Tooltip("Draw debug gizmos for requested headings, arc samples, and selected candidate in the Scene view.")]
    private bool drawDebugGizmos = true;

    // Debug fields populated during destination search to visualize what the motor tried.
    private Vector3 debugStartPosition;
    private Vector3 debugRequestedPoint;
    private Vector3 debugSelectedCandidate;
    private readonly System.Collections.Generic.List<Vector3> debugArcSamples =
        new System.Collections.Generic.List<Vector3>();
    private readonly System.Collections.Generic.List<Vector3> debugRandomSamples =
        new System.Collections.Generic.List<Vector3>();

    // Draw debug gizmos in the Scene view when enabled to visualize
    // what the motor attempted during candidate selection.
    private void OnDrawGizmos()
    {
        if (!drawDebugGizmos)
        {
            return;
        }

        Vector3 start = debugStartPosition;

        // Visualize the configured maximum mini-step radius so designers know the limit.
        Gizmos.color = Color.yellow;
        if (miniModeMaxMiniDistance > 0f)
        {
            Gizmos.DrawWireSphere(start, miniModeMaxMiniDistance);
        }

        // Draw the originally requested point (blue line).
        Gizmos.color = Color.blue;
        Gizmos.DrawLine(start, debugRequestedPoint);

        // Draw sampled arc points (cyan) and random samples (magenta).
        Gizmos.color = Color.cyan;
        foreach (var p in debugArcSamples)
        {
            Gizmos.DrawSphere(p, 0.12f);
        }

        Gizmos.color = Color.magenta;
        foreach (var p in debugRandomSamples)
        {
            Gizmos.DrawSphere(p, 0.08f);
        }

        // Highlight the chosen candidate (green) if set.
        if (debugSelectedCandidate != Vector3.zero)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawSphere(debugSelectedCandidate, 0.18f);
        }
    }

    [Header("Turning / Arc Movement")]
    [SerializeField, Min(0.1f)]
    [Tooltip("Radius (meters) used when sampling an arc in front of the agent to produce smooth turns.")]
    private float turnRadius = 5f;

    [SerializeField, Range(3, 48)]
    [Tooltip("Number of samples taken along the arc when searching for a valid NavMesh endpoint.")]
    private int turnSamples = 12;

    [SerializeField, Range(0f, 180f)]
    [Tooltip("Minimum signed angle (degrees) between current forward and desired heading to attempt an arc turn.")]
    private float turnAngleThresholdDegrees = 20f;


    [Header("Agent Limits")]

    [SerializeField, Min(0.1f)]
    private float minimumSpeed = 1f;

    [SerializeField, Min(0.1f)]
    private float maximumSpeed = 500f;

    [SerializeField, Min(0.1f)]
    private float minimumAcceleration = 1f;

    [SerializeField, Min(0.1f)]
    private float maximumAcceleration = 1000f;

    [SerializeField, Min(1f)]
    private float minimumAngularSpeed = 30f;

    [SerializeField, Min(1f)]
    private float maximumAngularSpeed = 720f;

    [SerializeField, Min(0f)]
    private float maximumStoppingDistance = 50f;


    [Header("Recovery")]

    [SerializeField, Min(0.5f)]
    [Tooltip("Seconds without meaningful progress before a local replan.")]
    private float stallTimeout = 4f;

    [SerializeField, Min(0.001f)]
    [Tooltip("Distance that counts as meaningful positional progress.")]
    private float stallPositionEpsilon = 0.1f;

    [SerializeField, Min(1f)]
    [Tooltip("Minimum movement timeout. Long paths automatically receive more time.")]
    private float minimumMovementTimeout = 20f;

    [SerializeField, Min(0.1f)]
    [Tooltip("Delay before retrying a local recovery when no destination is immediately available.")]
    private float recoveryRetryDelay = 1f;

    [SerializeField, Min(0f)]
    [Tooltip("Grace period after SetDestination before a missing path is treated as a failure.")]
    private float pathAcquisitionGrace = 0.75f;

    [SerializeField, Min(0.01f)]
    [Tooltip("Extra distance allowed when deciding that the agent physically arrived.")]
    private float arrivalTolerance = 0.5f;

    [SerializeField, Min(0f)]
    [Tooltip("Recovery avoids reselecting a destination this close to the destination that just failed.")]
    private float recoveryDestinationSeparation = 5f;

    [SerializeField, Min(0.1f)]
    [Tooltip("How many seconds before arrival the next LLM decision should be requested.")]
    private float nextDecisionLeadTime = 3f;


    public event Action<WandererMovementResult> MovementCompleted;
    public event Action RequestNextDecision;

    public bool IsInitialized { get; private set; }
    public bool IsBusy { get; private set; }

    public NavMeshAgent Agent => agent;

    private NavMeshAgent agent;
    private NavMeshQueryFilter filter;
    private NavMeshPath validationPath;

    private Coroutine movementRoutine;

    private WandererDecision activeDecision;
    private DestinationCandidate activeCandidate;

    private float lastDestinationSetTime;
    private bool nextDecisionRequested;


    private void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        validationPath = new NavMeshPath();
    }

    // Random search variant biased toward short distances for mini-step recovery.
    private bool TryRandomSearchMini(
        WandererDirection preferredDirection,
        float preferredDistance,
        out DestinationCandidate candidate,
        Vector3? excludedDestination = null)
    {
        candidate = default;

        Vector3 start = agent.nextPosition;
        Vector3 preferredVector = preferredDirection.ToWorldVector();

        int attempts = Mathf.Max(8, randomSearchAttempts / 2);

        float maxFallback = Mathf.Max(miniModeMaxFallbackDistance, preferredDistance * 2f);
        maxFallback = Mathf.Min(maxFallback, maximumMovementDistance);

        for (int i = 0; i < attempts; i++)
        {
            Vector2 random = UnityEngine.Random.insideUnitCircle;

            if (random.sqrMagnitude < 0.001f)
            {
                continue;
            }

            // Bias distances toward smaller values by squaring the random value.
            float t = Mathf.Pow(UnityEngine.Random.value, 2f);
            float distance = Mathf.Lerp(
                absoluteMinimumMovement,
                maxFallback,
                t
            );

            Vector3 requested = start + new Vector3(
                random.normalized.x,
                0f,
                random.normalized.y
            ) * distance;

            if (TryValidateCandidate(
                    requested,
                    preferredVector,
                    false,
                    preferredDistance,
                    out candidate))
            {
                // Avoid candidates that require a path much longer than the requested straight distance.
                // This helps prevent falling back to long detours along NavMesh edges.
                if (candidate.pathLength > preferredDistance * miniModeDetourFactor)
                {
                    continue;
                }
                if (IsExcludedRecoveryDestination(
                        candidate.position,
                        excludedDestination))
                {
                    continue;
                }

                candidate.searchDirection =
                    WandererDirectionExtensions.ClosestToVector(
                        candidate.position - start
                    );

                return true;
            }
        }

        return false;
    }

    private bool TryFindArcDestination(
        Vector3 forwardVector,
        Vector3 desiredVector,
        float preferredDistance,
        float radius,
        int samples,
        out DestinationCandidate candidate)
    {
        candidate = default;

        Vector3 start = agent.nextPosition;

        // Ensure planar
        forwardVector.y = 0f;
        desiredVector.y = 0f;

        if (forwardVector.sqrMagnitude < 0.0001f || desiredVector.sqrMagnitude < 0.0001f)
        {
            return false;
        }

        forwardVector.Normalize();
        desiredVector.Normalize();

        float signedAngle = Vector3.SignedAngle(forwardVector, desiredVector, Vector3.up);

        if (Mathf.Abs(signedAngle) < 1f)
        {
            return false; // no arc needed
        }

        float sign = Mathf.Sign(signedAngle);

        // Right vector points to agent's right
        Vector3 right = Vector3.Cross(Vector3.up, forwardVector).normalized;

        Vector3 center = start + right * (radius * sign);

        float startAngle = Mathf.Atan2(start.z - center.z, start.x - center.x) * Mathf.Rad2Deg;

        float totalSweep = Mathf.Abs(signedAngle);

        int take = Mathf.Max(3, samples);

        for (int i = 1; i <= take; i++)
        {
            float t = (float)i / take;
            float sampleAngle = startAngle + sign * (totalSweep * t);
            float radians = sampleAngle * Mathf.Deg2Rad;

            Vector3 offset = new Vector3(Mathf.Cos(radians), 0f, Mathf.Sin(radians)) * radius;
            Vector3 point = center + offset;

            float distFromStart = Vector3.Distance(start, point);

            Vector3 requested = point;

            if (distFromStart > preferredDistance)
            {
                requested = start + (point - start).normalized * preferredDistance;
            }

            if (TryValidateCandidate(
                    requested,
                    desiredVector,
                    false,
                    preferredDistance,
                    out DestinationCandidate tested))
            {
                tested.fallbackStage = "turn arc";
                tested.searchDirection =
                    WandererDirectionExtensions.ClosestToVector(tested.position - start);
                candidate = tested;
                return true;
            }
        }

        return false;
    }

    // Deterministic radial search variant restricted for mini-mode to avoid large fallbacks.
    private bool TryDeterministicRadialSearchMini(
        WandererDirection preferredDirection,
        float preferredDistance,
        out DestinationCandidate bestCandidate,
        Vector3? excludedDestination = null)
    {
        bestCandidate = default;
        bool found = false;
        float bestScore = float.NegativeInfinity;

        Vector3 start = agent.nextPosition;
        Vector3 preferredVector = preferredDirection.ToWorldVector();

        // Restrict radii to small distances up to miniModeMaxFallbackDistance.
        float[] candidateRadii = new float[]
        {
            absoluteMinimumMovement,
            absoluteMinimumMovement * 2f,
            absoluteMinimumMovement * 5f,
            5f,
            10f,
            25f,
            Mathf.Min(50f, miniModeMaxFallbackDistance)
        };

        const int angleSamples = 24;

        for (int r = 0; r < candidateRadii.Length; r++)
        {
            float radius = Mathf.Clamp(
                candidateRadii[r],
                absoluteMinimumMovement,
                Mathf.Min(maximumMovementDistance, miniModeMaxFallbackDistance)
            );

            for (int i = 0; i < angleSamples; i++)
            {
                float angle =
                    (360f / angleSamples) * i;

                float radians = angle * Mathf.Deg2Rad;

                Vector3 direction = new Vector3(
                    Mathf.Sin(radians),
                    0f,
                    Mathf.Cos(radians)
                );

                Vector3 requested = start + direction * radius;

                if (!TryValidateCandidate(
                        requested,
                        preferredVector,
                        false,
                        preferredDistance,
                        out DestinationCandidate tested))
                {
                    continue;
                }

                // Avoid selecting candidates that require a path much longer than preferredDistance
                // during mini-mode deterministic search. This reduces hugging NavMesh edges.
                if (tested.pathLength > preferredDistance * miniModeDetourFactor)
                {
                    continue;
                }

                if (IsExcludedRecoveryDestination(
                        tested.position,
                        excludedDestination))
                {
                    continue;
                }

                float score = ScoreCandidate(
                    tested,
                    preferredVector,
                    preferredDistance,
                    start
                );

                if (!found || score > bestScore)
                {
                    found = true;
                    bestScore = score;
                    bestCandidate = tested;
                    bestCandidate.searchDirection =
                        WandererDirectionExtensions.ClosestToVector(
                            tested.position - start
                        );
                }
            }

            if (found)
            {
                return true;
            }
        }

        return false;
    }

    private IEnumerator Start()
    {
        // Give runtime NavMeshSurface components a frame to register.
        yield return null;

        RebuildFilter();

        if (!BindToNavMesh())
        {
            enabled = false;
            yield break;
        }

        agent.isStopped = false;
        agent.updatePosition = true;
        agent.updateRotation = true;
        agent.updateUpAxis = true;

        IsInitialized = true;

        Debug.Log(
            $"WANDERER MOTOR READY\n" +
            $"NavMesh Position: {agent.nextPosition}\n" +
            $"Agent Type ID: {agent.agentTypeID}\n" +
            $"Area Mask: {agent.areaMask}",
            this
        );
    }

    /// <summary>
    /// Attempts to realize one LLM decision as a physical movement.
    /// </summary>
    public bool TryMove(WandererDecision decision)
    {
        Debug.Log($"TryMove called: headingDegrees={decision?.headingDegrees.ToString() ?? "null"}, direction={decision?.direction}, distance={decision?.distance.ToString() ?? "null"}, IsBusy={IsBusy}", this);

        if (!CanStartMovement() || decision == null)
        {
            Debug.LogWarning($"TryMove early-fail: CanStartMovement={CanStartMovement()}, decision==null={decision==null}", this);
            return false;
        }

        // If an explicit headingDegrees is provided, allow arbitrary heading vectors
        if (decision.headingDegrees >= 0f)
        {
            float preferredDistance = Mathf.Clamp(
                decision.distance,
                absoluteMinimumMovement,
                maximumMovementDistance
            );

            ConfigureAgent(decision);

            float radians = decision.headingDegrees * Mathf.Deg2Rad;
            Vector3 directionVector = new Vector3(
                Mathf.Sin(radians),
                0f,
                Mathf.Cos(radians)
            );

            // Try along the exact heading but do not strictly enforce alignment so
            // short mini-steps can still find nearby NavMesh samples when the
            // exact projection is slightly off the mesh.
            DestinationCandidate candidate;

            // First, try a direct projection along the exact heading (lenient alignment).
            if (TryFindAlongVector(
                    directionVector,
                    preferredDistance,
                    false,
                    out candidate))
            {
                WandererDirection preferredDirectionFromHeading =
                    WandererDirectionExtensions.ClosestToVector(directionVector);

                return BeginMovement(
                    decision,
                    preferredDirectionFromHeading,
                    preferredDistance,
                    candidate
                );
            }

            // If that fails, try searches biased toward the heading.
            WandererDirection biasDirection = WandererDirectionExtensions.ClosestToVector(directionVector);

            // Consider using an arc-based search when the desired heading requires a significant turn.
            Vector3 forwardVector = agent != null ? agent.transform.forward : Vector3.forward;
            forwardVector = new Vector3(forwardVector.x, 0f, forwardVector.z).normalized;

            float signedAngle = Vector3.SignedAngle(forwardVector, directionVector, Vector3.up);

            // If the angle is larger than the threshold, try an arc-sampled destination to make the agent turn smoothly.
            if (Mathf.Abs(signedAngle) >= turnAngleThresholdDegrees)
            {
                if (TryFindArcDestination(
                        forwardVector,
                        directionVector,
                        preferredDistance,
                        turnRadius,
                        turnSamples,
                        out candidate))
                {
                    candidate.fallbackStage = "turn arc";

                    WandererDirection preferredDirectionFromHeading =
                        WandererDirectionExtensions.ClosestToVector(directionVector);

                    return BeginMovement(
                        decision,
                        preferredDirectionFromHeading,
                        preferredDistance,
                        candidate
                    );
                }
            }

            bool miniMode = preferredDistance <= miniModeDistanceThreshold;

            if (miniMode)
            {
                // Try a small-radius deterministic radial search first.
                if (TryDeterministicRadialSearchMini(
                        biasDirection,
                        preferredDistance,
                        out candidate))
                {
                    candidate.fallbackStage = "deterministic radial fallback (headingDegrees, mini)";

                    return BeginMovement(
                        decision,
                        biasDirection,
                        preferredDistance,
                        candidate
                    );
                }

                // Next try a short-distance random search biased toward small radii.
                if (TryRandomSearchMini(
                        biasDirection,
                        preferredDistance,
                        out candidate))
                {
                    candidate.fallbackStage = "random reachable fallback (headingDegrees, mini)";

                    WandererDirection preferredDirectionFromHeading =
                        WandererDirectionExtensions.ClosestToVector(directionVector);

                    return BeginMovement(
                        decision,
                        preferredDirectionFromHeading,
                        preferredDistance,
                        candidate
                    );
                }

                // If mini-specific searches failed, fall through to the normal broader searches.
            }

            // Try the standard deterministic radial search.
            if (TryDeterministicRadialSearch(
                    biasDirection,
                    preferredDistance,
                    out candidate))
            {
                candidate.fallbackStage = "deterministic radial fallback (headingDegrees)";

                return BeginMovement(
                    decision,
                    biasDirection,
                    preferredDistance,
                    candidate
                );
            }

            // As a last resort, try a random reachable fallback biased by the heading.
            if (TryRandomSearch(
                    biasDirection,
                    preferredDistance,
                    out candidate))
            {
                candidate.fallbackStage = "random reachable fallback (headingDegrees)";

                WandererDirection preferredDirectionFromHeading =
                    WandererDirectionExtensions.ClosestToVector(directionVector);

                return BeginMovement(
                    decision,
                    preferredDirectionFromHeading,
                    preferredDistance,
                    candidate
                );
            }

            Debug.LogWarning(
                $"WANDERER COULD NOT REALIZE INTENT (headingDegrees)\n" +
                $"HeadingDegrees: {decision.headingDegrees:F2}\n" +
                $"Preferred Distance: {preferredDistance:F2} m\n" +
                "No valid destination was found yet. The controller may retry locally.",
                this
            );

            return false;
        }

        WandererDirection parsedDirection;

        if (!WandererDirectionExtensions.TryParse(
                decision.direction,
                out parsedDirection))
        {
            parsedDirection = WandererDirection.North;
        }

        float preferredDistance2 = Mathf.Clamp(
            decision.distance,
            absoluteMinimumMovement,
            maximumMovementDistance
        );

        ConfigureAgent(decision);

        if (!TryFindDestination(
                parsedDirection,
                preferredDistance2,
                out DestinationCandidate candidate2))
        {
            Debug.LogWarning(
                $"WANDERER COULD NOT REALIZE INTENT\n" +
                $"Preferred Direction: {parsedDirection}\n" +
                $"Preferred Distance: {preferredDistance2:F2} m\n" +
                "No valid destination was found yet. The controller may retry locally.",
                this
            );

            return false;
        }

        return BeginMovement(
            decision,
            parsedDirection,
            preferredDistance2,
            candidate2
        );
    }

    private bool TryFindAlongVector(
        Vector3 directionVector,
        float preferredDistance,
        bool enforceAlignment,
        out DestinationCandidate candidate)
    {
        candidate = default;

        Vector3 start = agent.nextPosition;

        float far = Mathf.Clamp(
            preferredDistance,
            absoluteMinimumMovement,
            maximumMovementDistance
        );

        float near = Mathf.Min(
            absoluteMinimumMovement,
            far
        );

        int samples = Mathf.Max(2, directionalDistanceSamples);

        for (int i = 0; i < samples; i++)
        {
            float t = samples == 1
                ? 0f
                : (float)i / (samples - 1);

            float distance;

            if (near <= 0f || far <= near)
            {
                distance = far;
            }
            else
            {
                distance = far * Mathf.Pow(near / far, t);
            }

            Vector3 requested = start + directionVector * distance;

            if (TryValidateCandidate(
                    requested,
                    directionVector,
                    enforceAlignment,
                    preferredDistance,
                    out candidate))
            {
                candidate.searchDirection =
                    WandererDirectionExtensions.ClosestToVector(
                        candidate.position - start
                    );

                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Starts a locally chosen movement without requiring a new LLM decision.
    /// Used only as an anti-freeze fallback.
    /// </summary>
    public bool TryMoveAnywhere(WandererDecision sourceDecision = null)
    {
        if (!CanStartMovement())
        {
            return false;
        }

        WandererDecision decision = sourceDecision ?? CreateNeutralFallbackDecision();

        ConfigureAgent(decision);

        WandererDirection preferredDirection;

        if (!WandererDirectionExtensions.TryParse(
                decision.direction,
                out preferredDirection))
        {
            preferredDirection =
                (WandererDirection)UnityEngine.Random.Range(0, 8);
        }

        float preferredDistance = Mathf.Clamp(
            decision.distance > 0f
                ? decision.distance
                : maximumMovementDistance * 0.5f,
            absoluteMinimumMovement,
            maximumMovementDistance
        );

        if (!TryFindDestination(
                preferredDirection,
                preferredDistance,
                out DestinationCandidate candidate))
        {
            return false;
        }

        return BeginMovement(
            decision,
            preferredDirection,
            preferredDistance,
            candidate
        );
    }

    private bool CanStartMovement()
    {
        if (!IsInitialized)
        {
            return false;
        }

        if (IsBusy)
        {
            return false;
        }

        if (agent == null || !agent.enabled)
        {
            Debug.LogError(
                "Wanderer cannot move because its NavMeshAgent is missing or disabled.",
                this
            );

            return false;
        }

        if (!agent.isOnNavMesh)
        {
            RebuildFilter();

            if (!BindToNavMesh())
            {
                Debug.LogError(
                    "Wanderer could not rebind to the NavMesh.",
                    this
                );

                return false;
            }
        }

        return true;
    }

    private void ConfigureAgent(WandererDecision decision)
    {
        agent.speed = Mathf.Clamp(
            decision.speed,
            minimumSpeed,
            maximumSpeed
        );

        agent.acceleration = Mathf.Clamp(
            decision.acceleration,
            minimumAcceleration,
            maximumAcceleration
        );

        agent.angularSpeed = Mathf.Clamp(
            decision.angularSpeed,
            minimumAngularSpeed,
            maximumAngularSpeed
        );

        agent.stoppingDistance = Mathf.Clamp(
            decision.stoppingDistance,
            0f,
            maximumStoppingDistance
        );

        agent.isStopped = false;
    }

    private bool TryFindDestination(
        WandererDirection preferredDirection,
        float preferredDistance,
        out DestinationCandidate candidate)
    {
        candidate = default;

        WandererDirection[] searchOrder =
            preferredDirection.GetFallbackOrder();

        // 1 + 2. Directional search: preferred heading first, then neighbors.
        for (int i = 0; i < searchOrder.Length; i++)
        {
            bool enforceAlignment = i == 0;

            if (TryFindAlongDirection(
                    searchOrder[i],
                    preferredDistance,
                    enforceAlignment,
                    out candidate))
            {
                candidate.fallbackStage =
                    i == 0
                        ? "preferred heading"
                        : $"neighbor heading ({searchOrder[i]})";

                return true;
            }
        }

        // 3. Random local search.
        if (TryRandomSearch(
                preferredDirection,
                preferredDistance,
                out candidate))
        {
            candidate.fallbackStage = "random reachable fallback";
            return true;
        }

        // 4. Deterministic local radial search.
        if (TryDeterministicRadialSearch(
                preferredDirection,
                preferredDistance,
                out candidate))
        {
            candidate.fallbackStage = "deterministic radial fallback";
            return true;
        }

        // 5. Expensive emergency fallback over baked NavMesh vertices.
        if (TryTriangulationFallback(
                preferredDirection,
                preferredDistance,
                out candidate))
        {
            candidate.fallbackStage = "NavMesh triangulation fallback";
            return true;
        }

        return false;
    }

    private bool TryFindAlongDirection(
        WandererDirection direction,
        float preferredDistance,
        bool enforceAlignment,
        out DestinationCandidate candidate)
    {
        candidate = default;

        Vector3 start = agent.nextPosition;
        Vector3 directionVector = direction.ToWorldVector();

        float far = Mathf.Clamp(
            preferredDistance,
            absoluteMinimumMovement,
            maximumMovementDistance
        );

        float near = Mathf.Min(
            absoluteMinimumMovement,
            far
        );

        int samples = Mathf.Max(2, directionalDistanceSamples);

        for (int i = 0; i < samples; i++)
        {
            float t = samples == 1
                ? 0f
                : (float)i / (samples - 1);

            // Crucially, this ALWAYS searches down to the absolute minimum.
            // A large preferred/minimum distance can never collapse all attempts
            // onto the same endpoint.
            float distance;

            if (near <= 0f || far <= near)
            {
                distance = far;
            }
            else
            {
                distance = far * Mathf.Pow(near / far, t);
            }

            Vector3 requested =
                start + directionVector * distance;

            if (TryValidateCandidate(
                    requested,
                    directionVector,
                    enforceAlignment,
                    preferredDistance,
                    out candidate))
            {
                candidate.searchDirection = direction;
                return true;
            }
        }

        return false;
    }

    private bool TryRandomSearch(
        WandererDirection preferredDirection,
        float preferredDistance,
        out DestinationCandidate candidate,
        Vector3? excludedDestination = null)
    {
        candidate = default;

        Vector3 start = agent.nextPosition;
        Vector3 preferredVector = preferredDirection.ToWorldVector();

        for (int i = 0; i < randomSearchAttempts; i++)
        {
            Vector2 random = UnityEngine.Random.insideUnitCircle;

            if (random.sqrMagnitude < 0.001f)
            {
                continue;
            }

            float distance = UnityEngine.Random.Range(
                absoluteMinimumMovement,
                maximumMovementDistance
            );

            Vector3 requested = start + new Vector3(
                random.normalized.x,
                0f,
                random.normalized.y
            ) * distance;

            if (TryValidateCandidate(
                    requested,
                    preferredVector,
                    false,
                    preferredDistance,
                    out candidate))
            {
                if (IsExcludedRecoveryDestination(
                        candidate.position,
                        excludedDestination))
                {
                    continue;
                }

                candidate.searchDirection =
                    WandererDirectionExtensions.ClosestToVector(
                        candidate.position - start
                    );

                return true;
            }
        }

        return false;
    }

    private bool TryDeterministicRadialSearch(
        WandererDirection preferredDirection,
        float preferredDistance,
        out DestinationCandidate bestCandidate,
        Vector3? excludedDestination = null)
    {
        bestCandidate = default;
        bool found = false;
        float bestScore = float.NegativeInfinity;

        Vector3 start = agent.nextPosition;
        Vector3 preferredVector = preferredDirection.ToWorldVector();

        // Search small distances first because this is an anti-freeze fallback.
        float[] radii =
        {
            absoluteMinimumMovement,
            absoluteMinimumMovement * 2f,
            absoluteMinimumMovement * 5f,
            10f,
            25f,
            50f,
            100f,
            250f,
            500f,
            1000f
        };
        const int angleSamples = 32;
        for (int r = 0; r < radii.Length; r++)
        {
            float radius = Mathf.Clamp(
                radii[r],
                absoluteMinimumMovement,
                maximumMovementDistance
            );

            for (int i = 0; i < angleSamples; i++)
            {
                float angle =
                    (360f / angleSamples) * i;

                float radians = angle * Mathf.Deg2Rad;

                Vector3 direction = new Vector3(
                    Mathf.Sin(radians),
                    0f,
                    Mathf.Cos(radians)
                );

                Vector3 requested =
                    start + direction * radius;

                if (!TryValidateCandidate(
                        requested,
                        preferredVector,
                        false,
                        preferredDistance,
                        out DestinationCandidate tested))
                {
                    continue;
                }

                if (IsExcludedRecoveryDestination(
                        tested.position,
                        excludedDestination))
                {
                    continue;
                }

                float score = ScoreCandidate(
                    tested,
                    preferredVector,
                    preferredDistance,
                    start
                );

                if (!found || score > bestScore)
                {
                    found = true;
                    bestScore = score;
                    bestCandidate = tested;
                    bestCandidate.searchDirection =
                        WandererDirectionExtensions.ClosestToVector(
                            tested.position - start
                        );
                }
            }

            // Once a valid short-radius ring exists, use it.
            // No reason to scan huge distances during emergency recovery.
            if (found)
            {
                return true;
            }
        }

        return false;
    }

    private bool TryTriangulationFallback(
        WandererDirection preferredDirection,
        float preferredDistance,
        out DestinationCandidate bestCandidate,
        Vector3? excludedDestination = null)
    {
        bestCandidate = default;
        bool found = false;
        float bestScore = float.NegativeInfinity;

        Vector3 start = agent.nextPosition;
        Vector3 preferredVector = preferredDirection.ToWorldVector();

        NavMeshTriangulation triangulation =
            NavMesh.CalculateTriangulation();

        if (triangulation.vertices == null ||
            triangulation.vertices.Length == 0)
        {
            return false;
        }

        for (int i = 0; i < triangulation.vertices.Length; i++)
        {
            Vector3 vertex = triangulation.vertices[i];

            Vector3 horizontal = new Vector3(
                vertex.x - start.x,
                0f,
                vertex.z - start.z
            );

            float directDistance = horizontal.magnitude;

            if (directDistance < absoluteMinimumMovement ||
                directDistance > maximumMovementDistance)
            {
                continue;
            }

            if (!TryValidateCandidate(
                    vertex,
                    preferredVector,
                    false,
                    preferredDistance,
                    out DestinationCandidate tested))
            {
                continue;
            }

            if (IsExcludedRecoveryDestination(
                    tested.position,
                    excludedDestination))
            {
                continue;
            }

            float score = ScoreCandidate(
                tested,
                preferredVector,
                preferredDistance,
                start
            );

            if (!found || score > bestScore)
            {
                found = true;
                bestScore = score;
                bestCandidate = tested;
                bestCandidate.searchDirection =
                    WandererDirectionExtensions.ClosestToVector(
                        tested.position - start
                    );
            }
        }

        return found;
    }

    private bool TryValidateCandidate(
        Vector3 requestedDestination,
        Vector3 requestedDirection,
        bool enforceAlignment,
        float preferredDistance,
        out DestinationCandidate candidate)
    {
        candidate = default;

        Vector3 start = agent.nextPosition;

        float requestedDistance =
            Vector3.Distance(start, requestedDestination);

        float projectionRadius = Mathf.Min(
            destinationSampleRadius,
            Mathf.Max(2f, requestedDistance * 0.35f)
        );

        if (!NavMesh.SamplePosition(
                requestedDestination,
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

        float actualDistance = horizontal.magnitude;

        if (actualDistance < absoluteMinimumMovement ||
            actualDistance > maximumMovementDistance + 0.01f)
        {
            return false;
        }

        float alignment = 1f;

        if (horizontal.sqrMagnitude > 0.001f)
        {
            alignment = Vector3.Dot(
                requestedDirection.normalized,
                horizontal.normalized
            );
        }

        if (enforceAlignment &&
            alignment < minimumPreferredAlignment)
        {
            return false;
        }

        validationPath.ClearCorners();

        bool calculated = agent.CalculatePath(
            hit.position,
            validationPath
        );

        if (!calculated ||
            validationPath.status != NavMeshPathStatus.PathComplete)
        {
            return false;
        }

        float pathLength =
            CalculatePathLength(validationPath);

        if (pathLength < absoluteMinimumMovement)
        {
            return false;
        }

        candidate = new DestinationCandidate
        {
            position = hit.position,
            requestedDistance = preferredDistance,
            actualDistance = actualDistance,
            pathLength = pathLength,
            alignment = alignment,
            searchDirection = WandererDirection.North,
            fallbackStage = "candidate"
        };

        return true;
    }

    private float ScoreCandidate(
        DestinationCandidate candidate,
        Vector3 preferredVector,
        float preferredDistance,
        Vector3 start)
    {
        Vector3 horizontal = new Vector3(
            candidate.position.x - start.x,
            0f,
            candidate.position.z - start.z
        );

        float alignment = horizontal.sqrMagnitude > 0.001f
            ? Vector3.Dot(
                preferredVector,
                horizontal.normalized
            )
            : -1f;

        float distanceError =
            Mathf.Abs(candidate.actualDistance - preferredDistance) /
            Mathf.Max(1f, maximumMovementDistance);

        return alignment * 2f - distanceError;
    }

    private bool BeginMovement(
        WandererDecision decision,
        WandererDirection preferredDirection,
        float preferredDistance,
        DestinationCandidate candidate)
    {
        EnsureStoppingDistanceAllowsMotion(
            candidate.actualDistance
        );

        agent.isStopped = false;

        if (!agent.SetDestination(candidate.position))
        {
            Debug.LogWarning(
                "NavMeshAgent rejected a validated destination.",
                this
            );

            return false;
        }

        lastDestinationSetTime = Time.time;

        activeDecision = decision;
        activeCandidate = candidate;
        nextDecisionRequested = false;

        IsBusy = true;

        if (movementRoutine != null)
        {
            StopCoroutine(movementRoutine);
        }

        movementRoutine = StartCoroutine(
            MonitorMovement(
                preferredDirection,
                preferredDistance
            )
        );

        Debug.Log(
            $"WANDERER MOVEMENT STARTED\n" +
            $"Desired Direction: {preferredDirection}\n" +
            $"Realized Direction: {candidate.searchDirection}\n" +
            $"Desired Distance: {preferredDistance:F2} m\n" +
            $"Actual Distance: {candidate.actualDistance:F2} m\n" +
            $"Path Length: {candidate.pathLength:F2} m\n" +
            $"Fallback: {candidate.fallbackStage}\n" +
            $"Destination: {candidate.position}",
            this
        );

        return true;
    }

    /// <summary>
    /// Monitors the active route without confusing Unity's asynchronous path creation
    /// with a navigation failure.
    /// </summary>
    private IEnumerator MonitorMovement(
        WandererDirection preferredDirection,
        float preferredDistance)
    {
        Vector3 lastProgressPosition = agent.nextPosition;
        float stalledFor = 0f;

        while (enabled && IsBusy)
        {
            if (!agent.isOnNavMesh)
            {
                Debug.LogWarning(
                    "Wanderer left the NavMesh. Attempting local recovery.",
                    this
                );

                if (!RecoverMovement(
                        preferredDirection,
                        preferredDistance))
                {
                    yield return new WaitForSeconds(
                        recoveryRetryDelay
                    );
                }

                lastProgressPosition = agent.nextPosition;
                stalledFor = 0f;
                yield return null;
                continue;
            }

            // SetDestination triggers asynchronous path calculation. While the path is
            // pending, neither hasPath nor remainingDistance should be used as failure
            // signals.
            if (agent.pathPending)
            {
                yield return null;
                continue;
            }

            if (!nextDecisionRequested)
            {
                float remaining = SafeRemainingDistance();

                float leadDistance =
                    Mathf.Max(
                        agent.stoppingDistance + arrivalTolerance,
                        agent.speed * nextDecisionLeadTime
                    );

                if (remaining >= 0f && remaining <= leadDistance)
                {
                    nextDecisionRequested = true;
                    RequestNextDecision?.Invoke();
                }
            }

            // Check arrival before checking hasPath. NavMeshAgent can legitimately have
            // no active path once the destination has been reached.
            if (HasArrived())
            {
                yield return FinishSuccessfulMovement(
                    preferredDirection,
                    preferredDistance
                );

                yield break;
            }

            bool stillInsidePathGrace =
                Time.time - lastDestinationSetTime <
                pathAcquisitionGrace;

            if (!agent.hasPath ||
                agent.pathStatus != NavMeshPathStatus.PathComplete)
            {
                if (stillInsidePathGrace)
                {
                    yield return null;
                    continue;
                }

                Debug.LogWarning(
                    $"Wanderer's route is unavailable or invalid. " +
                    $"hasPath={agent.hasPath}, status={agent.pathStatus}. " +
                    "Choosing a different local destination.",
                    this
                );

                if (!RecoverMovement(
                        preferredDirection,
                        preferredDistance))
                {
                    yield return new WaitForSeconds(
                        recoveryRetryDelay
                    );
                }

                lastProgressPosition = agent.nextPosition;
                stalledFor = 0f;
                yield return null;
                continue;
            }

            float progress = Vector3.Distance(
                agent.nextPosition,
                lastProgressPosition
            );

            if (progress >= stallPositionEpsilon)
            {
                lastProgressPosition = agent.nextPosition;
                stalledFor = 0f;
            }
            else
            {
                stalledFor += Time.deltaTime;
            }

            float expectedTravelTime =
                activeCandidate.pathLength /
                Mathf.Max(0.1f, agent.speed);

            float movementTimeout = Mathf.Max(
                minimumMovementTimeout,
                expectedTravelTime * 3f + 5f
            );

            float timeOnCurrentDestination =
                Time.time - lastDestinationSetTime;

            bool stalled = stalledFor >= stallTimeout;
            bool timedOut =
                timeOnCurrentDestination >= movementTimeout;

            if (stalled || timedOut)
            {
                Debug.LogWarning(
                    $"Wanderer needs a local replan. " +
                    $"Reason: {(stalled ? "stalled" : "timeout")}. " +
                    $"Remaining: {SafeRemainingDistance():F2} m. " +
                    $"Velocity: {agent.velocity.magnitude:F2} m/s.",
                    this
                );

                if (!RecoverMovement(
                        preferredDirection,
                        preferredDistance))
                {
                    yield return new WaitForSeconds(
                        recoveryRetryDelay
                    );
                }

                lastProgressPosition = agent.nextPosition;
                stalledFor = 0f;
                yield return null;
                continue;
            }

            yield return null;
        }
    }


    /// <summary>
    /// Robust arrival check. remainingDistance is preferred when Unity knows it;
    /// direct distance to the validated endpoint handles the frame where the path has
    /// already been cleared.
    /// </summary>
    private bool HasArrived()
    {
        if (agent == null ||
            !agent.enabled ||
            !agent.isOnNavMesh ||
            agent.pathPending)
        {
            return false;
        }

        float tolerance =
            agent.stoppingDistance +
            arrivalTolerance;

        float remaining =
            agent.remainingDistance;

        if (!float.IsNaN(remaining) &&
            !float.IsInfinity(remaining) &&
            remaining <= tolerance)
        {
            return true;
        }

        float directDistance = Vector3.Distance(
            agent.nextPosition,
            activeCandidate.position
        );

        return directDistance <= tolerance;
    }


    /// <summary>
    /// Finalizes a successful physical trip and raises MovementCompleted only after the
    /// optional Wanderer pause.
    /// </summary>
    private IEnumerator FinishSuccessfulMovement(
        WandererDirection preferredDirection,
        float preferredDistance)
    {
        if (agent.isOnNavMesh)
        {
            agent.ResetPath();
        }

        float waitSeconds = activeDecision != null
            ? Mathf.Max(0f, activeDecision.waitSeconds)
            : 0f;

        if (waitSeconds > 0f)
        {
            yield return new WaitForSeconds(waitSeconds);
        }

        WandererMovementResult result =
            new WandererMovementResult
            {
                desiredDirection =
                    preferredDirection
                        .ToString()
                        .ToLowerInvariant(),

                realizedDirection =
                    activeCandidate.searchDirection
                        .ToString()
                        .ToLowerInvariant(),

                desiredDistance =
                    preferredDistance,

                actualDistance =
                    activeCandidate.actualDistance,

                pathLength =
                    activeCandidate.pathLength,

                fallbackStage =
                    activeCandidate.fallbackStage,

                destination =
                    activeCandidate.position
            };

        IsBusy = false;
        movementRoutine = null;

        Debug.Log(
            $"WANDERER ARRIVED\n" +
            $"Desired Direction: {result.desiredDirection}\n" +
            $"Realized Direction: {result.realizedDirection}\n" +
            $"Actual Distance: {result.actualDistance:F2} m\n" +
            $"Fallback: {result.fallbackStage}",
            this
        );

        MovementCompleted?.Invoke(result);
    }


    /// <summary>
    /// Chooses a different destination after a route fails. This intentionally excludes
    /// the failed endpoint, preventing stall -> same target -> stall loops.
    /// </summary>
    private bool RecoverMovement(
        WandererDirection preferredDirection,
        float preferredDistance)
    {
        RebuildFilter();

        if (!agent.isOnNavMesh &&
            !BindToNavMesh())
        {
            return false;
        }

        Vector3 failedDestination =
            activeCandidate.position;

        DestinationCandidate candidate;

        bool found =
            TryRandomSearch(
                preferredDirection,
                preferredDistance,
                out candidate,
                failedDestination
            ) ||
            TryDeterministicRadialSearch(
                preferredDirection,
                preferredDistance,
                out candidate,
                failedDestination
            ) ||
            TryTriangulationFallback(
                preferredDirection,
                preferredDistance,
                out candidate,
                failedDestination
            );

        if (!found)
        {
            return false;
        }

        EnsureStoppingDistanceAllowsMotion(
            candidate.actualDistance
        );

        agent.isStopped = false;

        if (!agent.SetDestination(candidate.position))
        {
            return false;
        }

        lastDestinationSetTime = Time.time;

        candidate.fallbackStage =
            "movement recovery -> " +
            candidate.fallbackStage;

        activeCandidate = candidate;

        Debug.Log(
            $"WANDERER LOCAL REPLAN\n" +
            $"Previous Destination: {failedDestination}\n" +
            $"New Destination: {candidate.position}\n" +
            $"Separation: {Vector3.Distance(failedDestination, candidate.position):F2} m\n" +
            $"Fallback: {candidate.fallbackStage}",
            this
        );

        return true;
    }


    /// <summary>
    /// Returns true when a recovery candidate is too close to the destination that
    /// already failed. Normal searches pass null and are unaffected.
    /// </summary>
    private bool IsExcludedRecoveryDestination(
        Vector3 candidatePosition,
        Vector3? excludedDestination)
    {
        if (!excludedDestination.HasValue)
        {
            return false;
        }

        float separation = Mathf.Max(
            absoluteMinimumMovement * 1.5f,
            Mathf.Min(
                recoveryDestinationSeparation,
                maximumMovementDistance * 0.10f
            )
        );

        return Vector3.Distance(
            candidatePosition,
            excludedDestination.Value
        ) < separation;
    }


    private float SafeRemainingDistance()
    {
        float value = agent.remainingDistance;

        if (float.IsNaN(value) ||
            float.IsInfinity(value))
        {
            return -1f;
        }

        return value;
    }


    private void EnsureStoppingDistanceAllowsMotion(
        float actualDistance)
    {
        if (actualDistance <= 0f)
        {
            return;
        }

        float maximumUsefulStoppingDistance =
            Mathf.Max(
                0.05f,
                actualDistance * 0.20f
            );

        if (agent.stoppingDistance >=
            actualDistance - 0.05f)
        {
            agent.stoppingDistance =
                maximumUsefulStoppingDistance;
        }
    }

    
    private bool BindToNavMesh()
    {
        RebuildFilter();

        Vector3 source =
            agent != null
                ? transform.position
                : Vector3.zero;

        if (!NavMesh.SamplePosition(
                source,
                out NavMeshHit hit,
                startupSnapDistance,
                filter))
        {
            Debug.LogError(
                $"Wanderer could not find a compatible NavMesh within " +
                $"{startupSnapDistance:F2} units of {source}.",
                this
            );

            return false;
        }

        if (!agent.Warp(hit.position))
        {
            Debug.LogError(
                $"A NavMesh point was found at {hit.position}, " +
                "but NavMeshAgent.Warp rejected it.",
                this
            );

            return false;
        }

        return agent.isOnNavMesh;
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

    private static float CalculatePathLength(
        NavMeshPath navPath)
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

    private WandererDecision CreateNeutralFallbackDecision()
    {
        return new WandererDecision
        {
            direction =
                ((WandererDirection)
                    UnityEngine.Random.Range(0, 8))
                    .ToString()
                    .ToLowerInvariant(),

            distance =
                maximumMovementDistance * 0.5f,

            speed =
                Mathf.Clamp(
                    agent != null ? agent.speed : 20f,
                    minimumSpeed,
                    maximumSpeed
                ),

            acceleration =
                Mathf.Clamp(
                    agent != null ? agent.acceleration : 50f,
                    minimumAcceleration,
                    maximumAcceleration
                ),

            angularSpeed =
                Mathf.Clamp(
                    agent != null ? agent.angularSpeed : 180f,
                    minimumAngularSpeed,
                    maximumAngularSpeed
                ),

            stoppingDistance =
                agent != null
                    ? agent.stoppingDistance
                    : 1f,

            waitSeconds = 0f,
            mood = "persistent",
            thought = "I keep moving."
        };
    }

    private void OnDisable()
    {
        if (movementRoutine != null)
        {
            StopCoroutine(movementRoutine);
            movementRoutine = null;
        }

        if (agent != null &&
            agent.enabled &&
            agent.isOnNavMesh)
        {
            agent.ResetPath();
        }

        IsBusy = false;
        IsInitialized = false;
    }

    private struct DestinationCandidate
    {
        public Vector3 position;
        public float requestedDistance;
        public float actualDistance;
        public float pathLength;
        public float alignment;
        public WandererDirection searchDirection;
        public string fallbackStage;
    }
}
