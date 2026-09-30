using System.Collections;
using TMPro;
using UnityEngine;

/// <summary>
/// Event-driven conductor for the Wanderer lifecycle.
///
/// Loop:
/// 1. Sense terrain.
/// 2. Ask the LLM for one long-term decision.
/// 3. Ask WandererMotor to physically realize that intent.
/// 4. Locally generate mini decisions in the general direction from long-term decision.
/// 5. Record the experience.
/// 6. Ask the LLM again.
///
/// No periodic API polling is used.
/// </summary>
[RequireComponent(typeof(WandererPerception))]
[RequireComponent(typeof(WandererMotor))]
[RequireComponent(typeof(WandererBrain))]
public class WandererController : MonoBehaviour
{
    [SerializeField, Tooltip("Enable detailed navigation diagnostics in the Console.")]
    private bool showDetailedDiagnostics;

    [Header("Expression")]

    [SerializeField]
    [Tooltip("Optional TextMeshPro element used to display Wanderer's thought.")]
    private TMP_Text thoughtText;

    [SerializeField]
    [Tooltip("Optional TextMeshPro element used to display Wanderer's current mood.")]
    private TMP_Text moodText;


    [Header("Reliability")]

    [SerializeField, Min(0.1f)]
    [Tooltip("Delay between local movement retries if no destination is immediately realizable.")]
    private float localRetryDelay = 1f;

    [SerializeField, Min(0.1f)]
    [Tooltip("Delay before retrying a long-term decision after a failed or timed-out request.")]
    private float longTermDecisionRetryDelay = 1f;

    private WandererPerception perception;
    private WandererMotor motor;
    private WandererBrain brain;

    private bool decisionInProgress;
    private Coroutine localRetryRoutine;
    private Coroutine longTermRetryRoutine;
    private Coroutine activeBrainRequestRoutine;
    private WandererDecision lastDecision;
    private int consecutiveReversals;
    private bool oscillationEscapeActive;
    private int oscillationEscapeSide;

    private WandererDecision pendingDecision;

    [Header("Long-Term Goal")]
    [SerializeField]
    [Tooltip("Enable long-term LLM goals that produce many local mini-decisions toward a heading.")]
    private bool enableLongTermGoals = true;

    [SerializeField, Min(0.1f)]
    [Tooltip("Distance for each short mini-movement (meters).")]
    private float miniDistance = 5f;

    [SerializeField, Range(0f, 180f)]
    [Tooltip("Maximum angular deviation (degrees) left/right from the long-term heading for minis.")]
    private float maxAngleDeviation = 20f;

    [SerializeField, Min(0.1f)]
    [Tooltip("Speed used for mini-movements if not provided by the long-term mood.")]
    private float miniSpeed = 20f;

    [SerializeField, Min(0.1f)]
    [Tooltip("Acceleration used for mini-movements.")]
    private float miniAcceleration = 50f;

    [SerializeField, Min(1f)]
    [Tooltip("Angular speed used for mini-movements.")]
    private float miniAngularSpeed = 180f;

    [SerializeField, Min(0f)]
    [Tooltip("Stopping distance used for mini-movements.")]
    private float miniStoppingDistance = 1f;

    [SerializeField, Min(0f)]
    [Tooltip("When the sphere is within this radius (meters) of goal header, request a new long-term goal.")]
    private float longTermGoalRadius = 20f;

    [SerializeField, Min(0.1f)]
    [Tooltip("Maximum mini-step distance (meters) regardless of long-term distance/ratio.")]
    private float maxMiniDistance = 200f;

    [Header("Oscillation Recovery")]
    [SerializeField, Min(2)]
    [Tooltip("Consecutive near-reversals that trigger a movement-history reset and escape route.")]
    private int reversalsBeforeRecovery = 2;

    [SerializeField, Min(10f)]
    [Tooltip("Straight-line distance for the escape waypoint selected after an oscillation is detected.")]
    private float oscillationEscapeDistance = 500f;

    [Header("Long-Term Re-request Rules")]
    [SerializeField, Range(0f, 1f)]
    [Tooltip("Fraction of the long-term distance after which a new long-term goal is requested (1.0 = full distance).")]
    private float reRequestDistanceFraction = 1.0f;

    [SerializeField, Min(1)]
    [Tooltip("Maximum number of mini-steps to execute before requesting a new long-term goal regardless of distance.")]
    private int maxMiniSteps = 12;

    [SerializeField, Min(0f)]
    [Tooltip("Maximum seconds to keep a long-term goal before re-requesting. 0 disables time-based re-request.")]
    private float maxLongTermSeconds = 300f;

    // Runtime counters for long-term re-request policies
    private int miniStepsSinceLongTerm = 0;
    private float longTermStartTime = 0f;
    // Runtime long-term goal state
    private bool hasLongTermGoal = false;
    private float longTermHeadingDegrees = 0f;
    private bool hasFallbackLongTermGoal;
    private float fallbackLongTermHeadingDegrees;
    private float fallbackLongTermDistance;
    private string longTermMood = "curious";
    // Runtime long-term distance (meters) and accumulated forward progress toward the long-term heading.
    private float longTermDistance = 0f;
    private float accumulatedForwardProgress = 0f;
    private Vector3 lastLongTermProgressPosition;
    private bool hasLongTermProgressPosition;
    private int longTermRequestSequence;
    private int activeLongTermRequestId;
    private float activeLongTermRequestStartedAt;
    private Vector3 longTermOriginPosition;
    private readonly Vector3[] recentArrivalPositions = new Vector3[16];
    private int arrivalHistoryCount;
    private int arrivalHistoryNext;
    private Vector3 lastArrivalPosition;
    private bool hasLastArrivalPosition;
    private Vector3 lastArrivalDisplacement;


