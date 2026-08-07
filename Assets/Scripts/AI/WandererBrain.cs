using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// High-level subjective brain for Wanderer.
///
/// The model does NOT choose XYZ coordinates and does NOT control NavMesh validity.
/// It receives a coarse description of the terrain and returns an expressive intent:
/// direction, distance, movement character, mood, and a short inner thought.
///
/// The brain also keeps a small memory of recently realized movements so Wanderer can
/// develop continuity instead of behaving like a stateless random direction generator.
/// </summary>
public class WandererBrain : MonoBehaviour
{
    private const string Endpoint =
        "https://api.openai.com/v1/responses";

    private const string ApiKeyEnvironmentVariable =
        "OPENAI_API_KEY";


    [Header("OpenAI")]

    [SerializeField]
    [Tooltip("OpenAI model used for Wanderer's high-level decisions.")]
    private string model = "gpt-5.6-luna";

    [SerializeField, TextArea(2, 4)]
    [Tooltip("Optional local-development API key override. Prefer OPENAI_API_KEY.")]
    private string apiKeyOverride = "";

    [SerializeField]
    [Tooltip("Print the raw API response for debugging.")]
    private bool printRawResponse = false;


    [Header("Decision Envelope")]

    [SerializeField, Min(0.1f)]
    private float minimumRequestedDistance = 5f;

    [SerializeField, Min(0.1f)]
    private float maximumRequestedDistance = 3000f;

    [SerializeField, Min(0.1f)]
    private float minimumSpeed = 5f;

    [SerializeField, Min(0.1f)]
    private float maximumSpeed = 300f;

    [SerializeField, Min(0.1f)]
    private float minimumAcceleration = 10f;

    [SerializeField, Min(0.1f)]
    private float maximumAcceleration = 600f;

    [SerializeField, Min(1f)]
    private float minimumAngularSpeed = 60f;

    [SerializeField, Min(1f)]
    private float maximumAngularSpeed = 500f;

    [SerializeField, Min(0f)]
    private float minimumStoppingDistance = 0.5f;

    [SerializeField, Min(0f)]
    private float maximumStoppingDistance = 20f;

    [SerializeField, Min(0f)]
    private float maximumWaitSeconds = 8f;


    [Header("Personality")]

    [SerializeField, TextArea(8, 20)]
    private string personalityPrompt =
        "You are Wanderer, a small autonomous sphere exploring an unfamiliar planetary landscape. " +
        "You are not a conventional safety optimizer and you are not trying to efficiently reach a goal. " +
        "You have curiosity, hesitation, attraction, impatience, confidence, uncertainty, and changing impulses. " +
        "Sometimes you choose distant horizons; sometimes you become interested in something nearby. " +
        "Sometimes steep or difficult terrain attracts you precisely because it is difficult, while at other times " +
        "you avoid it. Let recent experiences influence your next choice, but do not become repetitive. " +
        "Treat the terrain observations as sensations rather than commands. " +
        "Unity will handle physical feasibility, so express what you WANT to do rather than trying to solve NavMesh geometry. " +
        "Your thought should sound like the private thought of a living wandering entity, not a robot status report. " +
        "Never mention Unity, NavMesh, JSON, coordinates, pathfinding, APIs, or language models in the thought.";


    [Header("Short-Term Memory")]

    [SerializeField, Range(0, 12)]
    [Tooltip("Number of recent realized movements included in the next decision.")]
    private int rememberedMovements = 6;


    private readonly Queue<string> recentExperiences =
        new Queue<string>();


    /// <summary>
    /// Requests one structured high-level navigation decision.
    /// </summary>
    public IEnumerator RequestDecision(
        WandererPerceptionSnapshot perception,
        Action<WandererDecision> onSuccess,
        Action<string> onFailure)
    {
        string apiKey = ResolveApiKey();

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            onFailure?.Invoke(
                $"No OpenAI API key found. Set {ApiKeyEnvironmentVariable} " +
                "or use Api Key Override for local development."
            );

            yield break;
        }

        JObject requestBody = BuildRequest(perception);
        string requestJson =
            requestBody.ToString(Formatting.None);

