using System.IO;
using UnityEngine;

namespace Vacuum
{
    /// <summary>
    /// Optional local-only swirl overlay for suction feedback. It loads a texture from local disk
    /// at runtime so the repository can avoid redistributing third-party texture files.
    /// Missing files simply disable this VFX.
    /// </summary>
    public sealed class SuctionLocalTextureVfx : MonoBehaviour
    {
        [Header("Optional Local Textures")]
        [Tooltip("Project-relative or absolute paths for the animated swirl textures. Missing files are ignored.")]
        [SerializeField]
        string[] swirlFramePaths =
        {
            "Assets/LocalOnly/VacuumVFX/T_Crom_Swirl_1.png",
            "Assets/LocalOnly/VacuumVFX/T_Crom_Swirl_2.png",
            "Assets/LocalOnly/VacuumVFX/T_Crom_Swirl_3.png",
            "../../assets/textures/DAVFX Free Textures 1.0/T_Crom_Swirl_1.png",
            "../../assets/textures/DAVFX Free Textures 1.0/T_Crom_Swirl_2.png",
            "../../assets/textures/DAVFX Free Textures 1.0/T_Crom_Swirl_3.png"
        };

        [Header("Overlay")]
        [Tooltip("Horizontal distance in front of the nozzle/player where the swirl overlay is drawn. Match this to the suction particle center.")]
        [SerializeField]
        [Min(0f)]
        float forwardOffset = 0.9f;

        [Tooltip("Vertical lift from the nozzle/player where the swirl overlay is drawn. Match this to the suction particle center.")]
        [SerializeField]
        float verticalOffset = 0.4f;

        [Tooltip("World scale for the swirl overlay. The default matches the existing suction air distortion plane.")]
        [SerializeField]
        [Min(0.1f)]
        float swirlScale = 3f;

        [Tooltip("Rotation speed around the aim axis.")]
        [SerializeField]
        [Min(0f)]
        float spinDegreesPerSecond = 260f;

        [Tooltip("Gentle breathing scale amount. Keeps the center fixed and only changes the outer radius.")]
        [SerializeField]
        [Range(0f, 0.5f)]
        float pulseAmount = 0.08f;

        [Tooltip("Breathing speed for the scale pulse.")]
        [SerializeField]
        [Min(0f)]
        float pulseSpeed = 1.25f;

        [Tooltip("Texture flipbook playback speed.")]
        [SerializeField]
        [Min(0.1f)]
        float framesPerSecond = 10f;

        Mesh quadMesh;
        Material swirlMaterial;
        Texture2D[] swirlFrames;
        GameObject swirlObject;
        Renderer swirlRenderer;
        Transform alignmentTarget;
        int currentFrame = -1;
        bool built;
        const int k_frameCount = 3;

        void Awake()
        {
            EnsureBuilt();
        }

        void OnDestroy()
        {
            DestroySafe(quadMesh);
            DestroySafe(swirlMaterial);
            if (swirlFrames != null)
            {
                for (int i = 0; i < swirlFrames.Length; i++)
                {
                    DestroySafe(swirlFrames[i]);
                }
            }
        }

        public void Tick(in SuctionContext ctx, bool consumed, bool hasAimHit, Vector3 aimHitPoint)
        {
            EnsureBuilt();
            if (swirlRenderer == null || !ctx.Held)
            {
                SetVisible(false);
                return;
            }

            Transform target = ResolveAlignmentTarget();
            Vector3 center = target != null ? target.position : ResolveFeedbackCenter(ctx);
            Quaternion facing = ResolveCameraFacingRotation(center);
            Quaternion spin = Quaternion.AngleAxis(Time.time * spinDegreesPerSecond, Vector3.forward);
            float pulse = 1f + pulseAmount * (
                0.7f * Mathf.Sin(Time.time * pulseSpeed * Mathf.PI * 2f) +
                0.3f * Mathf.Sin(Time.time * pulseSpeed * Mathf.PI * 2f * 1.7f + 1.3f));
            float baseScale = target != null ? ResolveTargetScale(target) : swirlScale;
            ApplyFrameAnimation();

            // Match SprayFoamEmitter.ResolveSuctionFeedbackPoint: horizontal forward + mid-height.
            swirlObject.transform.SetPositionAndRotation(center, facing * spin);
            swirlObject.transform.localScale = Vector3.one * (baseScale * pulse);
            SetVisible(true);
        }

        void EnsureBuilt()
        {
            if (built)
            {
                return;
            }

            built = true;
            swirlFrames = TryLoadTextures(swirlFramePaths, "Optional Vacuum Swirl Texture");
            if (swirlFrames == null || swirlFrames.Length == 0)
            {
                return;
            }

            quadMesh = BuildQuadMesh();
            swirlMaterial = CreateAdditiveMaterial(swirlFrames[0]);
            swirlObject = new GameObject("Optional Local Vacuum Swirl VFX");
            swirlObject.transform.SetParent(transform, false);

            var filter = swirlObject.AddComponent<MeshFilter>();
            filter.sharedMesh = quadMesh;

            swirlRenderer = swirlObject.AddComponent<MeshRenderer>();
            swirlRenderer.sharedMaterial = swirlMaterial;
            swirlRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            swirlRenderer.receiveShadows = false;
            SetVisible(false);
        }

