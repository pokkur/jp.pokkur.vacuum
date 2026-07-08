using UnityEngine;
using Vacuum;

namespace Vacuum.Samples
{
    /// <summary>
    /// One-component demo for the Vacuum package. Add this to an empty GameObject in a fresh scene and press Play.
    /// It builds a camera, a light, a ground, a nozzle (<see cref="SuctionSource"/> + <see cref="SampleVacuumNozzle"/>),
    /// and several <see cref="SuctionAbsorbable"/> targets of varied shape and size — one moving, one too strong to
    /// absorb. Hold the left mouse button and aim at a target to suck it in; release mid-suck to see it flow back.
    /// Uses standard primitives (no ProBuilder) so the sample stays dependency-free.
    ///
    /// LOCAL-0034: suction config is source-centric — select the built "Vacuum Nozzle" in the Hierarchy at runtime
    /// and tune its <see cref="SuctionSource"/> in the Inspector: Shape (Point / Spot + range / spotAngle),
    /// Max Concurrent, the Motion Profile (spiral / vortex / shake), and the optional category filter. One nozzle
    /// setting drives every target it pulls; the targets themselves only carry a resistance gate.
    /// </summary>
    public sealed class VacuumDemoSetup : MonoBehaviour
    {
        static readonly Color k_suckable = new Color(0.45f, 0.80f, 0.45f);
        static readonly Color k_tooStrong = new Color(0.85f, 0.35f, 0.35f);
        static readonly Color k_ground = new Color(0.60f, 0.60f, 0.62f);

        void Start()
        {
            EnsureLight();
            Camera cam = EnsureCamera();
            BuildNozzle(cam);
            BuildGround();
            BuildTargets();
        }

        void EnsureLight()
        {
            foreach (var existing in FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (existing.type == LightType.Directional)
                {
                    return;
                }
            }

            var go = new GameObject("Demo Light");
            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            go.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        }

        Camera EnsureCamera()
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                var go = new GameObject("Main Camera") { tag = "MainCamera" };
                cam = go.AddComponent<Camera>();
            }

            cam.transform.SetPositionAndRotation(new Vector3(0f, 3.5f, -7f), Quaternion.Euler(18f, 0f, 0f));
            return cam;
        }

        void BuildNozzle(Camera cam)
        {
            var go = new GameObject("Vacuum Nozzle");
            // Just in front of and below the camera, so absorbed targets stream toward the viewer.
            go.transform.position = cam.transform.position + cam.transform.forward * 0.6f + Vector3.down * 0.4f;
            SuctionSource source = go.AddComponent<SuctionSource>();
            source.AcquisitionMode = SuctionAcquisitionMode.Raycast;
            // SampleVacuumNozzle defaults aim with the mouse via Camera.main at power 1.
            go.AddComponent<SampleVacuumNozzle>();
            go.AddComponent<SuctionLocalTextureVfx>();
        }

        void BuildGround()
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.position = new Vector3(0f, 0f, 1.5f);
            ground.transform.localScale = Vector3.one * 2f;
            Tint(ground, k_ground);
        }

        void BuildTargets()
        {
            // requiredPower 0.5 < nozzle power 1 → suckable. The last one needs 2 → too strong, stays put (Failed).
            MakeTarget("Cube (suckable)", PrimitiveType.Cube, new Vector3(-2.6f, 0.40f, 1.0f), 0.8f, 0.5f, k_suckable);
            MakeTarget("Sphere (suckable)", PrimitiveType.Sphere, new Vector3(-0.9f, 0.50f, 1.9f), 1.0f, 0.5f, k_suckable);
            MakeTarget("Cylinder (suckable)", PrimitiveType.Cylinder, new Vector3(0.9f, 0.80f, 1.2f), 0.8f, 0.5f, k_suckable);

            SuctionAbsorbable moving = MakeTarget(
                "Capsule (moving)", PrimitiveType.Capsule, new Vector3(2.6f, 0.80f, 1.5f), 0.8f, 0.5f, k_suckable);
            moving.gameObject.AddComponent<SampleSuctionMover>();

            MakeTarget("Cube (too strong)", PrimitiveType.Cube, new Vector3(0f, 0.80f, 3.4f), 1.6f, 2.0f, k_tooStrong);
        }

        static SuctionAbsorbable MakeTarget(
            string name, PrimitiveType type, Vector3 position, float scale, float requiredPower, Color color)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.position = position;
            go.transform.localScale = Vector3.one * scale;
            Tint(go, color);

            var absorbable = go.AddComponent<SuctionAbsorbable>();
            absorbable.RequiredSuctionPower = requiredPower;
            return absorbable;
        }

        static void Tint(GameObject go, Color color)
        {
            var renderer = go.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material.color = color;
            }
        }
    }
}