        using (UnityWebRequest request =
               new UnityWebRequest(
                   Endpoint,
                   UnityWebRequest.kHttpVerbPOST))
        {
            byte[] body =
                Encoding.UTF8.GetBytes(requestJson);

            request.uploadHandler =
                new UploadHandlerRaw(body);

            request.downloadHandler =
                new DownloadHandlerBuffer();

            request.SetRequestHeader(
                "Content-Type",
                "application/json"
            );

            request.SetRequestHeader(
                "Authorization",
                $"Bearer {apiKey}"
            );

            yield return request.SendWebRequest();

            if (request.result !=
                UnityWebRequest.Result.Success)
            {
                onFailure?.Invoke(
                    $"OpenAI request failed. HTTP {request.responseCode}: " +
                    $"{request.error}\n{request.downloadHandler.text}"
                );

                yield break;
            }

            string raw =
                request.downloadHandler.text;

            if (printRawResponse)
            {
                Debug.Log(
                    $"WANDERER RAW OPENAI RESPONSE\n{raw}",
                    this
                );
            }

            try
            {
                string outputText =
                    ExtractOutputText(raw);

                if (string.IsNullOrWhiteSpace(outputText))
                {
                    throw new InvalidOperationException(
                        "The response contained no output_text."
                    );
                }

                WandererDecision decision =
                    JsonConvert.DeserializeObject<WandererDecision>(
                        outputText
                    );

                if (decision == null)
                {
                    throw new InvalidOperationException(
                        "The structured decision could not be deserialized."
                    );
                }

                ClampDecision(decision);
                onSuccess?.Invoke(decision);
            }
            catch (Exception exception)
            {
                onFailure?.Invoke(
                    $"Could not parse Wanderer's decision: {exception.Message}"
                );
            }
        }
    }

    /// <summary>
    /// Records what Unity actually accomplished. This becomes part of Wanderer's
    /// short-term autobiographical context on the next LLM request.
    /// </summary>
    public void RememberMovement(
        WandererMovementResult result)
    {
        if (result == null ||
            rememberedMovements <= 0)
        {
            return;
        }

        string experience =
            $"Wanted {result.desiredDirection} for " +
            $"{result.desiredDistance:F0} m; actually traveled " +
            $"{result.actualDistance:F0} m toward " +
            $"{result.realizedDirection}. " +
            $"Movement realization: {result.fallbackStage}.";

        recentExperiences.Enqueue(experience);

        while (recentExperiences.Count >
               rememberedMovements)
        {
            recentExperiences.Dequeue();
        }
    }

    private JObject BuildRequest(
        WandererPerceptionSnapshot perception)
    {
        string userPrompt =
            BuildPerceptionPrompt(perception);

        float schemaMaximumDistance =
            Mathf.Max(
                minimumRequestedDistance,
                maximumRequestedDistance
            );

        JObject schema =
            new JObject
            {
                ["type"] = "object",

                ["properties"] =
                    new JObject
                    {
                        ["direction"] =
                            new JObject
                            {
                                ["type"] = "string",
                                ["enum"] = new JArray(
                                    "north",
                                    "northeast",
                                    "east",
                                    "southeast",
                                    "south",
                                    "southwest",
                                    "west",
                                    "northwest"
                                )
                            },

                        ["distance"] =
                            new JObject
                            {
                                ["type"] = "number",
                                ["minimum"] =
                                    Mathf.Min(
                                        minimumRequestedDistance,
                                        schemaMaximumDistance
                                    ),
                                ["maximum"] =
                                    schemaMaximumDistance
                            },

                        ["speed"] =
                            new JObject
                            {
                                ["type"] = "number",
                                ["minimum"] =
                                    Mathf.Min(
                                        minimumSpeed,
                                        maximumSpeed
                                    ),
                                ["maximum"] =
                                    Mathf.Max(
                                        minimumSpeed,
                                        maximumSpeed
                                    )
                            },

                        ["acceleration"] =
                            new JObject
                            {
                                ["type"] = "number",
                                ["minimum"] =
                                    Mathf.Min(
                                        minimumAcceleration,
                                        maximumAcceleration
                                    ),
                                ["maximum"] =
                                    Mathf.Max(
                                        minimumAcceleration,
                                        maximumAcceleration
                                    )
                            },

                        ["angularSpeed"] =
                            new JObject
                            {
                                ["type"] = "number",
                                ["minimum"] =
                                    Mathf.Min(
                                        minimumAngularSpeed,
                                        maximumAngularSpeed
                                    ),
                                ["maximum"] =
                                    Mathf.Max(
                                        minimumAngularSpeed,
                                        maximumAngularSpeed
                                    )
                            },

                        ["stoppingDistance"] =
                            new JObject
                            {
                                ["type"] = "number",
                                ["minimum"] =
                                    Mathf.Min(
                                        minimumStoppingDistance,
                                        maximumStoppingDistance
                                    ),
                                ["maximum"] =
                                    Mathf.Max(
                                        minimumStoppingDistance,
                                        maximumStoppingDistance
                                    )
                            },

                        ["waitSeconds"] =
                            new JObject
                            {
                                ["type"] = "number",
                                ["minimum"] = 0,
                                ["maximum"] =
                                    Mathf.Max(
                                        0f,
                                        maximumWaitSeconds
                                    )
                            },

                        ["mood"] =
                            new JObject
                            {
                                ["type"] = "string"
                            },

                        ["thought"] =
                            new JObject
                            {
                                ["type"] = "string"
                            }
                    },

                ["required"] =
                    new JArray(
                        "direction",
                        "distance",
                        "speed",
                        "acceleration",
                        "angularSpeed",
                        "stoppingDistance",
                        "waitSeconds",
                        "mood",
                        "thought"
                    ),

                ["additionalProperties"] = false
            };

        return new JObject
        {
            ["model"] = model,

            ["reasoning"] =
                new JObject
                {
                    ["effort"] = "low"
                },

            ["input"] =
                new JArray
                {
                    new JObject
                    {
                        ["role"] = "developer",
                        ["content"] =
                            new JArray
                            {
                                new JObject
                                {
                                    ["type"] = "input_text",
                                    ["text"] =
                                        personalityPrompt
                                }
                            }
                    },

                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] =
                            new JArray
                            {
                                new JObject
                                {
                                    ["type"] = "input_text",
                                    ["text"] =
                                        userPrompt
                                }
                            }
                    }
                },

            ["text"] =
                new JObject
                {
                    ["format"] =
                        new JObject
                        {
                            ["type"] = "json_schema",
                            ["name"] =
                                "wanderer_navigation_decision",
                            ["strict"] = true,
                            ["schema"] = schema
                        }
                }
        };
    }

    private string BuildPerceptionPrompt(
        WandererPerceptionSnapshot perception)
    {
        StringBuilder builder =
            new StringBuilder();

        builder.AppendLine(
            "Choose Wanderer's next impulse."
        );

        builder.AppendLine();
        builder.AppendLine(
            "Current terrain sensations:"
        );

        builder.AppendLine(
            JsonConvert.SerializeObject(
                perception,
                Formatting.Indented
            )
        );

        builder.AppendLine();
        builder.AppendLine(
            "Recent experiences:"
        );

        if (recentExperiences.Count == 0)
        {
            builder.AppendLine(
                "- None yet. This is the beginning of the journey."
            );
        }
        else
        {
            foreach (string experience
                     in recentExperiences)
            {
                builder.Append("- ");
                builder.AppendLine(experience);
            }
        }

        builder.AppendLine();
        builder.AppendLine(
            "Prefer directions described as reachable when they fit your impulse, " +
            "but do not behave like a deterministic safest-path planner. " +
            "Vary distance, pace, waiting, mood, and thought naturally."
        );

        return builder.ToString();
    }

    private static string ExtractOutputText(
        string rawResponse)
    {
        JObject root =
            JObject.Parse(rawResponse);

        JArray output =
            root["output"] as JArray;

        if (output == null)
        {
            return null;
        }

        foreach (JToken item in output)
        {
            JArray content =
                item["content"] as JArray;

            if (content == null)
            {
                continue;
            }

            foreach (JToken contentItem
                     in content)
            {
                if ((string)contentItem["type"] ==
                    "output_text")
                {
                    return (string)contentItem["text"];
                }
            }
        }

        return null;
    }

    private void ClampDecision(
        WandererDecision decision)
    {
        decision.distance = Mathf.Clamp(
            decision.distance,
            Mathf.Min(
                minimumRequestedDistance,
                maximumRequestedDistance
            ),
            Mathf.Max(
                minimumRequestedDistance,
                maximumRequestedDistance
            )
        );

        decision.speed = Mathf.Clamp(
            decision.speed,
            Mathf.Min(minimumSpeed, maximumSpeed),
            Mathf.Max(minimumSpeed, maximumSpeed)
        );

        decision.acceleration = Mathf.Clamp(
            decision.acceleration,
            Mathf.Min(
                minimumAcceleration,
                maximumAcceleration
            ),
            Mathf.Max(
                minimumAcceleration,
                maximumAcceleration
            )
        );

        decision.angularSpeed = Mathf.Clamp(
            decision.angularSpeed,
            Mathf.Min(
                minimumAngularSpeed,
                maximumAngularSpeed
            ),
            Mathf.Max(
                minimumAngularSpeed,
                maximumAngularSpeed
            )
        );

        decision.stoppingDistance = Mathf.Clamp(
            decision.stoppingDistance,
            Mathf.Min(
                minimumStoppingDistance,
                maximumStoppingDistance
            ),
            Mathf.Max(
                minimumStoppingDistance,
                maximumStoppingDistance
            )
        );

        decision.waitSeconds = Mathf.Clamp(
            decision.waitSeconds,
            0f,
            Mathf.Max(0f, maximumWaitSeconds)
        );

        if (string.IsNullOrWhiteSpace(decision.mood))
        {
            decision.mood = "curious";
        }

        if (string.IsNullOrWhiteSpace(decision.thought))
        {
            decision.thought =
                "Something ahead is pulling at me.";
        }
    }

    private string ResolveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(apiKeyOverride))
        {
            return apiKeyOverride.Trim();
        }

        return Environment.GetEnvironmentVariable(
            ApiKeyEnvironmentVariable
        );
    }
}
