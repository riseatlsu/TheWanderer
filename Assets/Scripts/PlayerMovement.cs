// Generates movement for the player sphere

using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerMovement : MonoBehaviour
{
    [Header("Player Settings")]
    [SerializeField] private Transform player;
    [SerializeField] private Transform cameraTransform;
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
        smoothUpdate = (maxSmooth - minSmooth) /
                       ((maxSpeed - minSpeed) / speedUpdate);
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


    private void Awake()
    {
        // WASD movement
        hAction = new InputAction(type: InputActionType.Value);

        hAction.AddCompositeBinding("2DVector")
            .With("Up", "<Keyboard>/w")
            .With("Down", "<Keyboard>/s")
            .With("Left", "<Keyboard>/a")
            .With("Right", "<Keyboard>/d");


        // Vertical movement with arrow keys
        vAction = new InputAction(type: InputActionType.Value);

        vAction.AddCompositeBinding("1DAxis")
            .With("Positive", "<Keyboard>/rightArrow")
            .With("Negative", "<Keyboard>/leftArrow");
    }


    private void Update()
    {
        UpdateSmoothness();
        HandleMovement();
    }


    // Update speed based on N and M inputs.
    // Smoothness changes depending on speed.
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
        // Get WASD input
        Vector2 hInput = hAction.ReadValue<Vector2>();

        // Get vertical input
        float vInput = vAction.ReadValue<float>();


        // Get camera directions
        Vector3 cameraForward = cameraTransform.forward;
        Vector3 cameraRight = cameraTransform.right;


        // Ignore the camera's vertical rotation.
        // This keeps movement strictly horizontal.
        cameraForward.y = 0f;
        cameraRight.y = 0f;


        // Normalize directions
        cameraForward.Normalize();
        cameraRight.Normalize();


        // Convert WASD input to camera-relative movement.
        //
        // W = Away from camera / camera forward
        // S = Towards camera / camera backward
        // A = Camera left
        // D = Camera right
        Vector3 inputDir =
            cameraRight * hInput.x +
            cameraForward * hInput.y;


        // Calculate target horizontal velocity
        Vector3 targetVelocity = inputDir * hSpeed;


        // Smooth horizontal movement
        currentVelocity = Vector3.SmoothDamp(
            currentVelocity,
            targetVelocity,
            ref velocityRef,
            smoothTime
        );


        // Vertical movement
        // Right Arrow = Up
        // Left Arrow = Down
        float vertical = vInput * vSpeed;


        // Combine horizontal and vertical movement
        Vector3 movement = new Vector3(
            currentVelocity.x,
            vertical,
            currentVelocity.z
        );


        // Move player
        player.position += movement * Time.deltaTime;

        Vector3 position = player.position;
        position.y = dReference.GetGroundHeight();
        player.position = position;

        // Handle ground collision
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