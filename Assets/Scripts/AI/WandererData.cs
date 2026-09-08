using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Serializable data structures shared by the Wanderer components.
/// </summary>

[Serializable]
public class WandererDirectionSense
{
    public string direction;
    public float headingDegrees;
    public bool reachable;

    public float requestedDistance;
    public float actualDistance;
    public float pathLength;

    public float elevationChange;
    public float averageSlope;
    public float maximumSlope;
}

[Serializable]
public class WandererPerceptionSnapshot
{
    public float scanDistance;
    public List<WandererDirectionSense> directions = new List<WandererDirectionSense>();
}

[Serializable]
public class WandererDecision
{
    public string direction;                
    public float headingDegrees = -1f;
    public float distance;

    public float speed;
    public float acceleration;
    public float angularSpeed;
    public float stoppingDistance;
    public float waitSeconds;

    public string mood;
    public string thought;
}

/// <summary>
/// Describes what Unity physically realized from a high-level Wanderer decision.
/// The LLM can remember these outcomes on later decisions.
/// </summary>
[Serializable]
public class WandererMovementResult
{
    public string desiredDirection;
    public string realizedDirection;

    public float desiredDistance;
    public float actualDistance;
    public float pathLength;

    public string fallbackStage;

    // Kept for Unity debugging; this position is not sent to the LLM.
    public Vector3 destination;
}
