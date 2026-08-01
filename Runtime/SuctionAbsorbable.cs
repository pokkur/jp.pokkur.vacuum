using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace Vacuum
{
    /// <summary>
    /// LOCAL-0032 opt-in 吸引対象コンポーネント。<see cref="ISuctionTarget"/> を実装し、ノズル側ドライバ（SuctionSource など）
    /// から渡される <see cref="SuctionContext"/> で駆動される。5 状態機械
    /// （開始 / 中 / 失敗 / 逆流 / 完了）を持ち、各状態で <see cref="UnityEvent"/> と任意の per-state VFX を発火する。
    /// 中間で止まる状態は作らず、commit 後は必ず完了か逆流に解決する。funnel warp の数式は Step 0 スパイク
    /// から移植したもので、commit 時に頂点基準（s0/u0/lat0）を固定し、毎フレームは
    /// ノズル現在位置と進捗 t だけ動かす。これにより不変条件 t=0→identity / t=1→ノズル一点収束 / 形状項は進捗の
    /// 純関数（逆流が可逆）を保つ。震えは per-object コヒーレント（向きは全頂点共通、振幅のみ per-vertex）で表面が裂けない。
    /// LOCAL-0034: 吸引スタイル（speed/timing/vortex 等）は <see cref="SuctionMotionProfile"/> に集約され、commit 時に
    /// source から渡る profile（無ければ <see cref="fallbackProfile"/>）を <see cref="activeProfile"/> として採用する。
    /// 対象に残すのは gate（必要パワー）・geometry（subdivisions）・再生/VFX/debug のみ。
    /// </summary>
    [RequireComponent(typeof(MeshFilter))]
    public sealed class SuctionAbsorbable : MonoBehaviour, ISuctionTarget
    {
        public enum AbsorbState { Idle, Starting, Absorbing, Failed, Backflow, Completed }

        [Header("Suction Gate (成否判定)")]
        [Tooltip("吸い込みに必要なパワー閾値。ctx.power がこれ未満なら Failed（不動）になる。")]
        [SerializeField]
        [Min(0f)]
        float requiredSuctionPower = 0.5f;

        [Header("Filter (任意: 文字列カテゴリ)")]
        [Tooltip("この対象が属するカテゴリ（例: enemy, debris）。空なら無カテゴリ。ノズル側 SuctionSource.allowedCategories で " +
            "Any/All 判定に使う。プロジェクト固有名はここに置き、パッケージにはハードコードしない（依存ゼロ維持）。")]
        [SerializeField]
        string[] categories;

        [Header("Motion Profile (source 不在時の既定スタイル)")]
        [Tooltip("source（SuctionSource）が profile を渡さない場合に使う既定の吸引スタイル。debug 自走や旧経路用の " +
            "フォールバック。通常はノズル側 SuctionSource.MotionProfile が優先され、銃口 1 箇所でまとめて調整する。")]
        [SerializeField]
        SuctionMotionProfile fallbackProfile = new SuctionMotionProfile();

        [Header("Mesh & Playback")]
        [Tooltip("commit 時のメッシュ細分化レベル。低ポリ剛体がシャードに見えるのを和らげる。渦/曲げを効かせるなら 1 以上推奨。重いので最大3。")]
        [SerializeField]
        [Range(0, 3)]
        int subdivisions;

        [Tooltip("逆流（戻し）の再生速度倍率。1 で同速、大きいほど素早く戻る。")]
        [SerializeField]
        [Min(0.1f)]
        float backflowSpeedMultiplier = 1.5f;

        [Tooltip("毎フレーム法線を再計算する。見た目は良くなるが重い。frame time 計測時にオフで差分を見る。")]
        [SerializeField]
        bool recalculateNormals = true;

        [Tooltip("吸い込み完了で GameObject を破棄する。オフなら非表示にして検証を繰り返せる。")]
        [SerializeField]
        bool destroyOnComplete = true;

        [Tooltip("対象固有の最短吸引アニメーション時間（秒）。0 なら Motion Profile の時間だけを使う。" +
            "小さな対象でも変形を視認させたい場合に設定する。")]
        [SerializeField]
        [Min(0f)]
        float minimumEffectDuration;

        [Header("State (任意: 状態で吸える/吸えないを切替)")]
        [Tooltip("オフにすると吸えない（gate を満たしても Failed）。game 側がスタン等の状態で出し入れする。既定 true。")]
        [SerializeField]
        bool suckable = true;

        [Header("Per-State VFX (任意: 状態遷移時に spawn する prefab)")]
        [Tooltip("開始時に対象位置へ spawn。掴まれた合図。")]
        [SerializeField]
        GameObject vfxOnStarted;

        [Tooltip("吸引開始時にノズル位置へ spawn。漏斗ストリーム等。")]
        [SerializeField]
        GameObject vfxOnAbsorbing;

        [Tooltip("失敗（拒否）時に対象位置へ spawn。resist 合図。")]
        [SerializeField]
        GameObject vfxOnFailed;

        [Tooltip("逆流開始時に対象位置へ spawn。")]
        [SerializeField]
        GameObject vfxOnBackflow;

        [Tooltip("完了時にノズル位置へ spawn。pop / flash。")]
        [SerializeField]
        GameObject vfxOnCompleted;

        [Header("Debug Self-Drive (検証用: source なしで自走)")]
        [Tooltip("オンにすると SuctionSource 無しで Idle 中に自動 commit する。destroyOnComplete=false ならループ再生して目視・計測しやすい。本番の駆動は SuctionSource が行う。")]
        [SerializeField]
        bool debugSelfDrive;

        [Tooltip("Debug Self-Drive 時の吸い込み先ノズル。")]
        [SerializeField]
        Transform debugNozzle;

        [Tooltip("Debug Self-Drive 時に与える吸引パワー（ctx.power 相当）。")]
        [SerializeField]
        [Min(0f)]
        float debugPower = 1f;

        [Header("Events (状態ごとのエフェクト hook)")]
        public UnityEvent OnAbsorbStarted = new UnityEvent();
        public UnityEvent OnAbsorbing = new UnityEvent();
        public UnityEvent OnAbsorbFailed = new UnityEvent();
        public UnityEvent OnAbsorbBackflow = new UnityEvent();
        public UnityEvent OnAbsorbCompleted = new UnityEvent();

        AbsorbState state = AbsorbState.Idle;

        bool initialized;
        MeshFilter meshFilter;
        Mesh originalSharedMesh;
        Mesh runtimeMesh;
        Vector3[] baseVertices;
        Vector3[] workVertices;
        float[] s0;
        float[] u0;
        Vector3[] lat0;

        // 掴みビート(#3)を吸引速度に比例させる係数。startWindup を下限とし effectiveDuration に掛ける。
        const float k_windupFraction = 0.13f;
        // ~10°。これを超えると nozzle.forward が axis0 と平行扱い → 弧方向を sagDir へ退避する閾値。
        const float k_hoseParallelThreshold = 0.985f;

        // commit 時に確定する有効なスタイル（source から渡る profile か fallbackProfile）。ApplyWarp/CommitCapture が読む。
        SuctionMotionProfile activeProfile;

        Transform activeNozzle;
        Vector3 fallbackNozzlePos;
        Vector3 origin;
        Vector3 axis0;
        Vector3 sagDir;
        Vector3 shakePerpB;
        Vector3 hoseDirFrame;
        Vector3 shakeAxisA;
        Vector3 shakeAxisB;
        float objShakePhase;
        float objShakeFreqMul;
        float maxLat;
        float commitLength;
        float effectiveDuration;
        Matrix4x4 worldToLocalAtCommit;
        float elapsed;
        float windupRemaining;

        Rigidbody cachedBody;
        bool prevIsKinematic;
        bool prevDetectCollisions;
        Collider[] cachedColliders;
        bool[] prevColliderEnabled;
        readonly List<Behaviour> frozenDrivers = new List<Behaviour>();

        public AbsorbState State => state;

        public bool IsBusy =>
            state == AbsorbState.Starting || state == AbsorbState.Absorbing || state == AbsorbState.Backflow;

        /// <summary>
        /// Power threshold this target needs to be absorbed (<see cref="SuctionContext.Power"/> must reach it).
        /// Exposed so callers can configure suckable / too-strong targets from code. Clamped to &gt;= 0.
        /// </summary>
        public float RequiredSuctionPower
        {
            get => requiredSuctionPower;
            set => requiredSuctionPower = Mathf.Max(0f, value);
        }

        /// <summary>
        /// Alias of <see cref="RequiredSuctionPower"/> in resistance terms. Higher = harder to suck.
        /// Lets game code reason about "how strongly the object resists" without a rename of the gate field.
        /// </summary>
        public float SuctionResistance
        {
            get => RequiredSuctionPower;
            set => RequiredSuctionPower = value;
        }

        /// <summary>
        /// 対象固有の最短吸引アニメーション時間（秒）。0 の場合は Motion Profile の時間だけを使う。
        /// </summary>
        public float MinimumEffectDuration
        {
            get => minimumEffectDuration;
            set => minimumEffectDuration = Mathf.Max(0f, value);
        }

        /// <summary>
        /// Whether this target can currently be sucked at all. When false, even a sufficient-power context is
        /// rejected (Failed). Game code can flip this with state (e.g. only suckable while stunned). Default true.
        /// </summary>
        public bool Suckable
        {
            get => suckable;
            set => suckable = value;
        }

        /// <summary>このターゲットが属するカテゴリ（読み取り専用）。ノズル側のカテゴリフィルタが参照する。空配列もあり得る。</summary>
        public IReadOnlyList<string> Categories => categories;

        /// <summary>
        /// 吸引の進捗 0..1（commit から完了まで）。Idle/Failed では 0、Completed では 1。Absorbing/Backflow 中は
        /// elapsed/有効長。VFX/UI 連動用の読み取り専用値。
        /// </summary>
        public float Progress => effectiveDuration > 1e-5f ? Mathf.Clamp01(elapsed / effectiveDuration) : 0f;

        void Awake()
        {
            EnsureInitialized();
        }

        void Update()
        {
            Step(Time.deltaTime);
        }

        void OnDestroy()
        {
            // Instantiate したインスタンスメッシュは自動破棄されないので明示的に解放する
            DestroySafe(runtimeMesh);
        }

        public bool TryBeginAbsorb(in SuctionContext ctx)
        {
            return TryBeginAbsorb(ctx, null);
        }

        public bool TryBeginAbsorb(in SuctionContext ctx, SuctionMotionProfile profile)
        {
            EnsureInitialized();

            if (!CanBeSucked(ctx))
            {
                EnterFailed();
                return false;
            }

            // source の profile を優先。無ければ対象側の fallback（defensive に null も補完）。
            if (fallbackProfile == null)
            {
                fallbackProfile = new SuctionMotionProfile();
            }

            activeProfile = profile ?? fallbackProfile;

            CommitCapture(ctx);
            FreezeDynamics();
            elapsed = 0f;
            // 掴みビートは吸引速度に比例（最低 startWindup）。CommitCapture で effectiveDuration が確定済み。
            windupRemaining = Mathf.Max(activeProfile.startWindup, effectiveDuration * k_windupFraction);
            EnterStarting();
            return true;
        }

        public void Interrupt()
        {
            // 中断＝逆流。Starting/Absorbing 中のみ反応し、中間で止まらず逆流→Idle に解決する。
            if (state == AbsorbState.Starting || state == AbsorbState.Absorbing)
            {
                EnterBackflow();
            }
        }

        // EditMode テストから状態機械を決定論的に進めるためのフック（Update は Time.deltaTime を使うため）。
        internal void TickForTest(float deltaTime)
        {
            Step(deltaTime);
        }

        void Step(float deltaTime)
        {
            switch (state)
            {
                case AbsorbState.Idle:
                    TickIdle();
                    break;
                case AbsorbState.Starting:
                    TickStarting(deltaTime);
                    break;
                case AbsorbState.Absorbing:
                    TickAbsorbing(deltaTime);
                    break;
                case AbsorbState.Backflow:
                    TickBackflow(deltaTime);
                    break;
            }
        }

        void TickIdle()
        {
            if (!debugSelfDrive || debugNozzle == null)
            {
                return;
            }

            Vector3 nozzlePos = debugNozzle.position;
            var aim = new Ray(nozzlePos, transform.position - nozzlePos);
            var ctx = new SuctionContext(debugNozzle, nozzlePos, aim, debugPower, 0f, true);
            TryBeginAbsorb(ctx);
        }

        void TickStarting(float deltaTime)
        {
            windupRemaining -= deltaTime;
            if (windupRemaining <= 0f)
            {
                EnterAbsorbing();
            }
        }

        void TickAbsorbing(float deltaTime)
        {
            elapsed += deltaTime;
            ApplyWarp();

            if (elapsed >= effectiveDuration)
            {
                EnterCompleted();
            }
        }

        void TickBackflow(float deltaTime)
        {
            elapsed -= deltaTime * backflowSpeedMultiplier;
            if (elapsed <= 0f)
            {
                elapsed = 0f;
                RestoreOriginalMesh();
                UnfreezeDynamics();
                state = AbsorbState.Idle;
                return;
            }

            ApplyWarp();
        }

        void EnterStarting()
        {
            state = AbsorbState.Starting;
            OnAbsorbStarted?.Invoke();
            SpawnVfx(vfxOnStarted, atNozzle: false);
        }

        void EnterAbsorbing()
        {
            state = AbsorbState.Absorbing;
            OnAbsorbing?.Invoke();
            SpawnVfx(vfxOnAbsorbing, atNozzle: true);
        }

        void EnterFailed()
        {
            // 拒否。変形せず resist 合図のみ発火し、すぐ Idle へ戻す。
            state = AbsorbState.Failed;
            OnAbsorbFailed?.Invoke();
            SpawnVfx(vfxOnFailed, atNozzle: false);
            state = AbsorbState.Idle;
        }

        void EnterBackflow()
        {
            state = AbsorbState.Backflow;
            OnAbsorbBackflow?.Invoke();
            SpawnVfx(vfxOnBackflow, atNozzle: false);
        }

        void EnterCompleted()
        {
            state = AbsorbState.Completed;
            OnAbsorbCompleted?.Invoke();
            SpawnVfx(vfxOnCompleted, atNozzle: true);

            if (debugSelfDrive && !destroyOnComplete)
            {
                // 自走ループ: 元へ戻して Idle に置き、次フレームで再び自動吸引する（目視・計測用）
                ResetForLoop();
                return;
            }

            if (destroyOnComplete)
            {
                DestroySafe(gameObject);
            }
            else
            {
                // 検証を繰り返せるよう、破棄せず非表示にして停止状態に置く
                var meshRenderer = GetComponent<Renderer>();
                if (meshRenderer != null)
                {
                    meshRenderer.enabled = false;
                }
            }
        }

        void ResetForLoop()
        {
            RestoreOriginalMesh();
            UnfreezeDynamics();
            elapsed = 0f;
            state = AbsorbState.Idle;
        }

        // 純粋述語（副作用なし）。Failed FX が漏れないよう、外からはこの結果ではなく TryBeginAbsorb を使う前提。
        bool CanBeSucked(in SuctionContext ctx)
        {
            if (!suckable)
            {
                return false;
            }

            if (state != AbsorbState.Idle)
            {
                return false;
            }

            if (ctx.Power < requiredSuctionPower)
            {
                return false;
            }

            if (ctx.MaxDistance > 0f &&
                Vector3.Distance(transform.position, ctx.ResolveNozzlePosition()) > ctx.MaxDistance)
            {
                return false;
            }

            return true;
        }

        // commit 時に頂点ごとの基準（s0/u0/lat0）と commit 時 Transform を固定する。
        // 以後はノズル現在位置と t だけ動かすので、ノズルが横移動しても伸びがブレない。
        void CommitCapture(in SuctionContext ctx)
        {
            // 直前の複製が残っていれば破棄してから、原型を pristine に複製し直す（累積防止・リーク防止）
            DestroySafe(runtimeMesh);

            runtimeMesh = Instantiate(originalSharedMesh);
            runtimeMesh.name = originalSharedMesh.name + " (FunnelWarp)";
            if (subdivisions > 0)
            {
                Subdivide(runtimeMesh, subdivisions);
            }

            runtimeMesh.MarkDynamic();
            meshFilter.sharedMesh = runtimeMesh;

            baseVertices = runtimeMesh.vertices; // 細分化後の元ローカル頂点（共有メッシュは破壊しない）
            int count = baseVertices.Length;
            workVertices = new Vector3[count];
            s0 = new float[count];
            u0 = new float[count];
            lat0 = new Vector3[count];

            activeNozzle = ctx.Nozzle;
            fallbackNozzlePos = ctx.NozzlePosition;
            Vector3 nozzlePos = ctx.ResolveNozzlePosition();

            origin = transform.position;
            Vector3 toNozzle = nozzlePos - origin;
            axis0 = toNozzle.sqrMagnitude < 1e-8f ? transform.up : toNozzle.normalized;
            commitLength = Mathf.Max(0.001f, toNozzle.magnitude);
            // #9: 距離不変の吸引速度。minDuration で近距離の瞬間消去を防ぎ、absorbDuration を上限に遠距離の間延びを防ぐ。
            effectiveDuration = Mathf.Max(
                Mathf.Clamp(commitLength / activeProfile.suctionSpeed, activeProfile.minDuration, activeProfile.absorbDuration),
                minimumEffectDuration);

            // 垂れの向き: 重力(下)を axis0 に直交する平面へ投影。軸がほぼ鉛直なら別の直交軸へ退避。
            Vector3 perp = Vector3.down - Vector3.Dot(Vector3.down, axis0) * axis0;
            sagDir = perp.sqrMagnitude < 1e-5f
                ? Vector3.Cross(axis0, Vector3.forward).normalized
                : perp.normalized;
            shakePerpB = Vector3.Cross(axis0, sagDir); // axis0⊥sagDir かつ両者 unit → unit。震えの第2軸。

            // 吸い込み弧/渦の到達方向。ノズル forward をホース向きとして使う（無ければ aim ray 方向）。
            // axis0 とほぼ平行だと弧が潰れるので sagDir（axis0 直交・unit）へ退避し最小の弧を保証。
            Vector3 hoseRaw = activeNozzle != null ? activeNozzle.forward : ctx.AimRay.direction;
            hoseDirFrame = (hoseRaw.sqrMagnitude < 1e-6f ||
                            Mathf.Abs(Vector3.Dot(hoseRaw.normalized, axis0)) > k_hoseParallelThreshold)
                ? sagDir
                : hoseRaw.normalized;

            // 震えの per-object ランダム（commit 位置から決定論的に派生・commit 1 回。ループでも一貫）。lockstep を解消する。
            int qx = Mathf.RoundToInt(origin.x * 1000f);
            int qy = Mathf.RoundToInt(origin.y * 1000f);
            int qz = Mathf.RoundToInt(origin.z * 1000f);
            uint objSeed = Hash((uint)qx * 73856093u) ^ Hash((uint)qy * 19349663u) ^ Hash((uint)qz * 83492791u);
            objShakePhase = Hash01(objSeed * 747796405u + 1u) * (2f * Mathf.PI);
            objShakeFreqMul = Mathf.Lerp(1f - 0.35f * activeProfile.shakeJitter, 1f + 0.35f * activeProfile.shakeJitter,
                Hash01(objSeed * 2891336453u + 2u));
            float axisAng = Hash01(objSeed * 3266489917u + 3u) * (2f * Mathf.PI);
            // (sagDir, shakePerpB) を平面回転で別の正規直交対へ（新規 cross/normalize 不要）。
            float caShake = Mathf.Cos(axisAng);
            float saShake = Mathf.Sin(axisAng);
            shakeAxisA = sagDir * caShake + shakePerpB * saShake;
            shakeAxisB = sagDir * (-saShake) + shakePerpB * caShake;

            Matrix4x4 localToWorld = transform.localToWorldMatrix;
            worldToLocalAtCommit = transform.worldToLocalMatrix;

            float sMin = float.MaxValue;
            float sMax = float.MinValue;
            float maxLatSq = 0f;
            for (int i = 0; i < count; i++)
            {
                Vector3 worldPos = localToWorld.MultiplyPoint3x4(baseVertices[i]);
                Vector3 rel = worldPos - origin;
                float s = Vector3.Dot(rel, axis0);
                s0[i] = s;
                lat0[i] = rel - s * axis0;
                if (s < sMin)
                {
                    sMin = s;
                }

                if (s > sMax)
                {
                    sMax = s;
                }

                float latSq = lat0[i].sqrMagnitude;
                if (latSq > maxLatSq)
                {
                    maxLatSq = latSq;
                }

            }

            maxLat = Mathf.Sqrt(maxLatSq); // 対象の最大横半径。震え振幅をサイズ非依存にする基準。

            float range = Mathf.Max(1e-5f, sMax - sMin);
            for (int i = 0; i < count; i++)
            {
                u0[i] = Mathf.Clamp01((s0[i] - sMin) / range);
            }
        }

        void ApplyWarp()
        {
            // 全項は進捗の純関数（elapsed→t と per-vertex a のみ）。wall-clock 非依存なので Backflow で逆再生できる。
            float tNorm = Mathf.Clamp01(elapsed / effectiveDuration);
            float rawEase = activeProfile.absorbEase.Evaluate(tNorm);
            // 端点厳密: 冪カーブは代数的に 0/1。override パスは端点を 0/1 に固定して identity/collapse を守る。
            float t = activeProfile.useEaseOverride
                ? (tNorm <= 0f ? 0f : (tNorm >= 1f ? 1f : rawEase))
                : Mathf.Pow(tNorm, activeProfile.pullAccel);
            float denom = Mathf.Max(1e-4f, 1f - activeProfile.stagger);
            Vector3 nozzlePos = ResolveNozzle();
            Vector3 awayDir = -axis0; // #2 リコイルは吸引と逆向き
            float baseAngleRad = activeProfile.vortexRevolutions * 2f * Mathf.PI;
            float snapStart = 1f - activeProfile.snapWindow;

            // 軌道（#7 弧）: 共有 Bezier 制御点を per-frame で 1 回計算。各頂点は自分の P0=axialStart から始まる。
            bool wantCurve = activeProfile.trajectory != SuctionTrajectory.Straight;
            bool wantSpiral = activeProfile.trajectory == SuctionTrajectory.Spiral;
            Vector3 curveCtrl = Vector3.zero;
            float spiralPhaseBase = activeProfile.spiralRevolutions * 2f * Mathf.PI;
            if (wantCurve)
            {
                Vector3 chordMid = 0.5f * (origin + nozzlePos); // 弦中点（commit origin 基準で安定）
                // 到達タンジェントをホース方向へ: 制御点をノズル前方 +hoseDirFrame に出す → B'(1) が -hoseDirFrame ⇒ 口へ滑り込む
                Vector3 hoseLead = nozzlePos + hoseDirFrame * (activeProfile.curveStrength * commitLength);
                curveCtrl = Vector3.Lerp(chordMid, hoseLead, activeProfile.curveBias);
                // #7 弧: 制御点を上へ持ち上げると 2 次 Bezier が山なりの放物線になる（端点は不変）。
                curveCtrl += Vector3.up * (activeProfile.arcLift * commitLength);
            }

            // 吸引中だけ震える（#1/#2）。per-object で位相・速さ・軸をばらして lockstep は解消するが、
            // 揺れは「対象一体のコヒーレントなブレ」にして頂点バラつきによる表面のガサつき/裂けを出さない。
            // 振幅は t 立ち上げ + per-vertex (1-a) で両端 0 に gate（identity/collapse 不変）。位相のみ Time.time。state==Absorbing 限定。
            bool applyShake = activeProfile.shakeAmount > 0f && maxLat > 1e-5f && state == AbsorbState.Absorbing;
            Vector3 shakeOffset = Vector3.zero;
            if (applyShake)
            {
                float shakeEnv = Mathf.Clamp01(t / 0.08f); // 掴み直後に立ち上げ。tail は per-vertex (1-a) で消える。
                float ph = Time.time * activeProfile.shakeFrequency * objShakeFreqMul + objShakePhase; // per-object 速さ+位相
                float amp = activeProfile.shakeAmount * maxLat * shakeEnv;
                // per-object 回転軸で 2 周波。向きは全頂点共通＝一体で揺れるのでメッシュが裂けない（重複頂点も同じに動く）。
                shakeOffset = (shakeAxisA * Mathf.Sin(ph) + shakeAxisB * Mathf.Sin(ph * 1.7f + 1.3f)) * amp;
            }

            for (int i = 0; i < workVertices.Length; i++)
            {
                // (A) #5 stagger（重なり合った動き）→ #6 cross-section smoothstep
                float aLin = Mathf.Clamp01((t - (1f - u0[i]) * activeProfile.stagger) / denom);
                float a = aLin * aLin * (3f - 2f * aLin);

                // (B) #9/#10 snap: 終端 snapWindow で軸方向の収束を二次加速（ゴクッ）
                float aAxial = a;
                float sw = 0f;
                bool inSnap = activeProfile.snapWindow > 0f && a > snapStart;
                if (inSnap)
                {
                    sw = (a - snapStart) / activeProfile.snapWindow;
                    aAxial = snapStart + (1f - snapStart) * (sw * sw);
                }

                // (C) #1 squash(予備)/stretch(吸引)。axialStretch は a=0 で 1（identity）。
                float antPhase = activeProfile.anticipationFraction > 1e-4f ? Mathf.Clamp01(a / activeProfile.anticipationFraction) : 0f;
                float antShape = 4f * antPhase * (1f - antPhase); // a=0 と a>=fraction で 0、中ほどで peak 1
                float axialStretch = Mathf.Max(0.05f, 1f + activeProfile.stretchAmount * a - activeProfile.anticipationSquash * antShape);
                Vector3 axialStart = origin + (s0[i] * axialStretch) * axis0;
                Vector3 axialPoint;
                if (!wantCurve)
                {
                    axialPoint = Vector3.Lerp(axialStart, nozzlePos, aAxial); // Straight（旧来と同一）
                }
                else
                {
                    // Curve: 2 次 Bezier P0=axialStart / P1=curveCtrl(共有) / P2=nozzlePos(live)。端点は P1 の重み 0 で不変。
                    float v = aAxial;
                    float iv = 1f - v;
                    axialPoint = (iv * iv) * axialStart + (2f * iv * v) * curveCtrl + (v * v) * nozzlePos;
                    if (wantSpiral)
                    {
                        // Spiral: 中心線を corkscrew。包絡は両端 0（identity/collapse 不変）、偏向可。位相 +90° で sag と視覚分離。
                        // 偏りは正=ノズル寄り（排水口へ締まる）/ 負=根元寄り（大きく渦を巻いてから細い漏斗でノズルへ）。
                        float env = Mathf.Sin(v * Mathf.PI);
                        if (activeProfile.spiralTipBias > 0f)
                        {
                            env *= Mathf.Lerp(1f, v, activeProfile.spiralTipBias);
                        }
                        else if (activeProfile.spiralTipBias < 0f)
                        {
                            env *= Mathf.Lerp(1f, 1f - v, -activeProfile.spiralTipBias);
                        }

                        float radius = activeProfile.spiralRadius * commitLength * env;
                        float phase = spiralPhaseBase * v + (Mathf.PI * 0.5f);
                        axialPoint += radius * (Mathf.Cos(phase) * sagDir + Mathf.Sin(phase) * shakePerpB);
                    }
                }

                // (D) #2 予備動作リコイル: ノズルと逆へ一度引く一発パルス（a=0 と a>=fraction で 0）
                axialPoint += awayDir * (activeProfile.anticipationDistance * commitLength * antShape);

                // (E) #10 snap overshoot: ゴクッの瞬間ノズルを通り過ぎ、sw=1（a=1）で厳密にノズルへ収束
                if (activeProfile.snapOvershoot > 0f && inSnap)
                {
                    axialPoint = nozzlePos + axis0 * (activeProfile.snapOvershoot * commitLength * Mathf.Sin(sw * Mathf.PI));
                }

                // (F) #7/#9/#6 渦の回転角 = 時間集中 × 先端集中。a=0 か尾 u0=0 で 0（identity）。
                float spin01 = Mathf.Pow(a, activeProfile.vortexTimePower) * Mathf.Pow(u0[i], activeProfile.vortexTipPower);
                float angle = baseAngleRad * spin01;

                // (G) 横倍率 = 収束ゲート × taper(#8) × bulge(#8) × volume(#1) × pinch(#10) × straw(#1/#10)
                // snap 中はゲートを (1-sw)^2 で先に絞り、頭がノズルに着く前にチューブをネックさせる。
                float latGate = inSnap ? (1f - snapStart) * (1f - sw) * (1f - sw) : (1f - a);
                float taper = Mathf.Lerp(1f, activeProfile.endThin, u0[i] * a);
                float bulge = 1f + activeProfile.midBulge * Mathf.Sin(a * Mathf.PI);
                float latVolume = activeProfile.volumePreserve > 0f ? Mathf.Pow(axialStretch, -activeProfile.volumePreserve) : 1f;
                float pinch = 1f - activeProfile.vortexPinch * spin01; // 回転進捗に単調。[1-pinch,1]、a=0 で 1。
                // ストロー感: 吸われ始めた頂点 (a>0) の断面を序盤 (a≈0.3) までに transitThin へ絞り、以降維持。
                // a=0 で 1 なので identity 不変。ゲート (1-a) と違い移送中ずっと細いまま管を通る見た目になる。
                float straw = activeProfile.transitThin < 1f
                    ? Mathf.Lerp(1f, activeProfile.transitThin, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(a / 0.3f)))
                    : 1f;
                float latScale = latGate * taper * bulge * latVolume * pinch * straw;

                // (H) lat0 を axis0 周りに angle 回転（lat0⊥axis0 なので 2 項 Rodrigues で厳密・Quaternion 不要）
                Vector3 lat = lat0[i];
                Vector3 latPerp = Vector3.Cross(axis0, lat); // |latPerp| == |lat|
                float c = Mathf.Cos(angle);
                float sN = Mathf.Sin(angle);
                Vector3 rotLat = lat * c + latPerp * sN;

                // (I) #7 弧: 回転に連動して各シェルを軸へ引き込む（sin(angle*0.5) で angle=0 のとき 0）
                if (activeProfile.vortexInwardBend != 0f)
                {
                    float r = rotLat.magnitude;
                    if (r > 1e-5f)
                    {
                        float bend = activeProfile.vortexInwardBend * r * Mathf.Sin(angle * 0.5f);
                        rotLat -= (rotLat / r) * bend;
                    }
                }

                Vector3 warpedWorld = axialPoint + rotLat * latScale;

                // (J) #7/#8 重力サグ。a=0 で sin=0、a=1 で fade=0（有限傾きで melt）。終盤は渦に負けて消える。
                if (activeProfile.strandSag != 0f)
                {
                    float sagFade = (1f - a) * (1f - a);
                    warpedWorld += sagDir * (activeProfile.strandSag * commitLength * Mathf.Sin(a * Mathf.PI) * sagFade);
                }

                // 震え: 自由端(a→0)ほど強く・ノズル端(a→1)は抑える（collapse 不変）。向きは全頂点共通でガサつかない。
                if (applyShake)
                {
                    warpedWorld += shakeOffset * (1f - a);
                }

                workVertices[i] = worldToLocalAtCommit.MultiplyPoint3x4(warpedWorld);
            }

            runtimeMesh.SetVertices(workVertices);
            runtimeMesh.RecalculateBounds();
            if (recalculateNormals)
            {
                runtimeMesh.RecalculateNormals();
            }
        }

        Vector3 ResolveNozzle()
        {
            return activeNozzle != null ? activeNozzle.position : fallbackNozzlePos;
        }

        void SpawnVfx(GameObject prefab, bool atNozzle)
        {
            if (prefab == null)
            {
                return;
            }

            Vector3 pos = atNozzle ? ResolveNozzle() : transform.position;
            Instantiate(prefab, pos, Quaternion.identity);
        }

        // commit 時に各三角形を 4 分割して頂点密度を上げる。低ポリ剛体が funnel で曲がれるようにする。
        // 頂点共有はしない（簡素化）。同位置の重複頂点は同一 warp 結果になるため割れは出ない。
        static void Subdivide(Mesh mesh, int levels)
        {
            Vector3[] verts = mesh.vertices;
            int[] tris = mesh.triangles;
            for (int l = 0; l < levels; l++)
            {
                SubdivideOnce(ref verts, ref tris);
            }

            mesh.Clear();
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
        }

        static void SubdivideOnce(ref Vector3[] verts, ref int[] tris)
        {
            var newVerts = new List<Vector3>(tris.Length * 2);
            var newTris = new List<int>(tris.Length * 4);
            for (int t = 0; t < tris.Length; t += 3)
            {
                Vector3 a = verts[tris[t]];
                Vector3 b = verts[tris[t + 1]];
                Vector3 c = verts[tris[t + 2]];
                Vector3 ab = (a + b) * 0.5f;
                Vector3 bc = (b + c) * 0.5f;
                Vector3 ca = (c + a) * 0.5f;
                int baseIndex = newVerts.Count;
                newVerts.Add(a);
                newVerts.Add(b);
                newVerts.Add(c);
                newVerts.Add(ab);
                newVerts.Add(bc);
                newVerts.Add(ca);
                int iA = baseIndex, iB = baseIndex + 1, iC = baseIndex + 2;
                int iAB = baseIndex + 3, iBC = baseIndex + 4, iCA = baseIndex + 5;
                newTris.Add(iA); newTris.Add(iAB); newTris.Add(iCA);
                newTris.Add(iAB); newTris.Add(iB); newTris.Add(iBC);
                newTris.Add(iCA); newTris.Add(iBC); newTris.Add(iC);
                newTris.Add(iAB); newTris.Add(iBC); newTris.Add(iCA);
            }

            verts = newVerts.ToArray();
            tris = newTris.ToArray();
        }

        // 決定論的ハッシュ（震えの per-object ランダム位相用）。uint オーバーフローは既定の unchecked で問題なし。
        static uint Hash(uint x)
        {
            x ^= x >> 16;
            x *= 0x7feb352du;
            x ^= x >> 15;
            x *= 0x846ca68bu;
            x ^= x >> 16;
            return x;
        }

        static float Hash01(uint x)
        {
            return (Hash(x) & 0x00FFFFFFu) / 16777215f; // [0,1]
        }

        void RestoreOriginalMesh()
        {
            if (runtimeMesh == null || baseVertices == null)
            {
                return;
            }

            runtimeMesh.SetVertices(baseVertices);
            runtimeMesh.RecalculateBounds();
            if (recalculateNormals)
            {
                runtimeMesh.RecalculateNormals();
            }
        }

        // 物理・Transform 駆動を止める。commit 後に対象が動くと描画がズレるため。
        void FreezeDynamics()
        {
            cachedBody = GetComponent<Rigidbody>();
            if (cachedBody != null)
            {
                prevIsKinematic = cachedBody.isKinematic;
                prevDetectCollisions = cachedBody.detectCollisions;
                cachedBody.linearVelocity = Vector3.zero;
                cachedBody.angularVelocity = Vector3.zero;
                cachedBody.isKinematic = true;
                cachedBody.detectCollisions = false;
            }

            cachedColliders = GetComponentsInChildren<Collider>();
            prevColliderEnabled = new bool[cachedColliders.Length];
            for (int i = 0; i < cachedColliders.Length; i++)
            {
                prevColliderEnabled[i] = cachedColliders[i].enabled;
                cachedColliders[i].enabled = false;
            }

            // Transform を動かす peer（Animator など）も止める。commit 後に Transform が動くと描画がズレるため。
            frozenDrivers.Clear();
            var animators = GetComponentsInChildren<Animator>();
            for (int i = 0; i < animators.Length; i++)
            {
                if (animators[i].enabled)
                {
                    animators[i].enabled = false;
                    frozenDrivers.Add(animators[i]);
                }
            }
        }

        void UnfreezeDynamics()
        {
            if (cachedBody != null)
            {
                cachedBody.isKinematic = prevIsKinematic;
                cachedBody.detectCollisions = prevDetectCollisions;
            }

            if (cachedColliders != null)
            {
                for (int i = 0; i < cachedColliders.Length; i++)
                {
                    if (cachedColliders[i] != null)
                    {
                        cachedColliders[i].enabled = prevColliderEnabled[i];
                    }
                }
            }

            for (int i = 0; i < frozenDrivers.Count; i++)
            {
                if (frozenDrivers[i] != null)
                {
                    frozenDrivers[i].enabled = true;
                }
            }

            frozenDrivers.Clear();
        }

        void EnsureInitialized()
        {
            if (initialized)
            {
                return;
            }

            meshFilter = GetComponent<MeshFilter>();
            // commit のたびに pristine な原型から複製する。meshFilter.sharedMesh 代入後はインスタンスを指すため、
            // ここで原型を 1 度だけ掴んでおかないと再 commit で変形/細分化が累積する。EditMode テストでは Awake が
            // 走らないため、TryBeginAbsorb 冒頭からも呼んで commit 前の pristine を確実に捕まえる。
            if (meshFilter != null)
            {
                originalSharedMesh = meshFilter.sharedMesh;
            }

            initialized = true;
        }

        static void DestroySafe(Object obj)
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
