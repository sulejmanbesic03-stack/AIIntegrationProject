using UnityEngine;

public class CubeAI : MonoBehaviour
{
    // Center of the circular path
    public Vector3 center = Vector3.zero;
    // Base radius of the circular path
    public float radius = 5f;
    // Amount of random radius variation (increase for more erratic movement)
    public float radiusJitter = 1f;
    // Speed of movement (radians per second)
    public float angularSpeed = 1f;
    // Maximum random angle offset per frame (radians) – larger value = less linear
    public float angleJitter = 0.5f;
    // Optional vertical jitter range
    public float heightJitter = 0.3f;
    // Fixed base height (can be zero)
    public float baseHeight = 0f;

    private float angle = 0f;

    void Update()
    {
        // Increment base angle based on speed and time
        angle += angularSpeed * Time.deltaTime;
        // Keep angle in 0‑2π range
        if (angle > Mathf.PI * 2f) angle -= Mathf.PI * 2f;

        // Apply random jitter to angle and radius for non‑linear movement
        float jitteredAngle = angle + Random.Range(-angleJitter, angleJitter);
        float jitteredRadius = radius + Random.Range(-radiusJitter, radiusJitter);
        float jitteredHeight = baseHeight + Random.Range(-heightJitter, heightJitter);

        // Compute new position on jittered circle
        float x = Mathf.Cos(jitteredAngle) * jitteredRadius;
        float z = Mathf.Sin(jitteredAngle) * jitteredRadius;
        Vector3 newPos = center + new Vector3(x, jitteredHeight, z);
        transform.position = newPos;
    }
}
