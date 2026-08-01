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
        [Tooltip("Diameter of the horn mouth (the wide end at the aim point) in world units.")]
        [SerializeField]
        [Min(0.1f)]
        float swirlScale = 3f;

        [Header("Horn Shape")]
        [Tooltip("Throat diameter as a fraction of the mouth. Small values make a narrow nozzle end that opens up toward the target.")]
        [SerializeField]
        [Range(0.02f, 1f)]
        float throatRatio = 0.12f;

        [Tooltip("Flare curve. 1 is a straight cone; larger values keep the near half cylindrical and open up only near the mouth.")]
        [SerializeField]
        [Range(1f, 6f)]
        float flareExponent = 2.4f;

        [Tooltip("Shortest horn length in world units. Keeps the funnel readable when the aim point is very close.")]
        [SerializeField]
        [Min(0.1f)]
        float minLength = 0.8f;

        [Tooltip("Longest horn length in world units. Stops the funnel from stretching across the whole scene.")]
        [SerializeField]
        [Min(0.5f)]
        float maxLength = 6f;

        [Tooltip("Segments around the circumference. Higher values smooth the swirl wrapping.")]
        [SerializeField]
        [Range(8, 96)]
        int radialSegments = 40;

        [Tooltip("Segments along the axis. Higher values smooth the flare curve.")]
        [SerializeField]
        [Range(2, 32)]
        int lengthSegments = 12;

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

        /// <summary>
        /// Unit horn currently in use (mouth radius 1 at z = 1, throat at z = 0), or null when the
        /// optional textures are missing and the overlay is inactive. Consumers can feed this to a
        /// ParticleSystem mesh shape so the airflow emits from exactly the surface the swirl is drawn on.
        /// The mesh instance is replaced whenever the shape parameters change, so re-read it each frame.
        /// </summary>
        public Mesh HornMesh => hornMesh;

        /// <summary>World size of the horn: x/y = mouth radius, z = length. Zero until the first Tick.
        /// This includes the breathing pulse, so it changes every frame.</summary>
        public Vector3 HornSize { get; private set; }

        /// <summary>
        /// Same as <see cref="HornSize"/> but without the breathing pulse. Consumers that should not
        /// visibly beat in lockstep with the swirl — an airflow ParticleSystem, for instance — should
        /// size themselves from this instead, otherwise they inherit the overlay's rhythm.
        /// </summary>
        public Vector3 HornBaseSize { get; private set; }

        /// <summary>Spin rate of the swirl around the aim axis, in degrees per second.</summary>
        public float SpinDegreesPerSecond => spinDegreesPerSecond;

        /// <summary>Throat diameter as a fraction of the mouth. Consumers need this to follow the taper.</summary>
        public float ThroatRatio => throatRatio;

        Mesh hornMesh;
        Material swirlMaterial;
        Texture2D[] swirlFrames;
        GameObject swirlObject;
        Renderer swirlRenderer;
        MeshFilter meshFilter;
        int currentFrame = -1;
        bool built;
        int builtRadialSegments;
        int builtLengthSegments;
        float builtThroatRatio;
        float builtFlareExponent;
        Mesh relaxedHorn;
        float relaxedConformity = float.NaN;
        float relaxedMouthBand = float.NaN;
        int relaxedRadialSegments;
        int relaxedLengthSegments;
        float relaxedThroatRatio;
        float relaxedFlareExponent;
        const int k_frameCount = 3;

        void Awake()
        {
            EnsureBuilt();
        }

        void OnDestroy()
        {
            DestroySafe(hornMesh);
            DestroySafe(relaxedHorn);
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

            // The horn spans the actual suction axis: the throat sits at the nozzle and the mouth at the
            // aim point. Anchoring to the axis is what keeps this overlay centred on the suction particles,
            // which follow the same axis. The old build placed the quad on a fixed horizontal offset (and,
            // when present, on a sibling named "AirDistortion"), so it drifted apart from the particles
            // whenever the player aimed up or down or the target sat at a different distance.
            Vector3 origin = ctx.ResolveNozzlePosition();
            Vector3 aimPoint = ResolveAimPoint(ctx, hasAimHit, aimHitPoint);
            Vector3 axis = aimPoint - origin;
            float rawLength = axis.magnitude;
            Vector3 direction = rawLength > 1e-4f
                ? axis / rawLength
                : (ctx.Nozzle != null ? ctx.Nozzle.forward : transform.forward);
            float length = Mathf.Clamp(rawLength, minLength, maxLength);

            // Spinning around the axis rotates the polar UVs without changing the silhouette,
            // so the swirl reads as turning inside the funnel.
            Quaternion facing = Quaternion.LookRotation(direction, Vector3.up);
            Quaternion spin = Quaternion.AngleAxis(Time.time * spinDegreesPerSecond, Vector3.forward);
            float pulse = 1f + pulseAmount * (
                0.7f * Mathf.Sin(Time.time * pulseSpeed * Mathf.PI * 2f) +
                0.3f * Mathf.Sin(Time.time * pulseSpeed * Mathf.PI * 2f * 1.7f + 1.3f));
            ApplyFrameAnimation();

            float baseMouthRadius = swirlScale * 0.5f;
            float mouthRadius = baseMouthRadius * pulse;
            HornBaseSize = new Vector3(baseMouthRadius, baseMouthRadius, length);
            HornSize = new Vector3(mouthRadius, mouthRadius, length);
            swirlObject.transform.SetPositionAndRotation(origin, facing * spin);
            swirlObject.transform.localScale = ResolveLocalScale(mouthRadius, length);
            SetVisible(true);
        }

        Vector3 ResolveAimPoint(in SuctionContext ctx, bool hasAimHit, Vector3 aimHitPoint)
        {
            if (hasAimHit)
            {
                return aimHitPoint;
            }

            Ray ray = ctx.AimRay;
            if (ray.direction.sqrMagnitude > 1e-6f)
            {
                float reach = ctx.MaxDistance > 0f ? Mathf.Min(ctx.MaxDistance, maxLength) : maxLength;
                return ray.origin + ray.direction.normalized * reach;
            }

            // No usable aim ray at all: point the horn along the nozzle so it still has an axis.
            Transform nozzle = ctx.Nozzle != null ? ctx.Nozzle : transform;
            return nozzle.position + nozzle.forward * maxLength;
        }

        // The mesh is a unit horn (mouth radius 1, length 1), so world size comes from the transform.
        // Any scale on the parent would multiply into it, hence the explicit cancellation.
        Vector3 ResolveLocalScale(float mouthRadius, float length)
        {
            var scale = new Vector3(mouthRadius, mouthRadius, length);
            Transform parent = swirlObject.transform.parent;
            if (parent == null)
            {
                return scale;
            }

            Vector3 lossy = parent.lossyScale;
            return new Vector3(
                Mathf.Approximately(lossy.x, 0f) ? scale.x : scale.x / lossy.x,
                Mathf.Approximately(lossy.y, 0f) ? scale.y : scale.y / lossy.y,
                Mathf.Approximately(lossy.z, 0f) ? scale.z : scale.z / lossy.z);
        }

        void EnsureBuilt()
        {
            if (built)
            {
                // Shape values are meant to be dialled in by hand from the Inspector while playing,
                // so rebuild the mesh whenever one of them changes instead of baking it once.
                RebuildMeshIfShapeChanged();
                return;
            }

            built = true;
            swirlFrames = TryLoadTextures(swirlFramePaths, "Optional Vacuum Swirl Texture");
            if (swirlFrames == null || swirlFrames.Length == 0)
            {
                return;
            }

            hornMesh = BuildHornMesh(radialSegments, lengthSegments, throatRatio, flareExponent);
            RecordBuiltShape();
            swirlMaterial = CreateAdditiveMaterial(swirlFrames[0]);
            swirlObject = new GameObject("Optional Local Vacuum Swirl VFX");
            swirlObject.transform.SetParent(transform, false);

            meshFilter = swirlObject.AddComponent<MeshFilter>();
            meshFilter.sharedMesh = hornMesh;

            swirlRenderer = swirlObject.AddComponent<MeshRenderer>();
            swirlRenderer.sharedMaterial = swirlMaterial;
            swirlRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            swirlRenderer.receiveShadows = false;
            SetVisible(false);
        }

        /// <summary>
        /// Returns a looser variant of the horn for consumers that should follow the funnel only
        /// partially — e.g. an airflow ParticleSystem that hugs the shape without sticking to its surface.
        /// <paramref name="conformity"/> 0 gives a straight cylinder at mouth radius, 1 gives the horn
        /// itself, and values in between flatten the flare. Cached; rebuilt only when the shape or the
        /// requested conformity changes. Returns null when the overlay is inactive.
        /// </summary>
        public Mesh GetRelaxedHorn(float conformity, float mouthBand = 1f)
        {
            EnsureBuilt();
            if (hornMesh == null)
            {
                return null;
            }

            conformity = Mathf.Clamp01(conformity);
            mouthBand = Mathf.Clamp(mouthBand, 0.01f, 1f);
            if (relaxedHorn != null
                && Mathf.Approximately(relaxedConformity, conformity)
                && Mathf.Approximately(relaxedMouthBand, mouthBand)
                && relaxedRadialSegments == radialSegments
                && relaxedLengthSegments == lengthSegments
                && Mathf.Approximately(relaxedThroatRatio, throatRatio)
                && Mathf.Approximately(relaxedFlareExponent, flareExponent))
            {
                return relaxedHorn;
            }

            // Blending toward (throat 1, flare 1) straightens the profile into a plain cylinder,
            // so conformity acts as "how much of the funnel shape to keep".
            Mesh replacement = BuildHornMesh(
                radialSegments,
                lengthSegments,
                Mathf.Lerp(1f, throatRatio, conformity),
                Mathf.Lerp(1f, flareExponent, conformity),
                1f - mouthBand);
            DestroySafe(relaxedHorn);
            relaxedHorn = replacement;
            relaxedConformity = conformity;
            relaxedMouthBand = mouthBand;
            relaxedRadialSegments = radialSegments;
            relaxedLengthSegments = lengthSegments;
            relaxedThroatRatio = throatRatio;
            relaxedFlareExponent = flareExponent;
            return relaxedHorn;
        }

        void RebuildMeshIfShapeChanged()
        {
            if (meshFilter == null || ShapeMatchesBuilt())
            {
                return;
            }

            Mesh replacement = BuildHornMesh(radialSegments, lengthSegments, throatRatio, flareExponent);
            meshFilter.sharedMesh = replacement;
            DestroySafe(hornMesh);
            hornMesh = replacement;
            RecordBuiltShape();
        }

        bool ShapeMatchesBuilt()
        {
            return builtRadialSegments == radialSegments
                && builtLengthSegments == lengthSegments
                && Mathf.Approximately(builtThroatRatio, throatRatio)
                && Mathf.Approximately(builtFlareExponent, flareExponent);
        }

        void RecordBuiltShape()
        {
            builtRadialSegments = radialSegments;
            builtLengthSegments = lengthSegments;
            builtThroatRatio = throatRatio;
            builtFlareExponent = flareExponent;
        }

        /// <summary>
        /// Builds a unit horn: mouth radius 1 at z = 1, throat radius <paramref name="throatRatio"/> at z = 0.
        /// UVs are a polar projection of the disc, so the texture centre lands on the throat and its rim on
        /// the mouth. A front-facing swirl image therefore wraps onto the funnel and still reads as the
        /// original swirl when viewed down the axis, instead of being stretched into a band.
        /// Open at both ends so the inside stays visible (the overlay shader draws with Cull Off).
        /// </summary>
        static Mesh BuildHornMesh(
            int radialSegments, int lengthSegments, float throatRatio, float flareExponent, float vStart = 0f)
        {
            int ringCount = lengthSegments + 1;
            int columnCount = radialSegments + 1;   // duplicate the seam column so UVs do not wrap backwards
            var vertices = new Vector3[ringCount * columnCount];
            var uvs = new Vector2[ringCount * columnCount];
            var triangles = new int[radialSegments * lengthSegments * 6];

            for (int ring = 0; ring < ringCount; ring++)
            {
                // vStart > 0 keeps only the mouth end of the funnel. The profile is still evaluated at the
                // true axial position, so a partial band sits exactly on the full horn's surface.
                float v = Mathf.Lerp(vStart, 1f, (float)ring / lengthSegments);
                float radius = Mathf.Lerp(throatRatio, 1f, Mathf.Pow(v, flareExponent));
                for (int column = 0; column < columnCount; column++)
                {
                    float angle = (float)column / radialSegments * Mathf.PI * 2f;
                    float cos = Mathf.Cos(angle);
                    float sin = Mathf.Sin(angle);
                    int index = ring * columnCount + column;
                    vertices[index] = new Vector3(cos * radius, sin * radius, v);
                    uvs[index] = new Vector2(0.5f + 0.5f * cos * radius, 0.5f + 0.5f * sin * radius);
                }
            }

            int triangle = 0;
            for (int ring = 0; ring < lengthSegments; ring++)
            {
                for (int column = 0; column < radialSegments; column++)
                {
                    int current = ring * columnCount + column;
                    int next = current + columnCount;

                    triangles[triangle++] = current;
                    triangles[triangle++] = next;
                    triangles[triangle++] = current + 1;

                    triangles[triangle++] = current + 1;
                    triangles[triangle++] = next;
                    triangles[triangle++] = next + 1;
                }
            }

            var mesh = new Mesh { name = "Optional Local Vacuum Swirl Horn" };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
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
