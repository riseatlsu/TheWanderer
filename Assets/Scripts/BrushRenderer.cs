// Handles all line renderer generation


using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class BrushRenderer : MonoBehaviour
{
    [Header("Data References")]
    [SerializeField] DistanceReferencer dReferencer;
    [SerializeField] PhysicsReferencer pReferencer;
    [SerializeField] Transform DestroyAfter;

    [Header("Segment Settings")]
    [SerializeField] float segmentDistance = 0.1f;
    [SerializeField] float widthGap = 0.1f;
    [SerializeField] float angleChange = 90f;
    [SerializeField] int overlap = 1;
    [SerializeField] int totalPoints = 6;

    [Header("Color Transition")]
    [SerializeField, Min(1)] private int colorTransitionSegments = 20;

    [Header("Branch Segment Settings")]
    [SerializeField] int enterSegments = 20;
    [SerializeField] int returnSegments = 20;
    [SerializeField, Min(0f)] float branchCooldown = 3f;

    [Header("Material Settings")]
    [SerializeField] Material lineMaterial;
    [SerializeField] float ditherValueMax = 2f;
    [SerializeField] float ditherValueMin = 1.1f;
    [SerializeField] float ditherUpdate = 0.1f;

    private Vector3 prevEnd;
    private Vector3 prevBranchEnd;
    private Vector3 prevSubBranchEnd;

    private List<LineRenderer> segments;
    private List<LineRenderer> branchSegments;
    private List<LineRenderer> subBranchSegments;


    private Vector3 pastDirection;

    private float branchSide = 1f;
    private float branchAmount = 0f;
    private float subBranchAmount = 0f;
    private bool branchCycleActive;
    private int branchCycleProgress;
    private float nextBranchTime;
    private float accumulatedTurnAngle;


    private bool isRendering = false;
    private bool isInitialized;

    private float widthDistance;

    private float ditherValue = 2f;

    private bool fadedIn = false;

    
    private Color color;
    private Color colorRequest;
    private bool hasPreviousColor;
    private Color transitionStartColor;
    private Color transitionTargetColor;
    private int colorTransitionIndex;


    private void Start()
    {
        segments = new List<LineRenderer>();
        branchSegments = new List<LineRenderer>();
        subBranchSegments = new List<LineRenderer>();

        pastDirection = Vector3.zero;

        widthDistance = pReferencer.GetWidthRange() * widthGap;

        colorRequest = pReferencer.GetColor();

        branchCycleActive = false;
        branchCycleProgress = 0;
        nextBranchTime = 0f;
        accumulatedTurnAngle = 0f;
        isInitialized = false;
    }
    void Update()
    {
        SetRendering();
        SetColorRequest();

        if (!isRendering)
        {
            if (isInitialized)
            {
                isInitialized = false;
            }
            return;
        }

        if (!isInitialized)                                 // Delays initialization until first update
        {
            prevEnd = pReferencer.GetBrushPosition();
            prevBranchEnd = prevEnd;
            prevSubBranchEnd = prevEnd;
            pastDirection = Vector3.zero;
            branchAmount = 0f;
            subBranchAmount = 0f;
            branchCycleProgress = 0;
            branchCycleActive = false;
            accumulatedTurnAngle = 0f;
            isInitialized = true;
            return;
        }

        if (WidthChanged() || IsDistant())
        {
            Vector3 nextDirection = pReferencer.GetBrushPosition() - prevEnd;
            bool branchStarted = TryStartBranch(nextDirection);

            float prevBranchAmount = branchAmount;
            float prevSubBranchAmount = subBranchAmount;

            MakeSegment();

            if (branchStarted || branchCycleActive)
            {
                UpdateBranchAmount();
            }

            if (branchAmount > 0f || prevBranchAmount > 0f)
            {
                MakeBranchSegment(1);
            }

            if (subBranchAmount > 0f || prevSubBranchAmount > 0f)
            {
                MakeBranchSegment(2);
            }

            if (!branchCycleActive && branchAmount <= 0f)
            {
                prevBranchEnd = prevEnd;
            }

            if (!branchCycleActive && subBranchAmount <= 0f)
            {
                prevSubBranchEnd = prevEnd;
            }
        }
    }

    public void SetRendering()      // and also create fade in and fade out effect
    {
        if (Keyboard.current.pKey.isPressed)
        {
            isRendering = true;
            if (!fadedIn)
            {
                ditherValue -= (ditherUpdate * 2);
                if (ditherValue < 1.3f)
                {
                    fadedIn = true;
                }
            }
        }
        else if (isRendering)
        {
            ditherValue += (ditherUpdate * 2);
            if (ditherValue > ditherValueMax)
            {
                ditherValue = ditherValueMax;

                isRendering = false;
                fadedIn = false;

            }
        }
    }
    
    public bool IsDistant()
    {
        if (segments.Count == 0)
        {
            return dReferencer.GetHorizontalDistance(prevEnd, pReferencer.GetBrushPosition()) > segmentDistance;
        }

        if (segments.Count >= 1)
        {
            if (dReferencer.GetHorizontalDistance(segments[segments.Count - 1].GetPosition(totalPoints - 1), pReferencer.GetBrushPosition()) > segmentDistance)
            {
                return true;
            }
            else
            {
                return false;
            }
        }
        else
        {
            return false;
        }
    }


    public bool WidthChanged()
    {
        if (segments.Count < 1)
        {
            return false;
        }

        float previousWidth = segments[segments.Count - 1].endWidth;
        float currentWidth = pReferencer.GetWidth();

        return Mathf.Abs(previousWidth - currentWidth) > widthDistance;
    }

    private bool TryStartBranch(Vector3 currentDirection)
    {
        currentDirection.y = 0f;
        Vector3 previousDirection = pastDirection;
        previousDirection.y = 0f;

        if (segments.Count == 0 ||
            currentDirection.sqrMagnitude < 0.0001f ||
            previousDirection.sqrMagnitude < 0.0001f)
        {
            return false;
        }

        if (branchCycleActive || Time.time < nextBranchTime)
        {
            accumulatedTurnAngle = 0f;
            return false;
        }

        accumulatedTurnAngle += Vector3.SignedAngle(
            previousDirection.normalized,
            currentDirection.normalized,
            Vector3.up);

        if (Mathf.Abs(accumulatedTurnAngle) < angleChange)
        {
            return false;
        }

        branchSide = Mathf.Sign(accumulatedTurnAngle);
        accumulatedTurnAngle = 0f;
        branchAmount = 0f;
        subBranchAmount = 0f;
        branchCycleProgress = 0;
        branchCycleActive = true;
        nextBranchTime = Time.time + branchCooldown;
        return true;
    }

    public void MakeSegment()
    {
        LineRenderer segment = new GameObject().AddComponent<LineRenderer>();
        segment.useWorldSpace = true;
        segment.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        segment.receiveShadows = false;
        segment.transform.SetParent(DestroyAfter);

        SetPos(segment);
        SetWidth(segment);
        SetColor(segment);


        segments.Add(segment);
    }

    public void SetPos(LineRenderer seg)
    {
        seg.positionCount = totalPoints;
        Vector3[] positions = new Vector3[totalPoints];
        positions = pReferencer.GetPos(prevEnd, pReferencer.GetBrushPosition(), positions);
        
        for (int i = 0; i < totalPoints; i++)
        {
            seg.SetPosition(i, positions[i]);
        }

        pastDirection = positions[totalPoints - overlap] - prevEnd;
        prevEnd = positions[totalPoints - overlap];     // next start position is the end of this segment
        

    }

    public void SetWidth(LineRenderer seg)
    {
        if (segments.Count > 0)
        {
            seg.startWidth = segments[segments.Count - 1].endWidth;
            seg.endWidth = pReferencer.GetWidth();
        }
        else
        {
            seg.startWidth = pReferencer.GetWidth();
            seg.endWidth = pReferencer.GetWidth();
        }
    }

    public void SetColor(LineRenderer seg)
    {
        Material newMaterial = new Material(lineMaterial);

        if (Keyboard.current.kKey.isPressed)
        {
            ditherValue += ditherUpdate;
        }
        else if (Keyboard.current.lKey.isPressed)
        {
            fadedIn = true;
            ditherValue = Mathf.Max(ditherValueMin, ditherValue - ditherUpdate);
        }

        newMaterial.SetFloat("_DitherValue", ditherValue);
        seg.material = newMaterial;

        Color requestedColor = colorRequest;
        requestedColor.a = 1f;
        int transitionLength = Mathf.Max(1, colorTransitionSegments);

        if (!hasPreviousColor)
        {
            color = requestedColor;
            transitionStartColor = requestedColor;
            transitionTargetColor = requestedColor;
            colorTransitionIndex = transitionLength;
            hasPreviousColor = true;
        }
        else if (requestedColor != transitionTargetColor)
        {
            transitionStartColor = color;
            transitionTargetColor = requestedColor;
            colorTransitionIndex = 0;
        }

        if (colorTransitionIndex < transitionLength)
        {
            float startProgress = colorTransitionIndex / (float)transitionLength;
            float endProgress = (colorTransitionIndex + 1f) / transitionLength;
            seg.startColor = Color.Lerp(transitionStartColor, transitionTargetColor, startProgress);
            seg.endColor = Color.Lerp(transitionStartColor, transitionTargetColor, endProgress);
            color = seg.endColor;
            colorTransitionIndex++;
        }
        else
        {
            color = transitionTargetColor;
            seg.startColor = color;
            seg.endColor = color;
        }
        
        
    }




    public void SetColorRequest()
    {
        colorRequest = pReferencer.GetColor();
    }

    private void UpdateBranchAmount()
    {
        int growSegments = Mathf.Max(1, enterSegments);
        int shrinkSegments = Mathf.Max(1, returnSegments);
        branchCycleProgress++;

        if (branchCycleProgress <= growSegments)
        {
            branchAmount = branchCycleProgress / (float)growSegments;
        }
        else if (branchCycleProgress <= growSegments + shrinkSegments)
        {
            int returnProgress = branchCycleProgress - growSegments;
            branchAmount = 1f - returnProgress / (float)shrinkSegments;

            if (branchCycleProgress >= growSegments + shrinkSegments)
            {
                branchCycleActive = false;
            }
        }
        else
        {
            branchAmount = 0f;
            branchCycleActive = false;
        }

        subBranchAmount = branchAmount;

        if (!branchCycleActive && branchCycleProgress >= growSegments + shrinkSegments)
        {
            branchAmount = 0f;
            subBranchAmount = 0f;
        }
    }


    public void MakeBranchSegment(int branchLevel)
    {
        LineRenderer segment = new GameObject().AddComponent<LineRenderer>();
        segment.useWorldSpace = true;
        segment.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        segment.receiveShadows = false;
        segment.transform.SetParent(DestroyAfter);

        SetBranchPos(segment, branchLevel);
        SetBranchWidth(segment, branchLevel);
        SetColor(segment);

        if (branchLevel == 1)
        {
            branchSegments.Add(segment);
        }
        
        if (branchLevel == 2) 
        {
            subBranchSegments.Add(segment);
        }
    }


    public void SetBranchPos(LineRenderer seg, int branchLevel)
    {
        seg.positionCount = totalPoints;
        Vector3[] positions = new Vector3[totalPoints];

        if (branchLevel == 1)
        { 
            if (prevBranchEnd == prevEnd)
            {
                positions = pReferencer.GetBranchPos(prevEnd, positions, segments[segments.Count - 1], branchAmount, branchSide, branchLevel);
            }
            else
            {
                positions = pReferencer.GetBranchPos(prevBranchEnd, positions, segments[segments.Count - 1], branchAmount, branchSide, branchLevel);
            }

            for (int i = 0; i < totalPoints; i++)
            {
                seg.SetPosition(i, positions[i]);
            }

            prevBranchEnd = positions[totalPoints - overlap];
        }
        if (branchLevel == 2)
        {
            if (prevSubBranchEnd == prevEnd)
            {
                positions = pReferencer.GetBranchPos(prevEnd, positions, segments[segments.Count - 1], subBranchAmount, branchSide, branchLevel);
            }
            else
            {
                positions = pReferencer.GetBranchPos(prevSubBranchEnd, positions, segments[segments.Count - 1], subBranchAmount, branchSide, branchLevel);
            }

            for (int i = 0; i < totalPoints; i++)
            {
                seg.SetPosition(i, positions[i]);
            }

            prevSubBranchEnd = positions[totalPoints - overlap];
        }
    }

    public void SetBranchWidth(LineRenderer seg, int level)
    {
        if (branchSegments.Count == 0)
        {
            seg.startWidth = segments[segments.Count - 1].endWidth;
        }
        else
        {
            seg.startWidth = branchSegments[branchSegments.Count - 1].endWidth / level;
        }

        seg.endWidth = pReferencer.GetBranchWidth() / level;
    }


}


