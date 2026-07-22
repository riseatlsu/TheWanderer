
// Generaate movement for the player sphere


using UnityEngine;
using UnityEngine.InputSystem;
using static UnityEditor.Rendering.CameraUI;

public class PlayerMovement : MonoBehaviour
{
    [Header("Player Settings")]
    [SerializeField] private Transform player;
    [SerializeField] private float hSpeed = 1000f;
    [SerializeField] private float vSpeed = 100f;
    [SerializeField] private float speedUpdate = 1f;

    [Header("Smoothing")]
    [SerializeField] private float smoothTime = 0.75f;

    [Header("Reference Data")]
    [SerializeField] private DistanceReferencer dReference;

    private InputAction hAction;
    private InputAction vAction;

    private Vector3 currentVelocity;
    private Vector3 velocityRef;

    private float maxSpeed = 1750f;
    private float minSpeed = 250f;
    private float maxSmooth = 1f;
    private float minSmooth = 0.25f;

    private float smoothUpdate;

    private void Start()
    {
        smoothUpdate = (maxSmooth - minSmooth) / ((maxSpeed - minSpeed) / speedUpdate);     // proportional update values
    }

    private void OnEnable()
    {
        hAction.Enable();
        vAction.Enable();
    }

    private void OnDisable()
    {
        hAction.Disable();
        vAction.Disable();
    }

    void Awake()
    {
        hAction = new InputAction(type: InputActionType.Value);
        hAction.AddCompositeBinding("2DVector")
            .With("Left", "<Keyboard>/w")
            .With("Right", "<Keyboard>/s")
            .With("Down", "<Keyboard>/a")
            .With("Up", "<Keyboard>/d");

        vAction = new InputAction(type: InputActionType.Value);
        vAction.AddCompositeBinding("1DAxis")
            .With("Positive", "<Keyboard>/rightArrow")
            .With("Negative", "<Keyboard>/leftArrow");
    }

    void Update()
    {
        UpdateSmoothness();
        HandleMovement();
    }

    // update speed based on n and m inputs, make smoothness dependent on speed
    public void UpdateSmoothness()
    {
        if (Keyboard.current.nKey.isPressed)    
        {
            hSpeed += speedUpdate;
            smoothTime += smoothUpdate;

            if (hSpeed > maxSpeed)
            {
                hSpeed = maxSpeed;
                smoothTime = maxSmooth;
            }
        }
        else if (Keyboard.current.mKey.isPressed)     
        {
            hSpeed -= speedUpdate;
            smoothTime -= smoothUpdate;

            if (hSpeed < minSpeed)
            {
                hSpeed = minSpeed;
                smoothTime = minSmooth;
            }
        }
    }

    public void HandleMovement()
    {
        Vector2 hInput = hAction.ReadValue<Vector2>();
        float vInput = vAction.ReadValue<float>();


        // Horizontal movement (smooth)
        Vector3 inputDir = new Vector3(hInput.x, 0f, hInput.y);
        Vector3 targetVelocity = inputDir * hSpeed;

        currentVelocity = Vector3.SmoothDamp(
            currentVelocity,
            targetVelocity,
            ref velocityRef,
            smoothTime
        );

        // Vertical movement (instant, no smoothing, no acceleration)
        float vertical = vInput * vSpeed;

        Vector3 movement = new Vector3(
            currentVelocity.x,
            vertical,
            currentVelocity.z
        );

        player.position += movement * Time.deltaTime;

        HandleCollisions();
    }

    
    public void HandleCollisions()
    {
        float distance = dReference.GetDistanceToGround();

        if (distance <= 0.01f)
        {
            Vector3 pTransform = player.position;
            pTransform.y = dReference.GetGroundHeight();
            player.position = pTransform;
        }
    }

    public float GetCurrentSpeed()
    {
        return hSpeed;
    }

}