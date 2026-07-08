using System.Collections.Generic;
using UnityEngine;

namespace Vacuum
{
    /// <summary>
    /// 吸引対象の取得方式。Volume は Point/Spot 範囲、Raycast は <see cref="SuctionContext.AimRay"/> の
    /// 最前面 collider に付いた対象だけを取得する。
    /// </summary>
    public enum SuctionAcquisitionMode { Volume, Raycast }

    /// <summary>吸引範囲の形（Unity Light 類推）。Point=方向不問の球、Spot=方向つき円錐。</summary>
    public enum SuctionShape { Point, Spot }

    /// <summary>カテゴリ許可の合成方法。Any=どれか 1 つ持てば可、All=全て持つ場合のみ可。</summary>
    public enum SuctionFilterMode { Any, All }

    /// <summary>
    /// LOCAL-0032 / LOCAL-0034 ノズル側の吸引ドライバ。呼び出し側ドライバが組んだ <see cref="SuctionContext"/> を
    /// 毎フレーム <see cref="Tick"/> に渡す。設定の主語は「銃口」: 取得方式 / 方向 / 範囲 /
    /// 同時数（<see cref="maxConcurrent"/>）/ 吸引スタイル（<see cref="MotionProfile"/>）をここに集約する。
    /// Volume は <see cref="Physics.OverlapSphere"/>（Spot は円錐角でフィルタ）、Raycast は
    /// <see cref="SuctionContext.AimRay"/> の最前面 collider だけを検出し、成否判定（commit / Failed）を対象へ委譲する。
    /// 進行中の対象を <see cref="maxConcurrent"/> 上限のリストで保持し、
    /// <see cref="Tick"/> は <c>bool consumed</c> を返す。当たった tick は呼び出し側が泡 erase をスキップする。
    /// 中断（トリガ解放 / aim 大外し）＝逆流で、Backflow 中も対象を保持して consumed=true を返す
    /// （逆流中に泡 erase が混入するのを防ぐ）。
    /// </summary>
    public sealed class SuctionSource : MonoBehaviour
    {
        [Tooltip("吸引対象の検出に使う LayerMask。専用 layer に絞ると壁越し誤検出やオクルージョンを減らせる。既定は全 layer（ISuctionTarget の有無で最終フィルタ）。")]
        [SerializeField]
        LayerMask suckMask = ~0;

        [Header("Acquisition (対象の取得方式)")]
        [Tooltip("Volume=Point/Spot 範囲内を取得。Raycast=ctx.AimRay の最前面 collider に付いた対象だけを取得し、" +
            "非対象 collider は遮蔽物として背後をブロックする。")]
        [SerializeField]
        SuctionAcquisitionMode acquisitionMode = SuctionAcquisitionMode.Volume;

        [Header("Shape (吸引範囲の形・Unity Light 風)")]
        [Tooltip("Volume モード用。Point=ノズル周囲 range の球を方向不問で吸う。Spot=range ＋ spotAngle の円錐内（aim 方向）だけ吸う。")]
        [SerializeField]
        SuctionShape shape = SuctionShape.Spot;

        [Tooltip("吸引範囲の半径 (m)。ノズル収束先（ctx の解決ノズル位置）を中心に OverlapSphere する。")]
        [SerializeField]
        [Min(0f)]
        float range = 30f;

        [Tooltip("Spot 時の円錐の全角 (度)。aim 方向からの角度差がこの半分以内の対象だけ吸う。Point では無視。")]
        [SerializeField]
        [Range(1f, 179f)]
        float spotAngle = 50f;

        [Tooltip("同時に吸える対象数の上限 (1..N)。1 なら従来どおり 1 体ずつ。増やすと範囲内を複数同時に吸う。")]
        [SerializeField]
        [Min(1)]
        int maxConcurrent = 1;

        [Tooltip("吸引中に aim が対象からこの角度 (度) 以上外れたら中断＝逆流（Raycast または Volume/Spot）。" +
            "commit 後は collider を切るため raycast でなく角度差で判定する。0 で aim-leave 中断を無効化。" +
            "Volume/Point では方向不問なので無視。")]
        [SerializeField]
        [Range(0f, 90f)]
        float aimLeaveAngle = 30f;

