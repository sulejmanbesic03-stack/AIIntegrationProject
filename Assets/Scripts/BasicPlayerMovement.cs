using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(CapsuleCollider))]
public class BasicPlayerMovement : MonoBehaviour
{
    [Header("Movement Settings")]
    [SerializeField] private float moveSpeed = 6f;
    [SerializeField] private float jumpForce = 5f;

    [Header("Look Settings")]
    [SerializeField] private float mouseSensitivity = 2f;
    [SerializeField] private Transform cameraTransform;

    [Header("Ground Check Settings")]
    [SerializeField] private float groundCheckDistance = 0.2f;
    [SerializeField] private LayerMask groundMask = ~0;

    private Rigidbody rb;
    private CapsuleCollider capsuleCollider;

    private float pitch = 0f;
    private float cachedHorizontal;
    private float cachedVertical;
    private float cachedMouseX;
    private float cachedMouseY;
    private bool jumpRequested;
    private bool isGrounded;

    public bool IsGrounded => isGrounded;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        capsuleCollider = GetComponent<CapsuleCollider>();

        // Lock and hide cursor for FPS camera look
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        // Find camera reference if not assigned
        if (cameraTransform == null)
        {
            Camera cam = GetComponentInChildren<Camera>();
            if (cam != null)
            {
                cameraTransform = cam.transform;
            }
            else if (Camera.main != null)
            {
                cameraTransform = Camera.main.transform;
            }
        }

        // Configure Rigidbody parameters
        rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
    }

    private void Update()
    {
        // Read input in Update
        cachedHorizontal = Input.GetAxisRaw("Horizontal");
        cachedVertical = Input.GetAxisRaw("Vertical");

        cachedMouseX = Input.GetAxis("Mouse X") * mouseSensitivity;
        cachedMouseY = Input.GetAxis("Mouse Y") * mouseSensitivity;

        if (Input.GetButtonDown("Jump"))
        {
            jumpRequested = true;
        }

        // Vertical pitch on camera
        pitch -= cachedMouseY;
        pitch = Mathf.Clamp(pitch, -85f, 85f);
        if (cameraTransform != null)
        {
            cameraTransform.localRotation = Quaternion.Euler(pitch, 0f, 0f);
        }
    }

    private void FixedUpdate()
    {
        CheckGroundStatus();

        // Horizontal yaw on body via Rigidbody physics
        Quaternion yawRotation = Quaternion.Euler(0f, cachedMouseX, 0f);
        rb.MoveRotation(rb.rotation * yawRotation);

        // Movement velocity calculation
        Vector3 inputDir = (transform.right * cachedHorizontal + transform.forward * cachedVertical).normalized;
        Vector3 targetVelocity = inputDir * moveSpeed;

        Vector3 velocity = rb.linearVelocity;
        velocity.x = targetVelocity.x;
        velocity.z = targetVelocity.z;

        if (jumpRequested)
        {
            jumpRequested = false;
            if (isGrounded)
            {
                velocity.y = jumpForce;
            }
        }

        rb.linearVelocity = velocity;
    }

    private void CheckGroundStatus()
    {
        if (capsuleCollider == null) return;

        float checkRadius = capsuleCollider.radius * 0.9f;
        Vector3 origin = transform.position + capsuleCollider.center;
        float rayDistance = (capsuleCollider.height * 0.5f - capsuleCollider.radius) + groundCheckDistance + 0.05f;

        isGrounded = Physics.SphereCast(origin, checkRadius, Vector3.down, out _, rayDistance, groundMask, QueryTriggerInteraction.Ignore);
    }
}