    private void Awake()
    {
        perception =
            GetComponent<WandererPerception>();

        motor =
            GetComponent<WandererMotor>();

        brain =
            GetComponent<WandererBrain>();

        WandererMotor[] attachedMotors = GetComponents<WandererMotor>();
        DiagnosticLog(
            $"[WANDERER_DIAG][CONTROLLER_SETUP] object={name} controllerId={GetInstanceID()} selectedMotorId={(motor != null ? motor.GetInstanceID() : 0)} attachedMotorCount={attachedMotors.Length} brainId={(brain != null ? brain.GetInstanceID() : 0)} perceptionId={(perception != null ? perception.GetInstanceID() : 0)}",
            this
        );
        if (attachedMotors.Length != 1)
        {
            Debug.LogError(
                $"[WANDERER_DIAG][MOTOR_COUNT] object={name} expected=1 actual={attachedMotors.Length}; controller uses the first GetComponent<WandererMotor>() result.",
                this
            );
        }

        motor.MovementCompleted +=
            HandleMovementCompleted;

        motor.RequestNextDecision +=
            HandleNextDecisionRequest;
    }

    private void OnDestroy()
    {
        if (motor != null)
        {
            motor.MovementCompleted -=
                HandleMovementCompleted;
            motor.RequestNextDecision -=
                HandleNextDecisionRequest;

        }
    }

    private IEnumerator Start()
    {
        while (enabled &&
               !motor.IsInitialized)
        {
            yield return null;
        }

        if (!enabled)
        {
            yield break;
        }

        if (enableLongTermGoals)
        {
            StartLongTermGoal();
        }
        else
        {
            RequestNextDecision();
        }
    }

    private void RequestNextDecision()
    {
        if (!enabled ||
            decisionInProgress ||
            motor.IsBusy)
        {
            return;
        }

        // If we already have a long-term goal, generate a local mini decision instead
        if (hasLongTermGoal)
        {
            WandererDecision mini = GenerateMiniDecision();
            HandleDecision(mini);
            return;
        }

        if (enableLongTermGoals)
        {
            StartLongTermGoal();
            return;
        }

        BeginRequestTracking("idle");

        WandererPerceptionSnapshot snapshot =
            perception.Scan();

        StartBrainRequest(
            brain.RequestDecision(
                snapshot,
                HandleDecision,
                HandleDecisionFailure,
                activeLongTermRequestId
            )
        );
    }
    private void RequestNextDecisionWhileMoving()
    {
        if (!enabled ||
            decisionInProgress ||
            pendingDecision != null)
        {
            return;
        }

        if (enableLongTermGoals)
        {
            StartLongTermGoal(keepCurrentRoute: motor.IsBusy);
            return;
        }

        BeginRequestTracking("while-moving");

        WandererPerceptionSnapshot snapshot =
            perception.Scan();

        StartBrainRequest(
            brain.RequestDecision(
                snapshot,
                HandleDecision,
                HandleDecisionFailure,
                activeLongTermRequestId
            )
        );
    }

    private void HandleDecision(WandererDecision decision)
    {
        HandleDecision(decision, false);
    }

    private void HandleDecision(
        WandererDecision decision,
        bool redirectActiveMovement)
    {
        activeBrainRequestRoutine = null;
        DiagnosticLog(
            $"[WANDERER_DIAG][DECISION_RECEIVED] requestId={activeLongTermRequestId} requestAge={(decisionInProgress ? Time.time - activeLongTermRequestStartedAt : 0f):F2} redirect={redirectActiveMovement} busy={motor.IsBusy} pendingBefore={(pendingDecision != null)} hasGoal={hasLongTermGoal} position={motor.Agent.nextPosition} velocity={motor.Agent.velocity} decision={(decision == null ? "null" : $"heading={decision.headingDegrees:F1},direction={decision.direction},distance={decision.distance:F1},speed={decision.speed:F1},accel={decision.acceleration:F1},mood={decision.mood},thought={decision.thought}")}",
            this
        );
        decisionInProgress = false;
        lastDecision = decision;

        // Only update displayed mood/thought when the decision actually contains them.
        if (!string.IsNullOrWhiteSpace(decision.mood) ||
            !string.IsNullOrWhiteSpace(decision.thought))
        {
            DisplayExpression(decision);
        }

        // Long-term decisions come from the LLM and include mood/thought; minis do not.
        bool isLongTerm = !string.IsNullOrWhiteSpace(decision.mood) || !string.IsNullOrWhiteSpace(decision.thought);

        if (isLongTerm)
        {
            Debug.Log($"[Wanderer] GOAL received direction={decision.direction} distance={decision.distance:F0}m mood={decision.mood} thought=\"{decision.thought}\"", this);
        }
        else
        {
            Debug.Log($"[Wanderer] MINI heading={decision.headingDegrees:F1}° goal={longTermHeadingDegrees:F1}° distance={decision.distance:F0}m speed={decision.speed:F0} accel={decision.acceleration:F0}", this);
        }

        // If the Wanderer is already moving,
        // save this decision for the next movement.
        if (motor.IsBusy)
        {
            if (redirectActiveMovement)
            {
                if (motor.TryMove(decision, true))
                {
                    pendingDecision = null;
                    TrackMiniRouteQuality(isLongTerm, false);
                }
                else
                {
                    // Keep the existing valid route alive while a new direction is
                    // requested; do not queue a blocked mini for another retry.
                    TrackMiniRouteQuality(isLongTerm, true);
                }
                return;
            }

            pendingDecision = decision;
            return;
        }

        // Otherwise, this is the first movement.
        if (motor.TryMove(decision))
        {
            TrackMiniRouteQuality(isLongTerm, false);
        }
        else if (!isLongTerm &&
                 hasLongTermGoal &&
                 motor.TryMoveAnywhere(decision))
        {
            // Start a reachable route immediately using this mini's heading as
            // the bias. This avoids an idle retry delay when the exact mini fails.
            TrackMiniRouteQuality(isLongTerm, false);
        }
        else
        {
            if (!isLongTerm && hasLongTermGoal)
            {
                TrackMiniRouteQuality(isLongTerm, true);
                if (decisionInProgress)
                {
                    return;
                }
            }

            StartLocalRetryLoop();
        }
    }

