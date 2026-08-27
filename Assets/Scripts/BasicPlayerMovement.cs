using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(Collider))]
public class BasicPlayerMovement : MonoBehaviour
{
    public float moveSpeed = 5f;
    public float lookSensitivity = 2f;
    public float jumpForce = 5f;
    public float groundCheckDistance = 0.1f;
    public LayerMask groundMask = default;

    private Rigidbody rb;
    private Collider col;
    private Transform cameraTransform;
    private float pitch = 0f;

    // Cached input values
    private float cachedHorizontal;
    private float cachedVertical;
    private float cachedMouseX;
    private float cachedMouseY;
    private bool cachedJumpPressed;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        col = GetComponent<Collider>();
        // Ensure we can rotate around Y via physics
        rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;

        var head = transform.Find("Head");
        if (head != null)
        {
            var cam = head.GetComponentInChildren<Camera>(true);
            if (cam != null) cameraTransform = cam.transform;
        }
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        if (groundMask == 0)
        {
            groundMask = LayerMask.GetMask("Default");
        }
    }

    void Update()
    {
        // Read raw input each frame
        cachedMouseX = Input.GetAxis("Mouse X") * lookSensitivity;
        cachedMouseY = Input.GetAxis("Mouse Y") * lookSensitivity;
        cachedHorizontal = Input.GetAxisRaw("Horizontal");
        cachedVertical = Input.GetAxisRaw("Vertical");
        cachedJumpPressed = Input.GetButtonDown("Jump");

        // Apply mouse look (rotation) via Rigidbody.MoveRotation later
        pitch -= cachedMouseY;
        pitch = Mathf.Clamp(pitch, -85f, 85f);
        if (cameraTransform != null)
        {
            cameraTransform.localEulerAngles = new Vector3(pitch, 0f, 0f);
        }
    }

    void FixedUpdate()
    {
        // Apply rotation to the Rigidbody
        Quaternion targetRotation = Quaternion.Euler(0f, transform.eulerAngles.y + cachedMouseX, 0f);
        rb.MoveRotation(targetRotation);

        // Movement
        Vector3 move = (transform.right * cachedHorizontal + transform.forward * cachedVertical).normalized * moveSpeed;
        Vector3 velocity = new Vector3(move.x, rb.linearVelocity.y, move.z);
        rb.linearVelocity = velocity;

        // Jump
        if (cachedJumpPressed && IsGrounded())
        {
            rb.AddForce(Vector3.up * jumpForce, ForceMode.VelocityChange);
        }
    }

    bool IsGrounded()
    {
        // Raycast from slightly above the bottom of the collider
        float rayStartOffset = 0.05f;
        float rayLength = (col.bounds.extents.y - rayStartOffset) + groundCheckDistance;
        Vector3 origin = transform.position + Vector3.up * rayStartOffset;
        return Physics.Raycast(origin, Vector3.down, rayLength, groundMask);
    }
}