        [Header("Filter (任意: 文字列カテゴリ)")]
        [Tooltip("吸う対象のカテゴリ許可リスト。空なら全対象許可（suckMask のみで絞る）。対象側 SuctionAbsorbable.categories と突き合わせる。" +
            "enemy 等のプロジェクト固有名はここで設定し、パッケージにはハードコードしない（依存ゼロ維持）。")]
        [SerializeField]
        string[] allowedCategories;

        [Tooltip("Any=許可カテゴリのどれか 1 つでも対象が持てば吸う。All=許可カテゴリを全て対象が持つ場合のみ吸う。allowedCategories が空なら無視。")]
        [SerializeField]
        SuctionFilterMode filterMode = SuctionFilterMode.Any;

        [Header("Motion Profile (吸引スタイルを銃口でまとめて設定)")]
        [Tooltip("LOCAL-0034: この銃口が吸う全対象へ commit 時に渡す吸引スタイル（spiral / vortex / shake 等）。" +
            "対象ごとの手付けは不要になり、ここ 1 箇所で feel を調整できる。対象に profile を渡せない旧経路では " +
            "対象側 fallbackProfile が使われる。")]
        [SerializeField]
        SuctionMotionProfile motionProfile = new SuctionMotionProfile();

        // 進行中の対象（Starting/Absorbing/Backflow）。maxConcurrent 上限。Completed→Destroy や Backflow→Idle で解放する。
        readonly List<ISuctionTarget> _committed = new List<ISuctionTarget>();
        // commit 時の照準方向。吸引アニメーションで対象自身が ray 外へ移動しても自己中断しないよう、
        // aim-leave は「対象の現在位置」ではなく「取得時の照準からどれだけ外したか」で判定する。
        readonly Dictionary<ISuctionTarget, Vector3> _committedAimDirections =
            new Dictionary<ISuctionTarget, Vector3>();
        // 保持中に Failed を毎フレーム再発火させないためのデバウンス（トリガ解放で clear し再試行可能にする）。
        readonly HashSet<ISuctionTarget> _failed = new HashSet<ISuctionTarget>();
        // OverlapSphere の再利用バッファ（GC 回避）。範囲内 collider がこれを超えると一部取りこぼすが、実用上十分な広さ。
        readonly Collider[] _overlap = new Collider[64];
        SuctionLocalTextureVfx localTextureVfx;
        bool localTextureVfxResolved;

        /// <summary>この銃口が吸う全対象へ commit 時に渡す吸引スタイル（LOCAL-0034）。null にはならない。</summary>
        public SuctionMotionProfile MotionProfile => motionProfile;

        /// <summary>対象の取得方式。ランタイム生成する source の設定にも使用できる。</summary>
        public SuctionAcquisitionMode AcquisitionMode
        {
            get => acquisitionMode;
            set => acquisitionMode = value;
        }

        /// <summary>進行中の吸引対象が居るか（Starting/Absorbing/Backflow）。</summary>
        public bool HasCommittedTarget => _committed.Count > 0;

        // EditMode テストで committed-lifecycle を検証するためのシーム（OverlapSphere acquire は PlayMode/手動で確認）。
        // 単一対象として読み書きできるよう list の先頭にマップする（既存テスト互換）。
        internal ISuctionTarget CommittedForTest
        {
            get => _committed.Count > 0 ? _committed[0] : null;
            set
            {
                _committed.Clear();
                _committedAimDirections.Clear();
                if (value != null)
                {
                    _committed.Add(value);
                }
            }
        }

