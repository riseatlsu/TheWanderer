// Calculator for all physical attributes of the line renderers


using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

public class PhysicsReferencer : MonoBehaviour
{
    [Header("Reference Data")]
    [SerializeField] private DistanceReferencer dReference;
    [SerializeField] private PlayerMovement mReference;

    [Header("Width Settings")]
    [SerializeField] private float widthMin = 15f;
    [SerializeField] private float widthMax = 25f;
    [SerializeField] private float widthUpdate = 0.01f;

    [Header("Color Settings")]
    [SerializeField] private Color low = Color.blue;
    [SerializeField] private Color medium = Color.yellow;
    [SerializeField] private Color high = Color.red;

    [Header("Movement Settings")]
    [SerializeField] private float curveStrength = 0.25f;
    [SerializeField] private float branchWidthDivider = 2f;
    [SerializeField] private float branchScaler = 0.15f;

    private float width = 20f;
    private float branchWidth = 10f;
    private Color color;
    private Vector3 brushPos;




    public float GetWidth()
    {

        if (Keyboard.current.uKey.isPressed)
        {
            width += widthUpdate;

            if (width > widthMax)
            {
                width = widthMax;
            }
        }
        else if (Keyboard.current.iKey.isPressed)
        {
            width -= widthUpdate;

            if (width < widthMin)
            {
                width = widthMin;
            }
        }
        return width;
    }

    public float GetBranchWidth()
    {
        branchWidth = GetWidth() / branchWidthDivider;
        return branchWidth;
    }

    public float GetWidthRange()
    {
        return widthMax - widthMin;
    }


    public Color GetColor()
    {
        
        float distance = dReference.GetGroundHeight();
        distance = Mathf.InverseLerp(50f, 750f, distance);

        if (distance < 0.5f)
        {
            float t = Mathf.InverseLerp(0f, 0.5f, distance);
            color = Color.Lerp(low, medium, t);
        }
        else
        {
            float t = Mathf.InverseLerp(0.5f, 1f, distance);
            color = Color.Lerp(medium, high, t);
        }
        
        return color;
    }

    public Vector3[] GetPos(Vector3 start, Vector3 end, Vector3[] positions)
    {
        positions[0] = start;
        positions[positions.Length - 1] = end;

        Vector3 dir = end - start;
        float dist = dir.magnitude;
        dir.Normalize();

        Vector3 up = Vector3.up;
        if (Mathf.Abs(Vector3.Dot(dir, up)) > 0.9f)
            up = Vector3.right;

        Vector3 normal = Vector3.Cross(dir, up).normalized;

        float curvature = dist * curveStrength;

        for (int i = 1; i < positions.Length - 1; i++)
        {
            float t = i / (float)(positions.Length - 1);

            Vector3 linear = Vector3.Lerp(start, end, t);

            float arch = Mathf.Sin(Mathf.PI * t);

            positions[i] = linear + normal * arch * curvature;
        }
        return positions;
    }

    public Vector3[] GetBranchPos(Vector3 start, Vector3[] positions, LineRenderer mainSegment, float branchAmount, float branchSide, int branchLevel)
    {
        float newScaler = branchScaler;
        
        if (branchLevel == 2)
        {
            newScaler *= 2;
        }
        
        positions[0] = start;

        float enterBranchedAmount = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(branchAmount));
        Vector3 mainStart = mainSegment.GetPosition(0);
        Vector3 mainEnd = mainSegment.GetPosition(mainSegment.positionCount - 1);

        Vector3 mainDirection = mainEnd - mainStart;
        mainDirection.y = 0f;

        if (mainDirection.sqrMagnitude < 0.0001f)
        {
            mainDirection = Vector3.forward;
        }

        mainDirection.Normalize();

        Vector3 sideDirection = Vector3.Cross(Vector3.up, mainDirection).normalized;

        for (int i = 1; i < positions.Length; i++)
        {
            float t = i / (float)(positions.Length - 1);

            Vector3 mainPoint = mainSegment.GetPosition(i);
            Vector3 branchTarget = mainPoint + sideDirection * newScaler * enterBranchedAmount * branchSide;

            Vector3 branchPoint = Vector3.Lerp(start, branchTarget, t);
            if (branchPoint.y < dReference.GetTerrainHeightAt(branchPoint) + 0.1f)
            {
                branchPoint.y = dReference.GetTerrainHeightAt(branchPoint) + 0.1f;
            }

            positions[i] = branchPoint;
        }

        return positions;
    }


    public Vector3 GetBrushPosition()
    {
        brushPos.x = dReference.getPlayerPosition().x;
        brushPos.y = dReference.GetGroundHeight() + 0.01f;
        brushPos.z = dReference.getPlayerPosition().z;

        return brushPos;
    }

    public float GetPlayerSpeed()
    {
        return mReference.GetCurrentSpeed();
    }
}
