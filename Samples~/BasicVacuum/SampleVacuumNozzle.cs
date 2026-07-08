using UnityEngine;

namespace Vacuum.Samples
{
    /// <summary>
    /// Minimal, dependency-free nozzle driver for the Basic Vacuum Demo. Each frame it builds a
    /// <see cref="SuctionContext"/> from the aim camera (mouse ray) and feeds it to the sibling
    /// <see cref="SuctionSource"/> while the suck button is held. Uses the legacy Input Manager so
    /// it works without the Input System package. Replace this with your own controller in a real game.
    /// </summary>
    [RequireComponent(typeof(SuctionSource))]
    public sealed class SampleVacuumNozzle : MonoBehaviour
    {
        [Header("Aim")]
        [Tooltip("Camera used to turn the mouse cursor into an aim ray. Defaults to Camera.main.")]
        [SerializeField] Camera aimCamera;

        [Tooltip("If true, aim with the mouse cursor; otherwise aim straight along this transform's forward.")]
        [SerializeField] bool aimWithMouse = true;

        [Header("Suction")]
        [Tooltip("Suction power, compared against each target's Required Suction Power.")]
        [SerializeField, Min(0f)] float power = 1f;

        [Tooltip("Max suck range in meters. 0 = unlimited.")]
        [SerializeField, Min(0f)] float maxDistance = 30f;

        [Tooltip("Mouse button to hold to suck (0 = left, 1 = right, 2 = middle).")]
        [SerializeField, Min(0)] int suckMouseButton;

        SuctionSource source;

        void Awake()
        {
            source = GetComponent<SuctionSource>();
            if (aimCamera == null)
            {
                aimCamera = Camera.main;
            }
        }

        void Update()
        {
            bool held = Input.GetMouseButton(suckMouseButton);
            Ray aimRay = ResolveAimRay();
            var ctx = new SuctionContext(transform, transform.position, aimRay, power, maxDistance, held);

            // Tick every frame (even when released) so an interruption can resolve to backflow.
            source.Tick(ctx);
        }

        Ray ResolveAimRay()
        {
            if (aimWithMouse && aimCamera != null)
            {
                return aimCamera.ScreenPointToRay(Input.mousePosition);
            }

            return new Ray(transform.position, transform.forward);
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.cyan;
            float length = maxDistance > 0f ? maxDistance : 10f;
            Gizmos.DrawLine(transform.position, transform.position + transform.forward * length);
        }
    }
}