        /// <summary>
        /// 1 フレーム分の吸引を駆動する。範囲内の <see cref="ISuctionTarget"/> に触れた（吸えた / 拒否どちらも）、
        /// または進行中対象を保持している tick は <c>true</c>（consumed）を返す。呼び出し側はその tick の泡 erase を skip する。
        /// </summary>
        public bool Tick(in SuctionContext ctx)
        {
            bool hasAimHit = TryResolveAimHit(ctx, out Vector3 aimHitPoint);

            // 1. 進行中リストを更新（消滅/逆流完了を解放、必要なら中断、生存数を数える）。
            bool anyCommittedLive = UpdateCommitted(ctx);

            if (!ctx.Held)
            {
                // トリガ解放: 取得しない。デバウンスを解除。Backflow 中の対象が残っていれば consumed を返し続ける。
                _failed.Clear();
                NotifyLocalTextureVfx(ctx, anyCommittedLive, hasAimHit, aimHitPoint);
                return anyCommittedLive;
            }

            // 2. 選択した取得方式で空き枠ぶんだけ新規に取得する。触れた対象が 1 つでもあれば consumed。
            bool touched = AcquireTargets(ctx);
            bool consumed = anyCommittedLive || touched;
            NotifyLocalTextureVfx(ctx, consumed, hasAimHit, aimHitPoint);
            return consumed;
        }

        /// <summary>
        /// 外部で <see cref="ISuctionTarget.TryBeginAbsorb(in SuctionContext, SuctionMotionProfile)"/> 済みの対象を、この
        /// source の committed lifecycle へ預ける。以後トリガ解放 / aim 大外しでこの source が <see cref="ISuctionTarget.Interrupt"/>
        /// を呼ぶ（＝中断＝逆流）。OverlapSphere で検出されない対象（collider を持たない runtime 生成物など）を
        /// 中断可能にしたいときに使う。acquisition の <c>maxConcurrent</c> 上限には数えない（外部 commit のため）。二重登録は無視。
        /// </summary>
        public void AdoptCommitted(ISuctionTarget target)
        {
            if (target != null && !_committed.Contains(target))
            {
                _committed.Add(target);
            }
        }

        /// <summary>
        /// 外部 commit 済み対象を、取得時の照準方向つきで committed lifecycle へ預ける。
        /// 吸引アニメーションが対象 Transform を ray 外へ動かしても、その移動自体では aim-leave 中断しない。
        /// </summary>
        public void AdoptCommitted(ISuctionTarget target, in SuctionContext ctx)
        {
            AdoptCommitted(target);
            CaptureCommittedAim(target, ctx.AimRay.direction);
        }

        // 進行中リストを更新。消滅（Completed→Destroy）/ Idle 復帰（Backflow 完了）を解放し、必要なら中断する。
        // 生存（Starting/Absorbing/Backflow）が 1 つでもあれば true。
        bool UpdateCommitted(in SuctionContext ctx)
        {
            bool anyLive = false;
            for (int i = _committed.Count - 1; i >= 0; i--)
            {
                ISuctionTarget target = _committed[i];
                if (!IsLive(target))
                {
                    // Completed → Destroy 済み（Unity fake-null）。解放してこの tick から再取得可能にする。
                    _committed.RemoveAt(i);
                    _committedAimDirections.Remove(target);
                    continue;
                }

                if (target.IsBusy)
                {
                    // 中断＝逆流。Interrupt() は Starting/Absorbing のみ反応し Backflow 中は no-op なので保持してよい。
                    if (ShouldInterrupt(ctx, target))
                    {
                        target.Interrupt();
                    }

                    anyLive = true; // 進行中（Backflow 含む）は保持し consumed を返し続ける。
                }
                else
                {
                    // Backflow 完了で Idle に戻った → 解放。
                    _committed.RemoveAt(i);
                    _committedAimDirections.Remove(target);
                }
            }

            return anyLive;
        }

        // 選択した取得方式で ISuctionTarget を maxConcurrent 上限まで取得する。
        bool AcquireTargets(in SuctionContext ctx)
        {
            return acquisitionMode == SuctionAcquisitionMode.Raycast
                ? AcquireRaycastTarget(ctx)
                : AcquireVolumeTargets(ctx);
        }