    private void TrackMiniRouteQuality(bool isLongTerm, bool routeFailed)
    {
        if (isLongTerm || !hasLongTermGoal)
        {
            return;
        }

        float goalAlignment = motor.GetActiveMovementAlignment(longTermHeadingDegrees);
        Vector3 goalVector = HeadingVector(longTermHeadingDegrees);
        float velocityAlignment = motor.Agent.velocity.sqrMagnitude > 0.01f
            ? Vector3.Dot(motor.Agent.velocity.normalized, goalVector)
            : float.NaN;
        float steeringAlignment = motor.Agent.desiredVelocity.sqrMagnitude > 0.01f
            ? Vector3.Dot(motor.Agent.desiredVelocity.normalized, goalVector)
            : float.NaN;
        DiagnosticLog(
            $"[WANDERER_DIAG][ROUTE_QUALITY] failed={routeFailed} goal={longTermHeadingDegrees:F1} threshold=0.50 candidateAlignment={goalAlignment:F2} velocityAlignment={velocityAlignment:F2} steeringAlignment={steeringAlignment:F2} candidate={motor.Agent.destination} directToCandidate={motor.Agent.destination - motor.Agent.nextPosition} velocity={motor.Agent.velocity} desired={motor.Agent.desiredVelocity} route={motor.Agent.pathStatus} busy={motor.IsBusy} miniCount={miniStepsSinceLongTerm} progress={accumulatedForwardProgress:F1}/{longTermDistance:F1}",
            this
        );
        if (!routeFailed && goalAlignment >= 0.5f)
        {
            return;
        }

        string reason = routeFailed
            ? "mini-route-unavailable"
            : $"route-away-from-goal alignment={goalAlignment:F2}";
        DiagnosticLog(
            $"[WANDERER_DIAG][GOAL_REFRESH] reason={reason} oldGoal={longTermHeadingDegrees:F1} oldDistance={longTermDistance:F1} minis={miniStepsSinceLongTerm} progress={accumulatedForwardProgress:F1} goalAge={Time.time - longTermStartTime:F1} busy={motor.IsBusy} target={motor.Agent.destination} position={motor.Agent.nextPosition} velocity={motor.Agent.velocity}",
            this
        );

        hasLongTermGoal = false;
        accumulatedForwardProgress = 0f;
        miniStepsSinceLongTerm = 0;
        longTermStartTime = 0f;
        hasLongTermProgressPosition = false;
        StartLongTermGoal(keepCurrentRoute: true);
    }

    private void HandleDecisionFailure(
        string error)
    {
        activeBrainRequestRoutine = null;
        decisionInProgress = false;

        Debug.LogWarning(
            $"[Wanderer] Goal request failed; continuing with local movement. {error}",
            this
        );

        lastDecision =
            CreateLocalFallbackDecision();

        DisplayExpression(lastDecision);

        if (!motor.TryMove(lastDecision) &&
            !motor.TryMoveAnywhere(lastDecision))
        {
            StartLocalRetryLoop();
        }
    }

    private void HandleLongTermDecisionFailure(string error)
    {
        activeBrainRequestRoutine = null;
        decisionInProgress = false;
        Debug.LogWarning(
            $"[Wanderer] Long-term goal request failed; continuing toward {(hasFallbackLongTermGoal ? $"the previous heading {fallbackLongTermHeadingDegrees:F0}°" : "a local fallback")}. {error}",
            this
        );

        // Preserve a valid route on request failure. If it completed, continue
        // using small moves biased toward the last accepted long-term heading.
        if (!motor.IsBusy)
        {
            if (hasFallbackLongTermGoal)
            {
                if (!TryStartPendingMini(false)) StartLocalRetryLoop();
            }
            else if (!motor.TryMoveContinuously(lastDecision) &&
                     !motor.TryMoveAnywhere(lastDecision))
            {
                StartLocalRetryLoop();
            }
        }

        if (!enableLongTermGoals || longTermRetryRoutine != null)
        {
            return;
        }

        longTermRetryRoutine = StartCoroutine(RetryLongTermGoalAfterFailure());
    }

    private IEnumerator RetryLongTermGoalAfterFailure()
    {
        yield return new WaitForSecondsRealtime(longTermDecisionRetryDelay);
        longTermRetryRoutine = null;

        if (enabled && enableLongTermGoals && !decisionInProgress)
        {
            Debug.Log(
                "[Wanderer] Requesting a new long-term goal after the previous request failed.",
                this
            );
            StartLongTermGoal(keepCurrentRoute: motor.IsBusy);
        }
    }

