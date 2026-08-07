using UnityEngine;

/// <summary>
/// Eight coarse compass directions used by Wanderer's perception and high-level decisions.
///
/// World convention:
/// North = +Z
/// East  = +X
/// </summary>
public enum WandererDirection
{
    North = 0,
    Northeast = 1,
    East = 2,
    Southeast = 3,
    South = 4,
    Southwest = 5,
    West = 6,
    Northwest = 7
}

/// <summary>
/// Helper methods for converting between compass directions and Unity world vectors.
/// </summary>
public static class WandererDirectionExtensions
{
    public static Vector3 ToWorldVector(this WandererDirection direction)
    {
        switch (direction)
        {
            case WandererDirection.North:
                return Vector3.forward;

            case WandererDirection.Northeast:
                return new Vector3(1f, 0f, 1f).normalized;

            case WandererDirection.East:
                return Vector3.right;

            case WandererDirection.Southeast:
                return new Vector3(1f, 0f, -1f).normalized;

            case WandererDirection.South:
                return Vector3.back;

            case WandererDirection.Southwest:
                return new Vector3(-1f, 0f, -1f).normalized;

            case WandererDirection.West:
                return Vector3.left;

            case WandererDirection.Northwest:
                return new Vector3(-1f, 0f, 1f).normalized;

            default:
                return Vector3.forward;
        }
    }

    public static float ToHeadingDegrees(this WandererDirection direction)
    {
        return ((int)direction) * 45f;
    }

    public static bool TryParse(string value, out WandererDirection direction)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            direction = WandererDirection.North;
            return false;
        }

        string normalized = value
            .Trim()
            .Replace("_", "")
            .Replace("-", "")
            .Replace(" ", "")
            .ToLowerInvariant();

        switch (normalized)
        {
            case "north":
                direction = WandererDirection.North;
                return true;

            case "northeast":
                direction = WandererDirection.Northeast;
                return true;

            case "east":
                direction = WandererDirection.East;
                return true;

            case "southeast":
                direction = WandererDirection.Southeast;
                return true;

            case "south":
                direction = WandererDirection.South;
                return true;

            case "southwest":
                direction = WandererDirection.Southwest;
                return true;

            case "west":
                direction = WandererDirection.West;
                return true;

            case "northwest":
                direction = WandererDirection.Northwest;
                return true;

            default:
                direction = WandererDirection.North;
                return false;
        }
    }

    /// <summary>
    /// Returns compass headings in angularly expanding order around the preferred heading.
    /// Example for North: N, NW, NE, W, E, SW, SE, S.
    /// </summary>
    public static WandererDirection[] GetFallbackOrder(this WandererDirection preferred)
    {
        int center = (int)preferred;

        return new[]
        {
            FromIndex(center),
            FromIndex(center - 1),
            FromIndex(center + 1),
            FromIndex(center - 2),
            FromIndex(center + 2),
            FromIndex(center - 3),
            FromIndex(center + 3),
            FromIndex(center + 4)
        };
    }

    public static WandererDirection ClosestToVector(Vector3 vector)
    {
        Vector3 planar = new Vector3(vector.x, 0f, vector.z);

        if (planar.sqrMagnitude < 0.0001f)
        {
            return WandererDirection.North;
        }

        planar.Normalize();

        WandererDirection best = WandererDirection.North;
        float bestDot = float.NegativeInfinity;

        for (int i = 0; i < 8; i++)
        {
            WandererDirection candidate = (WandererDirection)i;
            float dot = Vector3.Dot(planar, candidate.ToWorldVector());

            if (dot > bestDot)
            {
                bestDot = dot;
                best = candidate;
            }
        }

        return best;
    }

    private static WandererDirection FromIndex(int index)
    {
        index %= 8;

        if (index < 0)
        {
            index += 8;
        }

        return (WandererDirection)index;
    }
}
