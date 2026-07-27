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
        hAction = new InputAction(type: InputActionType.Value);

        hAction.AddCompositeBinding("2DVector")
            .With("Up", "<Keyboard>/w")
            .With("Down", "<Keyboard>/s")
            .With("Left", "<Keyboard>/a")
            .With("Right", "<Keyboard>/d");


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

        Vector3 cameraForward = cameraTransform.forward;
        Vector3 cameraRight = cameraTransform.right;

        cameraForward.y = 0f;
        cameraRight.y = 0f;

        cameraForward.Normalize();
        cameraRight.Normalize();

        Vector3 inputDir =
            cameraRight * hInput.x +
            cameraForward * hInput.y;

        Vector3 targetVelocity = inputDir * hSpeed;

        currentVelocity = Vector3.SmoothDamp(
            currentVelocity,
            targetVelocity,
            ref velocityRef,
            smoothTime
        );

        float vertical = vInput * vSpeed;

        Vector3 movement = new Vector3(
            currentVelocity.x,
            vertical,
            currentVelocity.z
        );

        player.position += movement * Time.deltaTime;

        Vector3 position = player.position;
        position.y = dReference.GetGroundHeight();
        player.position = position;

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