    private void HandleMovementCompleted(
        WandererMovementResult result)
    {
        Vector3 position = motor.Agent.nextPosition;
        Vector3 arrivalDelta = hasLastArrivalPosition
            ? position - lastArrivalPosition
            : Vector3.zero;
        arrivalDelta.y = 0f;
        Vector3 goalNet = position - longTermOriginPosition;
        goalNet.y = 0f;
        float headingProgress = hasLongTermGoal
            ? Vector3.Dot(arrivalDelta, HeadingVector(longTermHeadingDegrees))
            : 0f;
        int revisits = RecordArrival(position);
        float reversal = lastArrivalDisplacement.sqrMagnitude > 0.001f && arrivalDelta.sqrMagnitude > 0.001f
            ? Vector3.Dot(lastArrivalDisplacement.normalized, arrivalDelta.normalized)
            : 1f;
        DiagnosticLog(
            $"[WANDERER_DIAG][ARRIVAL_STATE] position={position} delta={arrivalDelta} revisitsIn16={revisits} reversalDot={reversal:F2} goalActive={hasLongTermGoal} goal={longTermHeadingDegrees:F1} goalOrigin={longTermOriginPosition} goalNet={goalNet.magnitude:F1} goalNetVector={goalNet} stepHeadingProgress={headingProgress:F1} accumulatedForward={accumulatedForwardProgress:F1}/{longTermDistance:F1} miniCount={miniStepsSinceLongTerm} result={result.desiredDirection}->{result.realizedDirection} fallback='{result.fallbackStage}' requestPending={decisionInProgress} requestId={activeLongTermRequestId} pendingDecision={(pendingDecision != null)}",
            this
        );
        if (revisits > 0 || reversal < -0.5f)
        {
            DiagnosticLog(
                $"[WANDERER_DIAG][POSSIBLE_CYCLE] position={position} recentRevisits={revisits} reversalDot={reversal:F2} recentPositions={FormatRecentArrivalPositions()}",
                this
            );
        }
        if (arrivalDelta.sqrMagnitude > 0.001f)
        {
            lastArrivalDisplacement = arrivalDelta;
        }
        lastArrivalPosition = position;
        hasLastArrivalPosition = true;

        if (oscillationEscapeActive)
        {
            consecutiveReversals = 0;
            if (!decisionInProgress)
            {
                oscillationEscapeActive = false;
                Debug.Log(
                    $"[Wanderer] Recovery complete at {position:F1}; resuming navigation.",
                    this
                );
            }
        }
        else if (reversal < -0.75f)
        {
            consecutiveReversals++;
        }
        else
        {
            consecutiveReversals = 0;
        }

        if (!oscillationEscapeActive &&
            consecutiveReversals >= Mathf.Max(2, reversalsBeforeRecovery))
        {
            BeginOscillationRecovery(position, arrivalDelta);
            return;
        }

        brain.RememberMovement(result);

        // If following a long-term heading, accumulate forward progress toward it.
        if (hasLongTermGoal)
        {
            AccumulateForwardProgress();
            Vector3 goalNetAfterStep = motor.Agent.nextPosition - longTermOriginPosition;
            goalNetAfterStep.y = 0f;
            DiagnosticLog(
                $"[WANDERER_DIAG][PROGRESS_AFTER_ARRIVAL] goal={longTermHeadingDegrees:F1} netDistance={goalNetAfterStep.magnitude:F1} netVector={goalNetAfterStep} positiveForwardAccumulator={accumulatedForwardProgress:F1} goalDistance={longTermDistance:F1} miniCount={miniStepsSinceLongTerm}",
                this
            );
            if (TryRequestNextLongTermGoal()) return;
        }

        if (localRetryRoutine != null)
        {
            StopCoroutine(localRetryRoutine);
            localRetryRoutine = null;
        }

        // Consume a mini that was generated by the motor's look-ahead event. This
        // must come before the active long-term-goal branch, or that branch would
        // keep generating new minis and leave pendingDecision stuck forever.
        if (pendingDecision != null)
        {
            WandererDecision nextDecision =
                pendingDecision;

            pendingDecision = null;

            if (!motor.TryMove(nextDecision))
            {
                if (!motor.TryMoveAnywhere(nextDecision))
                {
                    StartLocalRetryLoop();
                }
            }

            return;
        }

        // No mini is queued, so continue the active long-term goal locally.
        if (hasLongTermGoal)
        {
            WandererDecision nextMini = GenerateMiniDecision();
            HandleDecision(nextMini, true);
            return;
        }

        if (!decisionInProgress && hasFallbackLongTermGoal)
        {
            if (!TryStartPendingMini(false)) StartLocalRetryLoop();
            return;
        }

        // Keep the current travel direction while the replacement goal is in flight.
        // Replaying lastDecision here can repeatedly select opposing fallback routes.
        if (decisionInProgress)
        {
            ContinueMovementWhileWaiting();
            return;
        }

        if (!motor.TryMove(lastDecision) && !motor.TryMoveAnywhere(lastDecision))
        {
            StartLocalRetryLoop();
        }
    }
    private void HandleNextDecisionRequest()
    {
        DiagnosticLog(
            $"[WANDERER_DIAG][NEXT_DECISION_EVENT] enabled={enabled} requestInProgress={decisionInProgress} busy={motor.IsBusy} goalActive={hasLongTermGoal} pending={(pendingDecision != null)} requestId={activeLongTermRequestId} position={motor.Agent.nextPosition} destination={motor.Agent.destination} remaining={(motor.Agent.hasPath ? motor.Agent.remainingDistance : -1f):F1} velocity={motor.Agent.velocity}",
            this
        );
        if (!enabled ||
            decisionInProgress ||
            pendingDecision != null)
        {
            return;
        }

        // If a long-term goal is active, generate a local mini-decision instead of calling the LLM.
        if (hasLongTermGoal)
        {
            AccumulateForwardProgress();
            if (TryRequestNextLongTermGoal())
            {
                return;
            }

            WandererDecision mini = GenerateMiniDecision();
            HandleDecision(mini, true);
            return;
        }

        // In long-term mode every LLM response must establish a goal and its
        // mini-movement sequence through OnLongTermDecision.
        if (enableLongTermGoals)
        {
            StartLongTermGoal(keepCurrentRoute: motor.IsBusy);
            return;
        }

        BeginRequestTracking("lookahead");

        WandererPerceptionSnapshot snapshot =
            perception.Scan();

        StartBrainRequest(
            brain.RequestDecision(
                snapshot,
                HandleDecision,
                HandleDecisionFailure,
                activeLongTermRequestId
            )
        );
    }

