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

    [Header("Branch Segment Settings")]
    [SerializeField] int enterSegments = 20;
    [SerializeField] int returnSegments = 20;
    [SerializeField] float upTime = 3f;
    [SerializeField] float downTime = 3f;

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

    private bool isBranching;
    private float branchAmount = 0f;

    private bool isSubBranching;
    private float subBranchAmount = 0f;


    private bool isRendering = false;
    private bool isInitialized;

    private float widthDistance;

    private float ditherValue = 2f;

    private float downtime = 0f;
    private float uptime = 0f;
    private bool branchSideCalculated = false;
    private bool fadedIn = false;
    private bool canChangeColor = false;
    private Color color;


    private void Start()
    {
        segments = new List<LineRenderer>();
        branchSegments = new List<LineRenderer>();
        subBranchSegments = new List<LineRenderer>();

        pastDirection = Vector3.zero;

        widthDistance = pReferencer.GetWidthRange() * widthGap;

        color = pReferencer.GetColor();

        isBranching = false;
        isSubBranching = false;
        isInitialized = false;
    }
    void Update()
    {
        SetRendering();


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
            canChangeColor = true;
            isInitialized = true;
            return;
        }

        SetBranching();
        SetSubBranching();

        if (WidthChanged() || IsDistant())
        {
            MakeSegment();

            float prevBranchAmount = branchAmount;
            float prevSubBranchAmount = subBranchAmount;
            UpdateBranchAmount();
            UpdateSubBranchAmount();

            if (branchAmount > 0f || prevBranchAmount > 0f)
            {
                if (!isSubBranching || !isBranching)
                {
                    MakeBranchSegment(1);
                }
            }

            if (subBranchAmount > 0f || prevSubBranchAmount > 0f)
            {
                if (!isBranching)
                {
                    MakeBranchSegment(2);
                }
            }

            if (branchAmount <= 0f)
            {
                if (!isBranching && !isSubBranching)
                {
                    prevBranchEnd = prevEnd;
                }
            }

            if (subBranchAmount <= 0f)
            {
                if (!isSubBranching && !isBranching)
                {
                    prevSubBranchEnd = prevEnd;
                }
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

    public bool AngleChanged(int i)
    {
        if (Time.time < downtime)
        {
            return false;
        }

        uptime = downtime + (upTime * i);

        if (!branchSideCalculated)
        {
            branchSide = GetBranchSide();
            branchSideCalculated = true;
        }

        if (Time.time > uptime)
        {
            downtime = Time.time + downTime;
            branchSideCalculated = false;
            return false;
        }

        Vector3 currentDirection = pReferencer.GetBrushPosition() - prevEnd;

        float angleDifference = Vector3.Angle(pastDirection, currentDirection);

        if (angleDifference > angleChange / i)
        {
            return true;
        }
        else
        {
            return false;
        }

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

        if (Keyboard.current.kKey.isPressed)                        // get more transparent
        {
            ditherValue += ditherUpdate;
        }
        else if (Keyboard.current.lKey.isPressed)                   // get more opaque
        {
            fadedIn = true;
            ditherValue -= ditherUpdate;

            if (ditherValue < ditherValueMin)
            {
                ditherValue = ditherValueMin;
            }
        }

        newMaterial.SetFloat("_DitherValue", ditherValue);
        seg.material = newMaterial;

        /*
        if (segments.Count > 0)
        {
            seg.startColor = segments[segments.Count - 1].endColor;
            seg.endColor = pReferencer.GetColor();
        }
        else
        {
            seg.startColor = pReferencer.GetColor();
            seg.endColor = pReferencer.GetColor();
        }
        */

        if (canChangeColor)
        {
            color = pReferencer.GetColor();
            canChangeColor = false;
        }

        seg.startColor = color;
        seg.endColor = color;
        
        
    }




    public void SetBranching()
    {
        bool wantsBranching = AngleChanged(4) && !isSubBranching && pReferencer.GetPlayerSpeed() >= 1000f;

        isBranching = wantsBranching;
    }

    public void SetSubBranching()
    {
        bool wantsSubBranching = AngleChanged(1) && !isBranching && pReferencer.GetPlayerSpeed() < 1000f;

        isSubBranching = wantsSubBranching;
    }

    public int GetBranchSide()
    {
        Vector3 currentDirection =
            pReferencer.GetBrushPosition() - prevEnd;

        float cross =
            pastDirection.x * currentDirection.y -
            pastDirection.y * currentDirection.x;

        if (cross < 0)
        {
            return -1;
        }
        else
        {
            return 1;
        }
    }


    private void UpdateBranchAmount()
    {
        float targetAmount = isBranching || isSubBranching ? 1f : 0f;
        int segmentCount = isBranching || isSubBranching ? enterSegments : returnSegments;
        float step = 1f / Mathf.Max(1, segmentCount);

        branchAmount = Mathf.MoveTowards(branchAmount, targetAmount, step);
    }

    private void UpdateSubBranchAmount()
    {
        float subTargetAmount = isSubBranching ? 1f : 0f;
        int subSegmentCount = isSubBranching ? enterSegments : returnSegments;
        float subStep = 1f / Mathf.Max(1, subSegmentCount);

        subBranchAmount = Mathf.MoveTowards(subBranchAmount, subTargetAmount, subStep);
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


