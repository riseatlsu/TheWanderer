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


    private WandererPerception perception;
    private WandererMotor motor;
    private WandererBrain brain;

    private bool decisionInProgress;
    private Coroutine localRetryRoutine;
    private WandererDecision lastDecision;

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
    // When a re-request is triggered but a pending mini exists, queue the re-request.
    private bool reRequestQueued = false;

    // Runtime long-term goal state
    private bool hasLongTermGoal = false;
    private float longTermHeadingDegrees = 0f;
    private string longTermMood = "curious";
    // Runtime long-term distance (meters) and accumulated forward progress toward the long-term heading.
    private float longTermDistance = 0f;
    private float accumulatedForwardProgress = 0f;


    private void Awake()
    {
        perception =
            GetComponent<WandererPerception>();

        motor =
            GetComponent<WandererMotor>();

        brain =
            GetComponent<WandererBrain>();

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
        string motorBusyStr = motor != null ? motor.IsBusy.ToString() : "null";
        string pendingStr = (pendingDecision != null).ToString();
        Debug.Log($"RequestNextDecision called: enabled={enabled}, decisionInProgress={decisionInProgress}, motor.IsBusy={motorBusyStr}, hasLongTermGoal={hasLongTermGoal}, pendingDecision={pendingStr}", this); // no-op patch

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

        decisionInProgress = true;

        WandererPerceptionSnapshot snapshot =
            perception.Scan();

        StartCoroutine(
            brain.RequestDecision(
                snapshot,
                HandleDecision,
                HandleDecisionFailure
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

        decisionInProgress = true;

        WandererPerceptionSnapshot snapshot =
            perception.Scan();

        StartCoroutine(
            brain.RequestDecision(
                snapshot,
                HandleDecision,
                HandleDecisionFailure
            )
        );
    }

    private void HandleDecision(
        WandererDecision decision)
    {
        decisionInProgress = false;
        lastDecision = decision;

        // Only update displayed mood/thought when the decision actually contains them.
        if (!string.IsNullOrWhiteSpace(decision.mood) ||
            !string.IsNullOrWhiteSpace(decision.thought))
        {
            DisplayExpression(decision);
        }

        string motorBusyStr2 = motor != null ? motor.IsBusy.ToString() : "null";
        string pendingStr2 = (pendingDecision != null).ToString();
        // Classify decision: long-term decisions come from the LLM and include mood/thought; minis do not.
        bool isLongTerm = !string.IsNullOrWhiteSpace(decision.mood) || !string.IsNullOrWhiteSpace(decision.thought);

        if (isLongTerm)
        {
            Debug.Log($"LONG-TERM DECISION RECEIVED (LLM) -- headingDegrees={decision.headingDegrees}, direction={decision.direction}, distance={decision.distance} m, mood='{decision.mood}', thought='{decision.thought}'", this);
            Debug.Log($"Explanation: LLM chose a broad goal toward {decision.direction ?? decision.headingDegrees.ToString()+"°"} for {decision.distance:F1} m. Minis will be approx {decision.distance/5f:F1} m (capped at {maxMiniDistance} m).", this);
        }
        else
        {
            Debug.Log($"MINI DECISION GENERATED (local) -- headingDegrees={decision.headingDegrees}, distance={decision.distance} m, speed={decision.speed}, accel={decision.acceleration}", this);
            Debug.Log($"Explanation: Local mini-step toward {decision.headingDegrees:F1}° for {decision.distance:F2} m to nudge movement toward long-term heading.", this);
        }

        Debug.Log(
            $"WANDERER DECISION\n" +
            $"Direction: {decision.direction}\n" +
            $"Distance: {decision.distance:F2} m\n" +
            $"Speed: {decision.speed:F2} m/s\n" +
            $"Acceleration: {decision.acceleration:F2} m/s²\n" +
            $"Angular Speed: {decision.angularSpeed:F2} deg/s\n" +
            $"Stopping Distance: {decision.stoppingDistance:F2} m\n" +
            $"Wait: {decision.waitSeconds:F2} s\n" +
            $"Mood: {decision.mood}\n" +
            $"Thought: {decision.thought}",
            this
        );

        // If the Wanderer is already moving,
        // save this decision for the next movement.
        if (motor.IsBusy)
        {
            pendingDecision = decision;
            return;
        }

        // Otherwise, this is the first movement.
        if (!motor.TryMove(decision))
        {
            StartLocalRetryLoop();
        }
    }

    private void HandleDecisionFailure(
        string error)
    {
        decisionInProgress = false;

        Debug.LogWarning(
            $"WANDERER BRAIN UNAVAILABLE\n{error}\n" +
            "Using local movement until the next physical arrival.",
            this
        );

        lastDecision =
            CreateLocalFallbackDecision();

        DisplayExpression(lastDecision);

        if (!motor.TryMoveAnywhere(lastDecision))
        {
            StartLocalRetryLoop();
        }
    }

    private void HandleMovementCompleted(
        WandererMovementResult result)
    {
        brain.RememberMovement(result);

        string motorBusyStr3 = motor != null ? motor.IsBusy.ToString() : "null";
        string pendingStr3 = (pendingDecision != null).ToString();
        string lastDecisionDistStr = lastDecision != null ? lastDecision.distance.ToString() : "null";
        if (Debug.isDebugBuild)
        {
            Debug.Log($"HANDLE MOVEMENT COMPLETED: hasLongTermGoal={hasLongTermGoal}, decisionInProgress={decisionInProgress}, motor.IsBusy={motorBusyStr3}, pendingDecision={pendingStr3}, lastDecisionDistance={lastDecisionDistStr}, longTermDistance={longTermDistance}, accumulatedForwardProgress={accumulatedForwardProgress}", this);
        }

        // If following a long-term heading, accumulate forward progress toward it.
        if (hasLongTermGoal && longTermGoalRadius > 0f)
        {
            // Try to parse the realized direction into a heading degrees.
            if (WandererDirectionExtensions.TryParse(result.realizedDirection, out WandererDirection realizedDir))
            {
                float realizedHeading = realizedDir.ToHeadingDegrees();

                float delta = Mathf.DeltaAngle(longTermHeadingDegrees, realizedHeading);
                float forward = Mathf.Cos(delta * Mathf.Deg2Rad) * result.actualDistance;

                if (forward > 0f)
                {
                    accumulatedForwardProgress += forward;
                }

                // If we've progressed far enough toward the long-term heading, request a new long-term goal.
                // But do not preempt any pendingDecision that is already waiting to run.
                // Evaluate combined re-request policies: distance fraction, mini-step count, short goal radius, or time.
                bool reachedDistanceFraction = false;

                if (longTermDistance > 0f && reRequestDistanceFraction > 0f)
                {
                    reachedDistanceFraction = accumulatedForwardProgress >= (longTermDistance * reRequestDistanceFraction);
                }

                bool reachedMiniCount = maxMiniSteps > 0 && miniStepsSinceLongTerm >= maxMiniSteps;

                bool reachedMaxTime = maxLongTermSeconds > 0f && (Time.time - longTermStartTime) >= maxLongTermSeconds;

                bool reachedGoalRadius = longTermGoalRadius > 0f && accumulatedForwardProgress >= longTermGoalRadius;
                if (reachedDistanceFraction || reachedMiniCount || reachedMaxTime || reachedGoalRadius)
                {
                    string reason = reachedDistanceFraction ? "distance-fraction" : (reachedMiniCount ? "mini-count" : (reachedMaxTime ? "time" : "goal-radius"));
                    Debug.Log($"Long-term re-request triggered by {reason}: progress={accumulatedForwardProgress:F1}, minis={miniStepsSinceLongTerm}, elapsed={(Time.time - longTermStartTime):F1}s", this);

                    if (pendingDecision != null)
                    {
                        // Defer the LLM request until the pending mini is consumed to avoid interrupting queued local movement.
                        reRequestQueued = true;
                        Debug.Log($"Long-term re-request queued because a pending mini exists.", this);
                    }
                    else
                    {
                        hasLongTermGoal = false;
                        accumulatedForwardProgress = 0f;
                        miniStepsSinceLongTerm = 0;
                        longTermStartTime = 0f;
                        StartLongTermGoal();
                        // Let the new long-term goal drive the next movement; do not fall back to local reuse.
                        return;
                    }
                }
            }
        }

        // If a long-term goal is active, immediately generate and try the next mini-decision.
        if (hasLongTermGoal)
        {
            WandererDecision nextMini = GenerateMiniDecision();

            // If Wanderer is not busy, attempt to start the mini movement now.
            if (!motor.IsBusy)
            {
                if (!motor.TryMove(nextMini))
                {
                    // Could not start the mini movement; start local retry loop.
                    StartLocalRetryLoop();
                }

                return;
            }
            else
            {
                // If motor is busy, queue the mini decision for when movement completes.
                pendingDecision = nextMini;
                return;
            }
        }

        // If a re-request was queued earlier (because a pending mini existed), and now there is no pending mini
        // and no decision in progress, trigger the long-term request.
        if (reRequestQueued && !decisionInProgress && pendingDecision == null)
        {
            reRequestQueued = false;
            hasLongTermGoal = false;
            accumulatedForwardProgress = 0f;
            miniStepsSinceLongTerm = 0;
            longTermStartTime = 0f;
            Debug.Log("Processing queued long-term re-request now that pending mini is cleared.", this);
            StartLongTermGoal();
            return;
        }

        if (localRetryRoutine != null)
        {
            StopCoroutine(localRetryRoutine);
            localRetryRoutine = null;
        }

        // Best case:
        // the next LLM decision is already waiting.
        if (pendingDecision != null)
        {
            WandererDecision nextDecision =
                pendingDecision;

            pendingDecision = null;

            if (!motor.TryMove(nextDecision))
            {
                StartLocalRetryLoop();
            }

            return;
        }

        // The LLM hasn't answered yet.
        // DO NOT let Wanderer freeze.
        //
        // Use the previous decision as a temporary local movement.
        if (!motor.TryMoveAnywhere(lastDecision))
        {
            StartLocalRetryLoop();
        }
    }
    private void HandleNextDecisionRequest()
    {
        string motorBusyStr4 = motor != null ? motor.IsBusy.ToString() : "null";
        string pendingStr4 = (pendingDecision != null).ToString();
        Debug.Log($"HandleNextDecisionRequest event: enabled={enabled}, decisionInProgress={decisionInProgress}, motor.IsBusy={motorBusyStr4}, hasLongTermGoal={hasLongTermGoal}, pendingDecision={pendingStr4}", this);

        if (!enabled ||
            decisionInProgress ||
            pendingDecision != null)
        {
            return;
        }

        // If a long-term goal is active, generate a local mini-decision instead of calling the LLM.
        if (hasLongTermGoal)
        {
            WandererDecision mini = GenerateMiniDecision();
            HandleDecision(mini);
            return;
        }

        decisionInProgress = true;

        WandererPerceptionSnapshot snapshot =
            perception.Scan();

        StartCoroutine(
            brain.RequestDecision(
                snapshot,
                HandleDecision,
                HandleDecisionFailure
            )
        );
    }

    /// <summary>
    /// Asks the LLM once for a long-term goal (coarse heading and mood),
    /// then begins generating local mini-decisions toward that heading.
    /// </summary>
    private void StartLongTermGoal()
    {
        Debug.Log($"StartLongTermGoal requested: enabled={enabled}, decisionInProgress={decisionInProgress}", this);
        if (!enabled || decisionInProgress)
        {
            return;
        }

        decisionInProgress = true;

        WandererPerceptionSnapshot snapshot = perception.Scan();

        StartCoroutine(
            brain.RequestDecision(
                snapshot,
                OnLongTermDecision,
                HandleDecisionFailure
            )
        );
    }

    private void OnLongTermDecision(WandererDecision decision)
    {
        // Mark that the LLM work is finished.
        decisionInProgress = false;

        if (decision == null)
        {
            HandleDecisionFailure("Long-term decision was null.");
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

        Debug.Log($"ON LONG-TERM DECISION: heading={longTermHeadingDegrees} deg, distance={longTermDistance} m, mood={longTermMood}", this);
        Debug.Log($"Long-term explanation: The LLM suggested heading {longTermHeadingDegrees}° for {longTermDistance:F1} m. The controller will generate mini-steps that bias toward this heading and preserve the LLM mood/thought until the next LLM decision.", this);

        // Show the long-term mood/thought immediately (do not let minis overwrite it).
        DisplayExpression(decision);

        hasLongTermGoal = true;
        // Reset counters/timers for the new long-term decision so re-request policies start fresh.
        miniStepsSinceLongTerm = 0;
        longTermStartTime = Time.time;

        // Immediately generate and start the first mini-decision toward the long-term heading.
        WandererDecision mini = GenerateMiniDecision();
        HandleDecision(mini);
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

        if (Debug.isDebugBuild)
        {
            Debug.Log($"GENERATE MINI: heading={heading:F1}°, offset={offset:F1}°, useDistance={useDistance:F2} m (longTermDistance={longTermDistance:F2}), maxMini={maxMiniDistance}", this);
            Debug.Log($"Mini explanation: This mini-step chooses heading {heading:F1}° which is {offset:F1}° offset from the long-term heading {longTermHeadingDegrees:F1}°. It travels {useDistance:F2} m to produce a short sway toward the long-term goal.", this);
        }

        // Count this mini for long-term re-request policy.
        // Keep this increment local to GenerateMiniDecision so the policy logic stays simple.
        miniStepsSinceLongTerm++;

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

    private IEnumerator RetryMovementUntilItStarts()
    {
        while (enabled &&
               !motor.IsBusy)
        {
            yield return new WaitForSeconds(
                localRetryDelay
            );

            if (motor.TryMoveAnywhere(lastDecision))
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
}