    /// <summary>
    /// Asks the LLM once for a long-term goal (coarse heading and mood),
    /// then begins generating local mini-decisions toward that heading.
    /// </summary>
    private void StartLongTermGoal(bool keepCurrentRoute = false)
    {
        if (!enabled || decisionInProgress)
        {
            return;
        }

        BeginRequestTracking(keepCurrentRoute ? "route-refresh" : "long-term-start");

        DiagnosticLog(
            $"[WANDERER_DIAG][GOAL_REQUEST_START] requestId={activeLongTermRequestId} keepRoute={keepCurrentRoute} hasGoal={hasLongTermGoal} priorGoal={longTermHeadingDegrees:F1} position={motor.Agent.nextPosition} busy={motor.IsBusy} path={motor.Agent.hasPath} pendingPath={motor.Agent.pathPending} velocity={motor.Agent.velocity} previousDecision={(lastDecision == null ? "none" : $"{lastDecision.direction}/{lastDecision.headingDegrees:F1}°/{lastDecision.distance:F1}m")}",
            this
        );

        WandererPerceptionSnapshot snapshot = perception.Scan();

        StartBrainRequest(
            brain.RequestDecision(
                snapshot,
                OnLongTermDecision,
                HandleLongTermDecisionFailure,
                activeLongTermRequestId
            )
        );

        // A request can outlast the current mini-route. Extend the active heading
        // with a longer route while waiting, except during the dedicated escape
        // waypoint, which must remain straight until it is reached.
        if (!oscillationEscapeActive)
        {
            ContinueMovementWhileWaiting();
        }
    }

    private void BeginRequestTracking(string source)
    {
        decisionInProgress = true;
        activeLongTermRequestId = ++longTermRequestSequence;
        activeLongTermRequestStartedAt = Time.time;
        DiagnosticLog(
            $"[WANDERER_DIAG][REQUEST_BEGIN] requestId={activeLongTermRequestId} source={source} position={motor.Agent.nextPosition} busy={motor.IsBusy} goalActive={hasLongTermGoal} pending={(pendingDecision != null)} target={motor.Agent.destination}",
            this
        );
    }

    private void ContinueMovementWhileWaiting()
    {
        if (motor == null)
        {
            return;
        }

        if (decisionInProgress)
        {
            if (hasFallbackLongTermGoal && !oscillationEscapeActive)
            {
                TryStartPendingMini(motor.IsBusy);
                return;
            }

            bool continued = motor.TryMoveContinuously(lastDecision);
            if (!continued)
            {
                continued = motor.TryMoveAnywhere(lastDecision, replaceActiveMovement: motor.IsBusy);
            }
            if (!continued && !motor.IsBusy)
            {
                StartLocalRetryLoop();
            }
            return;
        }

        // Reuse the last exact heading when possible. Mini decisions have no
        // compass direction string, so TryMoveAnywhere would choose a random one.
        bool wasBusy = motor.IsBusy;
        if (wasBusy)
        {
            if (!motor.TryMove(lastDecision, true))
            {
                Debug.LogWarning("[Wanderer] Could not extend the current route while waiting for a goal.", this);
            }
            return;
        }

        if (!motor.TryMove(lastDecision) && !motor.TryMoveAnywhere(lastDecision))
        {
            StartLocalRetryLoop();
        }
    }

    private void AccumulateForwardProgress()
    {
        if (motor == null || motor.Agent == null || !motor.Agent.isOnNavMesh)
        {
            return;
        }

        Vector3 current = motor.Agent.nextPosition;
        if (hasLongTermProgressPosition)
        {
            Vector3 displacement = current - lastLongTermProgressPosition;
            displacement.y = 0f;
            float radians = longTermHeadingDegrees * Mathf.Deg2Rad;
            Vector3 goalDirection = new Vector3(Mathf.Sin(radians), 0f, Mathf.Cos(radians));
            float forward = Vector3.Dot(displacement, goalDirection);
            if (forward > 0f)
            {
                accumulatedForwardProgress += forward;
            }

            Vector3 net = current - longTermOriginPosition;
            net.y = 0f;
            DiagnosticLog(
                $"[WANDERER_DIAG][PROGRESS_ACCOUNTING] displacement={displacement} signedForward={forward:F1} accumulatedPositive={accumulatedForwardProgress:F1} goalNet={net.magnitude:F1} netVector={net} goal={longTermHeadingDegrees:F1}",
                this
            );
        }

        lastLongTermProgressPosition = current;
        hasLongTermProgressPosition = true;
    }