        // aim ray の最前面 collider だけを調べる。そこに ISuctionTarget が無ければ遮蔽物として背後を取得しない。
        bool AcquireRaycastTarget(in SuctionContext ctx)
        {
            if (_committed.Count >= maxConcurrent)
            {
                return _committed.Count > 0;
            }

            float distance = ctx.MaxDistance > 0f ? ctx.MaxDistance : Mathf.Infinity;
            if (!Physics.Raycast(ctx.AimRay, out RaycastHit hit, distance, suckMask, QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            ISuctionTarget target = hit.collider.GetComponentInParent<ISuctionTarget>();
            if (target == null || !PassesCategory(target))
            {
                return false;
            }

            return TryCommitCandidate(target, ctx);
        }

        // 範囲内（Point=球 / Spot=円錐）の ISuctionTarget を maxConcurrent 上限まで取得する。
        // 範囲内の吸える対象に 1 つでも触れたら true（commit / Failed / 既保持 / デバウンス問わず consumed）。
        bool AcquireVolumeTargets(in SuctionContext ctx)
        {
            if (_committed.Count >= maxConcurrent)
            {
                // 既に上限。取りに行かないが、保持があるので consumed。
                return _committed.Count > 0;
            }

            Vector3 nozzlePos = ctx.ResolveNozzlePosition();
            Vector3 aimDir = ctx.AimRay.direction;
            int count = Physics.OverlapSphereNonAlloc(nozzlePos, range, _overlap, suckMask, QueryTriggerInteraction.Ignore);

            bool touched = false;
            for (int i = 0; i < count && _committed.Count < maxConcurrent; i++)
            {
                Collider col = _overlap[i];
                if (col == null)
                {
                    continue;
                }

                var target = col.GetComponentInParent<ISuctionTarget>();
                if (target == null)
                {
                    continue;
                }

                if (shape == SuctionShape.Spot && !PassesSpot(nozzlePos, aimDir, col.transform.position))
                {
                    continue;
                }

                if (!PassesCategory(target))
                {
                    continue;
                }

                touched |= TryCommitCandidate(target, ctx);
            }

            return touched;
        }

        // 1 つの候補に対する commit 判定（テストから直接呼べる internal seam）。
        // 既保持/デバウンス済みは touched=true で返して再 commit/再 Failed を避ける。空き枠があれば TryBeginAbsorb を試み、
        // 成功で _committed に追加、失敗で _failed に記録する。範囲内の吸える対象に触れたら必ず true を返す（consumed）。
        internal bool TryCommitCandidate(ISuctionTarget target, in SuctionContext ctx)
        {
            if (target == null)
            {
                return false;
            }

            if (_committed.Contains(target))
            {
                return true; // 既に吸引中 — 触れているので consumed。
            }

            if (_failed.Contains(target))
            {
                return true; // 直近で吸えなかった対象。保持中は再 Failed しない（当たってはいる）。
            }

            if (_committed.Count >= maxConcurrent)
            {
                return false; // 枠が無い — この対象には触れていない扱い。
            }

            if (target.TryBeginAbsorb(ctx, motionProfile))
            {
                _committed.Add(target);
                CaptureCommittedAim(target, ctx.AimRay.direction);
            }
            else
            {
                _failed.Add(target);
            }

            return true; // 吸える対象に当たった（commit / Failed どちらも）→ この tick の泡 erase を skip。
        }

        // カテゴリフィルタ。allowedCategories が空なら常に true（suckMask のみで絞る）。対象が SuctionAbsorbable なら
        // その Categories を読む。他の ISuctionTarget 実装はカテゴリ無し扱い（空許可のときだけ通る）。
        internal bool PassesCategory(ISuctionTarget target)
        {
            IReadOnlyList<string> cats = (target as SuctionAbsorbable)?.Categories;
            return MatchesCategories(cats, allowedCategories, filterMode);
        }

        // 純関数のカテゴリ照合（テストから決定論的に検証する）。allowed が空＝全許可。
        // Any: 許可のどれか 1 つを対象が持てば true。All: 許可を全て対象が持つ場合のみ true。
        internal static bool MatchesCategories(IReadOnlyList<string> targetCategories, string[] allowed, SuctionFilterMode mode)
        {
            if (allowed == null || allowed.Length == 0)
            {
                return true;
            }

            if (mode == SuctionFilterMode.Any)
            {
                for (int i = 0; i < allowed.Length; i++)
                {
                    if (Contains(targetCategories, allowed[i]))
                    {
                        return true;
                    }
                }

                return false;
            }

            for (int i = 0; i < allowed.Length; i++)
            {
                if (!Contains(targetCategories, allowed[i]))
                {
                    return false;
                }
            }

            return true;
        }

        static bool Contains(IReadOnlyList<string> list, string value)
        {
            if (list == null)
            {
                return false;
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == value)
                {
                    return true;
                }
            }

            return false;
        }

        // Spot: ノズル→対象ベクトルが aim 方向から spotAngle/2 以内か。
        bool PassesSpot(Vector3 nozzlePos, Vector3 aimDir, Vector3 targetPos)
        {
            Vector3 toTarget = targetPos - nozzlePos;
            if (toTarget.sqrMagnitude < 1e-8f)
            {
                return true; // ノズル直上は方向を定義できないので通す。
            }

            if (aimDir.sqrMagnitude < 1e-8f)
            {
                return true; // aim 方向が無ければ角度フィルタ無効。
            }

            return Vector3.Angle(aimDir, toTarget) <= spotAngle * 0.5f;
        }

        // 中断すべきか。トリガ解放は常に中断。aim-leave は Raycast または Volume/Spot で使う。
        bool ShouldInterrupt(in SuctionContext ctx, ISuctionTarget target)
        {
            if (!ctx.Held)
            {
                return true;
            }

            bool directionMatters = acquisitionMode == SuctionAcquisitionMode.Raycast || shape == SuctionShape.Spot;
            if (directionMatters && aimLeaveAngle > 0f)
            {
                Vector3 committedDirection;
                if (!_committedAimDirections.TryGetValue(target, out committedDirection)
                    && target is Component component && component != null)
                {
                    committedDirection = component.transform.position - ctx.AimRay.origin;
                }

                if (committedDirection.sqrMagnitude > 1e-6f
                    && ctx.AimRay.direction.sqrMagnitude > 1e-6f
                    && Vector3.Angle(ctx.AimRay.direction, committedDirection) > aimLeaveAngle)
                {
                    return true;
                }
            }

            return false;
        }

        void CaptureCommittedAim(ISuctionTarget target, Vector3 direction)
        {
            if (target == null || direction.sqrMagnitude <= 1e-6f)
            {
                return;
            }

            _committedAimDirections[target] = direction.normalized;
        }

        bool TryResolveAimHit(in SuctionContext ctx, out Vector3 hitPoint)
        {
            float distance = ctx.MaxDistance > 0f ? ctx.MaxDistance : Mathf.Infinity;
            if (Physics.Raycast(ctx.AimRay, out RaycastHit hit, distance, suckMask, QueryTriggerInteraction.Ignore))
            {
                hitPoint = hit.point;
                return true;
            }

            hitPoint = default;
            return false;
        }

        void NotifyLocalTextureVfx(in SuctionContext ctx, bool consumed, bool hasAimHit, Vector3 aimHitPoint)
        {
            if (!localTextureVfxResolved)
            {
                localTextureVfx = GetComponent<SuctionLocalTextureVfx>();
                localTextureVfxResolved = true;
            }

            if (localTextureVfx != null)
            {
                localTextureVfx.Tick(ctx, consumed, hasAimHit, aimHitPoint);
            }
        }

        static bool IsLive(ISuctionTarget target)
        {
            if (target == null)
            {
                return false;
            }

            // ISuctionTarget は MonoBehaviour 実装なので、Destroy 済み（Unity fake-null）を Unity の == で判定する。
            // 純 C# のフェイク実装（テスト用）は UnityEngine.Object でないため常に生存扱い。
            if (target is Object unityObject)
            {
                return unityObject != null;
            }

            return true;
        }
    }
}
