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
    [SerializeField] int enterSegments = 8;
    [SerializeField] int returnSegments = 8;

    [Header("Material Settings")]
    [SerializeField] Material lineMaterial;
    //[SerializeField] float glowStrength = 1f;
    //[SerializeField] float glowSize = 1f;

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


    private bool isRendering = true;
    private bool isInitialized;

    private float widthDistance;

    //private Transform frozenLineHolder;


    private void Start()
    {
        segments = new List<LineRenderer>();
        branchSegments = new List<LineRenderer>();
        subBranchSegments = new List<LineRenderer>();

        pastDirection = Vector3.zero;

        widthDistance = pReferencer.GetWidthRange() * widthGap;

        //lineMaterial.SetFloat("_GlowStrength", glowStrength);
        //lineMaterial.SetFloat("_GlowSize", glowSize);

        isBranching = false;
        isSubBranching = false;
        isInitialized = false;
    }
    void Update()
    {
        if (!isRendering)
        {
            return;
        }

        if (!isInitialized)                                 // Delays initialization until first update
        {
            prevEnd = pReferencer.GetBrushPosition();
            prevBranchEnd = prevEnd;
            prevSubBranchEnd = prevEnd;
            isInitialized = true;
            return;
        }

        SetBranching();
        SetSubBranching();
        

        if (WidthChanged() || IsDistant() || AngleChanged())
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

    public bool AngleChanged()
    {

        float angleDifference = Vector3.Angle(pastDirection, pReferencer.GetBrushPosition() - pastDirection);

        if (angleDifference > angleChange)
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

        pastDirection = prevEnd - positions[totalPoints - overlap];
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
        seg.material = lineMaterial;
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
    }




    public void SetBranching()
    {
        bool wantsBranching = Keyboard.current.pKey.isPressed && segments.Count > 0 && !isSubBranching;

        
        if (wantsBranching && !isBranching && branchAmount <= 0)
        {
            branchSide = Random.value < 0.5 ? -1f : 1f;
        }
        

        isBranching = wantsBranching;
    }

    public void SetSubBranching()
    {
        bool wantsSubBranching = Keyboard.current.oKey.isPressed && segments.Count > 0 && !isBranching;

        
        if (wantsSubBranching && !isSubBranching && branchAmount <= 0 && subBranchAmount <= 0)
        {
            branchSide = Random.value < 0.5 ? -1f : 1f;
        }
        

        isSubBranching = wantsSubBranching;
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
        SetBranchWidth(segment);
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

    public void SetBranchWidth(LineRenderer seg)
    {
        if (branchSegments.Count == 0)
        {
            seg.startWidth = segments[segments.Count - 1].endWidth;
        }
        else
        {
            seg.startWidth = branchSegments[branchSegments.Count - 1].endWidth;
        }

        seg.endWidth = pReferencer.GetBranchWidth();
    }
    
    
    
    
    
    // Mode Switch Logic
    
    /*
    public void StopRendering()
    {
        isRendering = false;
        FreezeRenderedLines();
        HideOriginalLines();
        enabled = false;
    }

    public bool TryGetFrozenTraceBounds(out Bounds bounds)
    {
        return TryGetSavedLineBounds(segmentPositions, out bounds);
    }

    private bool TryGetSavedLineBounds(List<Vector3[]> savedPositions, out Bounds bounds)
    {
        bool hasPoint = false;
        bounds = new Bounds();

        foreach (Vector3[] positions in savedPositions)
        {
            foreach (Vector3 position in positions)
            {
                if (!hasPoint)
                {
                    bounds = new Bounds(position, Vector3.zero);
                    hasPoint = true;
                }
                else
                {
                    bounds.Encapsulate(position);
                }
            }
        }

        return hasPoint;
    }

    private void FreezeRenderedLines()
    {
        if (frozenLineHolder == null)
        {
            frozenLineHolder = new GameObject("FrozenRenderedLines").transform;
        }

        FreezeLineList(segments, segmentPositions);
        FreezeLineList(branchSegments, branchSegmentPositions);
        FreezeLineList(subBranchSegments, subBranchSegmentPositions);
    }

    private void FreezeLineList(List<LineRenderer> lineRenderers, List<Vector3[]> savedPositions)
    {
        for (int i = 0; i < lineRenderers.Count; i++)
        {
            LineRenderer lineRenderer = lineRenderers[i];
            if (lineRenderer == null)
            {
                continue;
            }

            Vector3[] positions = i < savedPositions.Count
                ? savedPositions[i]
                : GetLinePositions(lineRenderer);

            LineRenderer frozenLine = new GameObject("FrozenLineSegment").AddComponent<LineRenderer>();
            frozenLine.transform.SetParent(frozenLineHolder, false);
            frozenLine.useWorldSpace = true;
            frozenLine.positionCount = positions.Length;
            frozenLine.SetPositions(positions);

            frozenLine.startWidth = lineRenderer.startWidth;
            frozenLine.endWidth = lineRenderer.endWidth;
            frozenLine.startColor = lineRenderer.startColor;
            frozenLine.endColor = lineRenderer.endColor;
            frozenLine.material = lineRenderer.material;
            frozenLine.numCornerVertices = lineRenderer.numCornerVertices;
            frozenLine.numCapVertices = lineRenderer.numCapVertices;
            frozenLine.shadowCastingMode = lineRenderer.shadowCastingMode;
            frozenLine.receiveShadows = lineRenderer.receiveShadows;
        }
    }

    private Vector3[] GetLinePositions(LineRenderer lineRenderer)
    {
        Vector3[] positions = new Vector3[lineRenderer.positionCount];

        for (int i = 0; i < lineRenderer.positionCount; i++)
        {
            Vector3 position = lineRenderer.GetPosition(i);
            positions[i] = lineRenderer.useWorldSpace
                ? position
                : lineRenderer.transform.TransformPoint(position);
        }

        return positions;
    }

    private void HideOriginalLines()
    {
        SetLineListEnabled(segments, false);
        SetLineListEnabled(branchSegments, false);
        SetLineListEnabled(subBranchSegments, false);
    }

    private void SetLineListEnabled(List<LineRenderer> lineRenderers, bool enabledState)
    {
        foreach (LineRenderer lineRenderer in lineRenderers)
        {
            if (lineRenderer != null)
            {
                lineRenderer.enabled = enabledState;
            }
        }
    }
*/
    


}