    private bool TryRequestNextLongTermGoal()
    {
        bool reachedDistanceFraction = longTermDistance > 0f &&
            reRequestDistanceFraction > 0f &&
            accumulatedForwardProgress >= longTermDistance * reRequestDistanceFraction;
        bool reachedMiniCount = maxMiniSteps > 0 && miniStepsSinceLongTerm >= maxMiniSteps;
        bool reachedMaxTime = maxLongTermSeconds > 0f &&
            Time.time - longTermStartTime >= maxLongTermSeconds;
        bool reachedGoalRadius = longTermGoalRadius > 0f &&
            accumulatedForwardProgress >= longTermGoalRadius;

        DiagnosticLog(
            $"[WANDERER_DIAG][GOAL_POLICY] goal={longTermHeadingDegrees:F1} distance={longTermDistance:F1} forwardProgress={accumulatedForwardProgress:F1} goalRadius={longTermGoalRadius:F1} distanceFraction={reRequestDistanceFraction:F2} miniCount={miniStepsSinceLongTerm}/{maxMiniSteps} goalAge={Time.time - longTermStartTime:F1}/{maxLongTermSeconds:F1} reachedDistance={reachedDistanceFraction} reachedRadius={reachedGoalRadius} reachedMiniCount={reachedMiniCount} reachedTime={reachedMaxTime}",
            this
        );

        if (!reachedDistanceFraction && !reachedMiniCount && !reachedMaxTime && !reachedGoalRadius)
        {
            return false;
        }

        string reason = reachedDistanceFraction ? "distance" :
            reachedMiniCount ? "mini-count" : reachedMaxTime ? "time" : "progress";
        DiagnosticLog($"[WANDERER_DIAG][GOAL_REFRESH] reason={reason} progress={accumulatedForwardProgress:F0}/{longTermDistance:F0}m minis={miniStepsSinceLongTerm} goal={longTermHeadingDegrees:F1} position={motor.Agent.nextPosition} busy={motor.IsBusy}", this);

        hasLongTermGoal = false;
        accumulatedForwardProgress = 0f;
        miniStepsSinceLongTerm = 0;
        longTermStartTime = 0f;
        hasLongTermProgressPosition = false;
        StartLongTermGoal();
        return true;
    }

    private void OnLongTermDecision(WandererDecision decision)
    {
        activeBrainRequestRoutine = null;
        float priorHeading = longTermHeadingDegrees;
        DiagnosticLog(
            $"[WANDERER_DIAG][GOAL_RESPONSE] requestId={activeLongTermRequestId} elapsed={Time.time - activeLongTermRequestStartedAt:F2} rawHeading={(decision != null ? decision.headingDegrees.ToString("F1") : "null")} rawDirection={decision?.direction} distance={(decision != null ? decision.distance.ToString("F1") : "null")} busy={motor.IsBusy} pendingDecision={(pendingDecision != null)} position={motor.Agent.nextPosition} target={motor.Agent.destination} velocity={motor.Agent.velocity} recentArrivals={FormatRecentArrivalPositions()}",
            this
        );
        // Mark that the LLM work is finished.
        decisionInProgress = false;

        if (decision == null)
        {
            HandleLongTermDecisionFailure("Long-term decision was null.");
            return;
        }

        // If the decision contains an explicit headingDegrees use it; otherwise convert the coarse direction.
        if (decision.headingDegrees >= 0f)
        {
            longTermHeadingDegrees = decision.headingDegrees;
        }
        else
        {
            if (!WandererDirectionExtensions.TryParse(decision.direction, out WandererDirection d))
            {
                d = WandererDirection.North;
            }

            longTermHeadingDegrees = d.ToHeadingDegrees();
        }

        longTermMood = string.IsNullOrWhiteSpace(decision.mood)
            ? "curious"
            : decision.mood;

        // Record the long-term distance if provided by the LLM decision.
        longTermDistance = decision.distance > 0f ? decision.distance : 0f;
        hasFallbackLongTermGoal = true;
        fallbackLongTermHeadingDegrees = longTermHeadingDegrees;
        fallbackLongTermDistance = longTermDistance;

        DiagnosticLog($"[WANDERER_DIAG][GOAL_ACTIVE] requestId={activeLongTermRequestId} priorHeading={priorHeading:F1} newHeading={longTermHeadingDegrees:F1} turn={Mathf.DeltaAngle(priorHeading, longTermHeadingDegrees):F1} distance={longTermDistance:F1} mood={longTermMood} thought=\"{decision.thought}\" position={motor.Agent.nextPosition} busy={motor.IsBusy} currentTarget={motor.Agent.destination}", this);

        // Show the long-term mood/thought immediately (do not let minis overwrite it).
        DisplayExpression(decision);

        hasLongTermGoal = true;
        // Reset counters/timers for the new long-term decision so re-request policies start fresh.
        miniStepsSinceLongTerm = 0;
        longTermStartTime = Time.time;
        lastLongTermProgressPosition = motor.Agent.nextPosition;
        hasLongTermProgressPosition = true;
        longTermOriginPosition = motor.Agent.nextPosition;

        if (oscillationEscapeActive)
        {
            DiagnosticLog(
                $"[WANDERER][OSCILLATION_ESCAPE_GOAL_READY] heading={longTermHeadingDegrees:F1} distance={longTermDistance:F1}; applying it when the escape waypoint is complete.",
                this
            );
            return;
        }

        // Immediately generate and start the first mini-decision toward the long-term heading.
        WandererDecision mini = GenerateMiniDecision();
        HandleDecision(mini, true);
    }