        static Mesh BuildQuadMesh()
        {
            var mesh = new Mesh { name = "Optional Local Vacuum Swirl Quad" };
            mesh.SetVertices(new[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f)
            });
            mesh.SetUVs(0, new[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(0f, 1f),
                new Vector2(1f, 1f)
            });
            mesh.SetTriangles(new[] { 0, 2, 1, 1, 2, 3 }, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        void ApplyFrameAnimation()
        {
            if (swirlMaterial == null || swirlFrames == null || swirlFrames.Length == 0)
            {
                return;
            }

            int frame = Mathf.FloorToInt(Time.time * framesPerSecond) % swirlFrames.Length;
            if (frame == currentFrame)
            {
                return;
            }

            currentFrame = frame;
            swirlMaterial.mainTexture = swirlFrames[frame];
        }

        static Material CreateAdditiveMaterial(Texture texture)
        {
            Shader shader =
                Shader.Find("Vacuum/Suction Local Texture Overlay") ??
                Shader.Find("Legacy Shaders/Particles/Additive") ??
                Shader.Find("Particles/Additive") ??
                Shader.Find("Unlit/Transparent") ??
                Shader.Find("Standard");

            var material = new Material(shader) { name = "Optional Local Vacuum Swirl VFX (Runtime)" };
            material.mainTexture = texture;
            material.color = Color.white;
            if (material.HasProperty("_TintColor"))
            {
                material.SetColor("_TintColor", Color.white);
            }

            material.renderQueue = 3000;
            return material;
        }

        static Texture2D[] TryLoadTextures(string[] paths, string textureName)
        {
            if (paths == null)
            {
                return null;
            }

            for (int i = 0; i + k_frameCount <= paths.Length; i += k_frameCount)
            {
                Texture2D[] textureSet = TryLoadTextureSet(paths, i, k_frameCount, textureName);
                if (textureSet != null)
                {
                    return textureSet;
                }
            }

            var fallbackTextures = new System.Collections.Generic.List<Texture2D>();
            for (int i = 0; i < paths.Length; i++)
            {
                Texture2D texture = TryLoadTexture(paths[i], textureName);
                if (texture != null)
                {
                    fallbackTextures.Add(texture);
                }
            }

            return fallbackTextures.Count > 0 ? fallbackTextures.ToArray() : null;
        }

        static Texture2D[] TryLoadTextureSet(string[] paths, int startIndex, int count, string textureName)
        {
            var textures = new Texture2D[count];
            for (int i = 0; i < count; i++)
            {
                textures[i] = TryLoadTexture(paths[startIndex + i], textureName);
                if (textures[i] != null)
                {
                    continue;
                }

                for (int j = 0; j < i; j++)
                {
                    DestroySafe(textures[j]);
                }

                return null;
            }

            return textures;
        }

        static Texture2D TryLoadTexture(string path, string textureName)
        {
            string fullPath = ResolveProjectPath(path);
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
            {
                return null;
            }

            byte[] bytes = File.ReadAllBytes(fullPath);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                name = textureName,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            if (ImageConversion.LoadImage(texture, bytes, false))
            {
                return texture;
            }

            DestroySafe(texture);
            return null;
        }

        static string ResolveProjectPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            if (Path.IsPathRooted(path))
            {
                return path;
            }

            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            return projectRoot == null ? null : Path.GetFullPath(Path.Combine(projectRoot, path));
        }

        void SetVisible(bool visible)
        {
            if (swirlRenderer != null)
            {
                swirlRenderer.enabled = visible;
            }
        }

        Transform ResolveAlignmentTarget()
        {
            if (alignmentTarget != null)
            {
                return alignmentTarget;
            }

            Transform[] children = transform.GetComponentsInChildren<Transform>(includeInactive: true);
            for (int i = 0; i < children.Length; i++)
            {
                Transform child = children[i];
                if (child != null && child != transform && child.name == "AirDistortion")
                {
                    alignmentTarget = child;
                    return alignmentTarget;
                }
            }

            return null;
        }

        static float ResolveTargetScale(Transform target)
        {
            Vector3 scale = target.lossyScale;
            return Mathf.Max(0.1f, (Mathf.Abs(scale.x) + Mathf.Abs(scale.y)) * 0.5f);
        }

        Vector3 ResolveFeedbackCenter(in SuctionContext ctx)
        {
            Transform nozzle = ctx.Nozzle != null ? ctx.Nozzle : transform;
            Vector3 flatForward = Vector3.ProjectOnPlane(nozzle.forward, Vector3.up);
            if (flatForward.sqrMagnitude <= 1e-4f)
            {
                flatForward = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
            }

            if (flatForward.sqrMagnitude <= 1e-4f)
            {
                flatForward = Vector3.forward;
            }

            return nozzle.position + flatForward.normalized * forwardOffset + Vector3.up * verticalOffset;
        }

        static Quaternion ResolveCameraFacingRotation(Vector3 center)
        {
            Camera camera = Camera.main;
            if (camera == null)
            {
                return Quaternion.identity;
            }

            Vector3 toCamera = center - camera.transform.position;
            if (toCamera.sqrMagnitude <= 1e-6f)
            {
                return camera.transform.rotation;
            }

            return Quaternion.LookRotation(toCamera.normalized, camera.transform.up);
        }

        static void DestroySafe(UnityEngine.Object obj)
        {
            if (obj == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(obj);
            }
            else
            {
                DestroyImmediate(obj);
            }
        }
    }
}
