using System;
using UnityEngine;

namespace Vacuum
{
    /// <summary>吸い込み軌道。経路そのものの曲がり方を選ぶ（断面の渦 vortex とは別軸）。</summary>
    public enum SuctionTrajectory { Straight, Curve, Spiral }

    /// <summary>
    /// LOCAL-0034 吸引モーションのスタイル設定をまとめた値オブジェクト。speed/timing・stretch/volume・vortex・
    /// trajectory・anticipation・snap・shake を 1 つに集約し、ノズル側 <see cref="SuctionSource"/> が 1 個保持して
    /// commit 時に対象へ渡す（<see cref="ISuctionTarget.TryBeginAbsorb(in SuctionContext, SuctionMotionProfile)"/>）。
    /// 主語を「吸われる側」→「銃口」へ移すための入れ物で、これ自体は数式を持たず、<see cref="SuctionAbsorbable"/> の
    /// funnel warp が読み取る。gate（必要パワー）や geometry 依存（subdivisions 等）は対象側に残し、ここには入れない。
    /// フィールドは package 内部からのみ読む（<c>internal</c>）が、<c>[SerializeField]</c> で Inspector には出る。
    /// </summary>
    [Serializable]
    public sealed class SuctionMotionProfile
    {
        [Header("Speed & Timing (#9)")]
        [Tooltip("主吸引速度 (m/秒)。距離/速度で所要時間を決め、近くても遠くても同じ速さで吸われる体感に。大きいほど一気に吸う。")]
        [SerializeField]
        [Min(0.1f)]
        internal float suctionSpeed = 6f;

        [Tooltip("近距離でも下回らない最短アニメ長 (秒)。瞬間消去に見えるのを防ぐ。")]
        [SerializeField]
        [Min(0.01f)]
        internal float minDuration = 0.18f;

        [Tooltip("最長アニメ長/上限 (秒)。実時間は commitLength/suctionSpeed を minDuration〜この値でクランプ。遠距離の間延び防止の天井。")]
        [SerializeField]
        [Min(0.01f)]
        internal float absorbDuration = 0.6f;

        [Tooltip("吸い込み加速度カーブ指数。1=等速、上げるほど穴へ加速して落ちる ease-in。端点が厳密に 0/1。")]
        [SerializeField]
        [Range(1f, 4f)]
        internal float pullAccel = 2.2f;

        [Tooltip("オンで absorbEase を時間カーブに使う。オフなら pullAccel の冪カーブ（推奨・端点 0/1）。")]
        [SerializeField]
        internal bool useEaseOverride;

        [Tooltip("useEaseOverride=true 時の時間カーブ。両端は必ず (0,0)/(1,1) を通すこと。手付けで溜め→加速を強めたい時用。")]
        [SerializeField]
        internal AnimationCurve absorbEase = new AnimationCurve(new Keyframe(0f, 0f, 0f, 0f), new Keyframe(1f, 1f, 2f, 2f));

        [Tooltip("commit 直後の溜め (秒)。掴まれた合図を一瞬置いてから伸び始める (#2)。")]
        [SerializeField]
        [Min(0f)]
        internal float startWindup = 0.08f;

        [Header("Stretch & Volume (#1)")]
        [Tooltip("主動作の軸方向ストレッチ量 (#1)。大きいほど細く長く伸びる。体積保存で横が反比例し細る。")]
        [SerializeField]
        [Range(0f, 1.5f)]
        internal float stretchAmount = 0.6f;

        [Tooltip("体積保存の強さ (#1)。1 で軸ストレッチ/スクワッシュに横が完全反比例（質量感大）、0 で横不変。")]
        [SerializeField]
        [Range(0f, 1f)]
        internal float volumePreserve = 0.5f;

        [Tooltip("伸び/重なり合った動き (#5)。大きいほどノズル寄りから時間差で長く伸びる。snap 窓と尾の到達両立のため既定を下げた。")]
        [SerializeField]
        [Range(0f, 0.95f)]
        internal float stagger = 0.6f;

        [Tooltip("先端の細さ（副動作）。小さいほどノズル側がキュッと細く収束。")]
        [SerializeField]
        [Range(0.01f, 1f)]
        internal float endThin = 0.1f;

        [Tooltip("中ほどの膨らみ（体積感・副動作）。渦が主役なので一段控えめが既定。t=0 では効かない。")]
        [SerializeField]
        [Range(0f, 2f)]
        internal float midBulge = 0.4f;

        [Tooltip("移送中の断面倍率 (#1/#10 ストロー感)。吸われ始めた頂点の断面を早期にこの倍率まで絞り、" +
                 "細い管を通っているように移送する。1 で従来どおり（絞りなし）、小さいほど細いストロー。")]
        [SerializeField]
        [Range(0.05f, 1f)]
        internal float transitThin = 1f;

        [Tooltip("垂れ/うねり（副動作・#7 弧）。重力方向へ弧を描く。終盤は渦に巻き込まれ消える。0 で直線。")]
        [SerializeField]
        [Range(0f, 0.5f)]
        internal float strandSag = 0.18f;

        [Header("Vortex / Swirl (#7 #10)")]
        [Tooltip("渦の総回転数 (1=360°)。巻き込まれる螺旋の巻き量。")]
        [SerializeField]
        [Range(0f, 5f)]
        internal float vortexRevolutions = 0.75f;

        [Tooltip("回転を後半へ寄せる指数 (#9/#6)。大きいほど終盤で一気に巻き上がる。1 で等速回転。")]
        [SerializeField]
        [Range(1f, 6f)]
        internal float vortexTimePower = 2.2f;