    private WandererDecision GenerateMiniDecision()
    {
        if (!hasLongTermGoal)
        {
            return CreateLocalFallbackDecision();
        }
        float offset = UnityEngine.Random.Range(-maxAngleDeviation, maxAngleDeviation);
        float heading = longTermHeadingDegrees + offset;

        // Normalize heading to [0,360)
        heading = Mathf.Repeat(heading, 360f);

        // Prefer configured miniDistance, but if a long-term goal exists use a fraction of it.
        float useDistance = miniDistance;
        if (longTermDistance > 0f)
        {
            useDistance = longTermDistance / 5f;
        }

        // Clamp mini distance to a sensible range so minis remain usable but not tiny.
        useDistance = Mathf.Clamp(useDistance, Mathf.Max(0.01f, miniDistance), maxMiniDistance);

        // Count this mini for long-term re-request policy.
        // Keep this increment local to GenerateMiniDecision so the policy logic stays simple.
        miniStepsSinceLongTerm++;

        DiagnosticLog(
            $"[WANDERER_DIAG][MINI_GENERATED] goal={longTermHeadingDegrees:F1} randomOffset={offset:F1} resultingHeading={heading:F1} quantizedCompass={WandererDirectionExtensions.ClosestToVector(HeadingVector(heading))} goalDistance={longTermDistance:F1} requestedMini={useDistance:F1} configuredMiniDistance={miniDistance:F1} miniCap={maxMiniDistance:F1} miniIndex={miniStepsSinceLongTerm} maxMiniSteps={maxMiniSteps} speed={miniSpeed:F1} acceleration={miniAcceleration:F1} stopping={miniStoppingDistance:F2} position={motor.Agent.nextPosition}",
            this
        );

        return new WandererDecision
        {
            // Use headingDegrees so Motor will use arbitrary vector search.
            headingDegrees = heading,
            distance = useDistance,
            speed = miniSpeed,
            acceleration = miniAcceleration,
            angularSpeed = miniAngularSpeed,
            stoppingDistance = miniStoppingDistance,
            waitSeconds = 0f,
            // Do not set mood or thought for mini-decisions. Preserve the long-term expression until the next LLM decision.
            mood = string.Empty,
            thought = string.Empty
        };
    }

    private bool TryStartPendingMini(bool replaceActiveMovement)
    {
        if (!hasFallbackLongTermGoal || motor == null || motor.Agent == null)
        {
            return false;
        }

        float offset = Random.Range(-maxAngleDeviation, maxAngleDeviation);
        float heading = Mathf.Repeat(fallbackLongTermHeadingDegrees + offset, 360f);
        float distance = fallbackLongTermDistance > 0f
            ? fallbackLongTermDistance / 5f
            : miniDistance;
        distance = Mathf.Clamp(distance, Mathf.Max(0.01f, miniDistance), maxMiniDistance);
        var mini = new WandererDecision
        {
            headingDegrees = heading,
            distance = distance,
            speed = Mathf.Max(miniSpeed, motor.Agent.speed),
            acceleration = Mathf.Max(miniAcceleration, motor.Agent.acceleration),
            angularSpeed = Mathf.Max(miniAngularSpeed, motor.Agent.angularSpeed),
            stoppingDistance = 0f,
            waitSeconds = 0f,
            mood = string.Empty,
            thought = string.Empty
        };

        bool started = motor.TryMove(mini, replaceActiveMovement) ||
            motor.TryMoveAnywhere(mini, replaceActiveMovement);
        Debug.Log(
            $"[Wanderer] MINI while goal request is pending: started={started} replace={replaceActiveMovement} heading={heading:F1} goal={fallbackLongTermHeadingDegrees:F1} distance={distance:F1} busy={motor.IsBusy} position={motor.Agent.nextPosition}",
            this
        );
        if (started)
        {
            lastDecision = mini;
        }
        return started;
    }

    private void StartLocalRetryLoop()
    {
        if (localRetryRoutine != null)
        {
            return;
        }

        localRetryRoutine =
            StartCoroutine(
                RetryMovementUntilItStarts()
            );
    }

    private void BeginOscillationRecovery(Vector3 position, Vector3 lastDisplacement)
    {
        Vector3 travelAxis = lastDisplacement;
        travelAxis.y = 0f;
        if (travelAxis.sqrMagnitude < 0.001f)
        {
            travelAxis = motor.Agent.velocity;
            travelAxis.y = 0f;
        }
        if (travelAxis.sqrMagnitude < 0.001f)
        {
            travelAxis = transform.forward;
            travelAxis.y = 0f;
        }
        travelAxis.Normalize();

        Vector3 escapeDirection = Vector3.Cross(Vector3.up, travelAxis).normalized;
        if ((oscillationEscapeSide++ & 1) != 0)
        {
            escapeDirection = -escapeDirection;
        }

        Debug.LogWarning(
            $"[Wanderer] Repeated reversals detected at {position:F1}; taking a {oscillationEscapeDistance:F0} m recovery route.",
            this
        );

        if (localRetryRoutine != null)
        {
            StopCoroutine(localRetryRoutine);
            localRetryRoutine = null;
        }
        CancelBrainRequest();
        hasLongTermGoal = false;
        hasFallbackLongTermGoal = false;
        fallbackLongTermDistance = 0f;
        longTermHeadingDegrees = 0f;
        longTermDistance = 0f;
        longTermMood = "curious";
        accumulatedForwardProgress = 0f;
        miniStepsSinceLongTerm = 0;
        longTermStartTime = 0f;
        hasLongTermProgressPosition = false;
        longTermOriginPosition = position;
        pendingDecision = null;
        lastDecision = null;
        brain.ClearMovementMemory();
        ClearArrivalHistory(position);
        consecutiveReversals = 0;
        oscillationEscapeActive = true;

        WandererDecision escapeDecision = new WandererDecision
        {
            headingDegrees = Mathf.Repeat(
                Mathf.Atan2(escapeDirection.x, escapeDirection.z) * Mathf.Rad2Deg,
                360f
            ),
            distance = oscillationEscapeDistance,
            speed = Mathf.Max(miniSpeed, motor.Agent.speed),
            acceleration = Mathf.Max(miniAcceleration, motor.Agent.acceleration),
            angularSpeed = Mathf.Max(miniAngularSpeed, motor.Agent.angularSpeed),
            stoppingDistance = 0f,
            waitSeconds = 0f,
            mood = string.Empty,
            thought = string.Empty
        };

        bool escapeStarted = motor.TryMoveOscillationEscape(
            escapeDirection,
            oscillationEscapeDistance,
            escapeDecision
        );

        if (!escapeStarted)
        {
            Debug.LogWarning(
                "[WANDERER][OSCILLATION_ESCAPE_FALLBACK] No straight lateral NavMesh route was available; trying the best reachable forward route.",
                this
            );
            escapeStarted = motor.TryMoveContinuously(escapeDecision) ||
                motor.TryMoveAnywhere(escapeDecision);
        }

        if (!escapeStarted)
        {
            StartLocalRetryLoop();
        }

        StartLongTermGoal(keepCurrentRoute: true);
    }

