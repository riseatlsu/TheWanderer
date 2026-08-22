using System.Collections;
using TMPro;
using UnityEngine;

/// <summary>
/// Event-driven conductor for the Wanderer lifecycle.
///
/// Loop:
/// 1. Sense terrain.
/// 2. Ask the LLM for one subjective decision.
/// 3. Ask WandererMotor to physically realize that intent.
/// 4. Wait until the sphere truly reaches a destination.
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

        RequestNextDecision();
    }

    private void RequestNextDecision()
    {
        if (!enabled ||
            decisionInProgress ||
            motor.IsBusy)
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

        DisplayExpression(decision);

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