        [Tooltip("ノズル寄り (u0→1) ほど回転集中の指数 (#7)。大きいほど先端だけきつく巻く締まる螺旋。1 で均等。")]
        [SerializeField]
        [Range(0.25f, 6f)]
        internal float vortexTipPower = 2f;

        [Tooltip("回転連動で各シェルを軸へ引き込む量 (#7)。漏斗壁が凹む排水口断面に。0 で円筒回転のみ。")]
        [SerializeField]
        [Range(0f, 1f)]
        internal float vortexInwardBend = 0.35f;

        [Tooltip("回転の速い箇所ほど横半径を絞る誇張 (#10)。絞り上げられる見た目を強調。0 で半径一定。")]
        [SerializeField]
        [Range(0f, 0.9f)]
        internal float vortexPinch = 0.35f;

        [Header("Trajectory (#7 弧)")]
        [Tooltip("吸い込み軌道。Straight=直線（旧来・最軽量）/ Curve=掃除機ホースの曲がった先端へ滑り込む緩い弧（既定・nozzle.forward へ到達）/ Spiral=中心線が螺旋を描いて渦巻きながら吸い込む。")]
        [SerializeField]
        internal SuctionTrajectory trajectory = SuctionTrajectory.Curve;

        [Tooltip("弧の強さ（funnel 長比）。ノズル手前へ hose 方向に制御点を出す距離。大きいほど大きく回り込む。0 でほぼ直線。Curve/Spiral で有効。")]
        [SerializeField]
        [Range(0f, 1.5f)]
        internal float curveStrength = 0.35f;

        [Tooltip("制御点を「弦の中点（弧全体を膨らませる）」と「ホース方向の到達点（終端だけ沿わせる）」の間で配分。小さいほど全体が膨らみ、大きいほど終端だけホース先端へ。")]
        [SerializeField]
        [Range(0f, 1f)]
        internal float curveBias = 0.6f;

        [Tooltip("中心線の螺旋巻き数 (1=360°)。断面ツイスト vortexRevolutions とは別物（経路そのものが渦巻く）。同符号で揃えると一本の corkscrew に見える。Spiral で有効。")]
        [SerializeField]
        [Range(0f, 6f)]
        internal float spiralRevolutions = 1.5f;

        [Tooltip("螺旋の最大横半径（funnel 長比）。両端 0・中ほど最大の包絡。大きいほど大きく渦を巻く。0 で Curve と同じ。Spiral で有効。")]
        [SerializeField]
        [Range(0f, 1f)]
        internal float spiralRadius = 0.18f;

        [Tooltip("螺旋包絡の偏り。0=対称、正でノズル寄り（排水口へ締まる感）、負で根元寄り（大きく渦を巻いてから細い漏斗でノズルへ吸い込まれる竜巻シルエット）。両端 0 は維持。Spiral で有効。")]
        [SerializeField]
        [Range(-1f, 1f)]
        internal float spiralTipBias = 0.5f;

        [Tooltip("弧の持ち上げ（funnel 長比）。Bezier 制御点を world 上方向へ持ち上げ、山なりの放物線でノズルへ吸い込まれる。0 で従来どおり。Curve/Spiral で有効。")]
        [SerializeField]
        [Range(0f, 1f)]
        internal float arcLift = 0f;

        [Header("Anticipation (#2)")]
        [Tooltip("予備動作リコイル (#2)。掴まれた瞬間ノズルと逆へ一度引いてから吸う。funnel 長比。0 で無し。")]
        [SerializeField]
        [Range(0f, 0.5f)]
        internal float anticipationDistance = 0.1f;

        [Tooltip("進捗 a のうち予備動作（リコイル+スクワッシュ）に充てる前半割合。小さいほど鋭い引き、大きいほど粘る溜め。")]
        [SerializeField]
        [Range(0.05f, 0.5f)]
        internal float anticipationFraction = 0.22f;

        [Tooltip("予備動作中のスクワッシュ量/柔らかさ (#1)。大きいほどゼリー状に潰れ横に膨らむ。0 で硬い素材。")]
        [SerializeField]
        [Range(0f, 1f)]
        internal float anticipationSquash = 0.35f;

        [Header("Snap / Gulp (#9 #10)")]
        [Tooltip("終端スナップ長（進捗 a の最後の割合・#10/#9）。この区間で収束が急加速しキュッと吸われるゴクッを作る。0 で無し。")]
        [SerializeField]
        [Range(0f, 0.4f)]
        internal float snapWindow = 0.12f;

        [Tooltip("スナップ時ノズルを一瞬通り過ぎる行き過ぎ量（funnel 長比・#10）。a=1 ではノズルに収束し消失は破綻しない。0 で無し。")]
        [SerializeField]
        [Range(0f, 0.5f)]
        internal float snapOvershoot = 0.12f;

        [Header("Shake (#1/#2 吸引中の震え)")]
        [Tooltip("吸引中の小刻みな震え量（対象の横幅比）。掴まれて抵抗するブルブル感。自由端ほど強く、ノズル捕捉端は抑える。0 で震えなし。")]
        [SerializeField]
        [Range(0f, 1f)]
        internal float shakeAmount = 0.25f;

        [Tooltip("震えの速さ。大きいほど細かくブルブルする。")]
        [SerializeField]
        [Min(0f)]
        internal float shakeFrequency = 45f;

        [Tooltip("震えのオブジェクト間ばらつき（0=速さ揃う、1=最大）。各対象の揺れ速さをばらす。位相・軸は常に対象ごとに別なので lockstep は常に解消。揺れは対象一体のブレで表面はガサつかない。")]
        [SerializeField]
        [Range(0f, 1f)]
        internal float shakeJitter = 0.6f;
    }
}