    private void ClearArrivalHistory(Vector3 currentPosition)
    {
        arrivalHistoryCount = 0;
        arrivalHistoryNext = 0;
        lastArrivalPosition = currentPosition;
        hasLastArrivalPosition = false;
        lastArrivalDisplacement = Vector3.zero;
        RecordArrival(currentPosition);
    }

    private void StartBrainRequest(IEnumerator request)
    {
        activeBrainRequestRoutine = StartCoroutine(request);
    }

    private void CancelBrainRequest()
    {
        if (activeBrainRequestRoutine != null)
        {
            StopCoroutine(activeBrainRequestRoutine);
            activeBrainRequestRoutine = null;
        }
        decisionInProgress = false;
    }

    private IEnumerator RetryMovementUntilItStarts()
    {
        while (enabled &&
               !motor.IsBusy)
        {
            yield return new WaitForSeconds(
                localRetryDelay
            );

            bool started = decisionInProgress
                ? motor.TryMoveContinuously(lastDecision)
                : motor.TryMove(lastDecision) || motor.TryMoveAnywhere(lastDecision);

            if (!started && decisionInProgress)
            {
                started = motor.TryMoveAnywhere(lastDecision);
            }

            if (started)
            {
                localRetryRoutine = null;
                yield break;
            }

            Debug.LogWarning(
                "Wanderer still cannot find a reachable NavMesh destination. " +
                "Retrying locally without making another LLM request.",
                this
            );
        }

        localRetryRoutine = null;
    }

    private int RecordArrival(Vector3 position)
    {
        int revisits = 0;
        for (int i = 0; i < arrivalHistoryCount; i++)
        {
            if (Vector3.Distance(position, recentArrivalPositions[i]) <= 10f)
            {
                revisits++;
            }
        }

        recentArrivalPositions[arrivalHistoryNext] = position;
        arrivalHistoryNext = (arrivalHistoryNext + 1) % recentArrivalPositions.Length;
        arrivalHistoryCount = Mathf.Min(arrivalHistoryCount + 1, recentArrivalPositions.Length);
        return revisits;
    }

    private string FormatRecentArrivalPositions()
    {
        System.Text.StringBuilder builder = new System.Text.StringBuilder();
        int oldest = arrivalHistoryCount == recentArrivalPositions.Length
            ? arrivalHistoryNext
            : 0;
        for (int i = 0; i < arrivalHistoryCount; i++)
        {
            if (i > 0) builder.Append(" -> ");
            builder.Append(recentArrivalPositions[(oldest + i) % recentArrivalPositions.Length].ToString("F0"));
        }
        return builder.ToString();
    }

    private static Vector3 HeadingVector(float headingDegrees)
    {
        float radians = headingDegrees * Mathf.Deg2Rad;
        return new Vector3(Mathf.Sin(radians), 0f, Mathf.Cos(radians));
    }

    private void DisplayExpression(WandererDecision decision)
    {
        if (thoughtText != null)
        {
            // Clear the previous rendered text first.
            thoughtText.text = string.Empty;
            thoughtText.ForceMeshUpdate();

            // Display the new thought.
            thoughtText.text =
                decision != null
                    ? decision.thought
                    : string.Empty;

            thoughtText.ForceMeshUpdate();
        }

        if (moodText != null)
        {
            moodText.text = string.Empty;
            moodText.ForceMeshUpdate();

            moodText.text =
                decision != null
                    ? decision.mood
                    : string.Empty;

            moodText.ForceMeshUpdate();
        }
    }

    private WandererDecision CreateLocalFallbackDecision()
    {
        WandererDirection randomDirection =
            (WandererDirection)
                Random.Range(0, 8);

        return new WandererDecision
        {
            direction =
                randomDirection
                    .ToString()
                    .ToLowerInvariant(),

            distance = 100f,
            speed = 20f,
            acceleration = 50f,
            angularSpeed = 180f,
            stoppingDistance = 1f,
            waitSeconds = 0f,
            mood = "persistent",
            thought =
                "The world has gone quiet, but I still feel like moving."
        };
    }

    private void DiagnosticLog(object message, UnityEngine.Object context = null)
    {
        if (showDetailedDiagnostics)
        {
            Debug.Log(message, context != null ? context : this);
        }
    }
}
