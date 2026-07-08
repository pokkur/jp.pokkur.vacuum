# Vacuum

Unity 向けの依存なし汎用 **メッシュ吸引** パッケージです。ノズルでメッシュ対象を狙い、ジーニー効果や渦巻き風の変形で 1 点へ吸い込みます。挙動は決定的な 5 状態ライフサイクルで管理されます。

- **Package id**: `jp.pokkur.vacuum`
- **Version**: `0.2.1`
- **Assembly**: `Vacuum.Runtime`
- **Namespace**: `Vacuum`
- **Unity**: `6000.4` 以降
- **Dependencies**: `UnityEngine` のみ
- **Sample**: `Basic Vacuum Demo`

英語版は [README.md](README.md) を参照してください。Package Manager 用のローカルドキュメントは [Documentation~/index.ja.md](Documentation~/index.ja.md) にあります。
AI エージェント向けの参照入口は [llms.txt](llms.txt) にあります。

![ノズルがメッシュ対象を吸い込む Vacuum デモ](Documentation~/media/vacuum-demo.gif)

## 主な機能

- 吸引の設定は `SuctionSource` に集約します。取得方式、範囲、方向、同時吸引数、カテゴリフィルタ、中断条件、モーションスタイルをノズル側で管理します。
- 取得方式は 2 種類です。
  - `Raycast`: `SuctionContext.AimRay` が最初に当たった collider の対象だけを取得します。非対象 collider は遮蔽物になり、背後の対象は取得しません。
  - `Volume`: ノズル周囲の Point 球、または Spot 円錐内の対象を取得します。
- `SuctionMotionProfile` で速度、タイミング、伸縮、体積維持、渦、曲線/螺旋軌道、予備動作、スナップ、震えを 1 つのノズル設定として調整できます。
- `maxConcurrent` により、単体吸引と複数同時吸引の両方に対応します。
- 文字列カテゴリによる任意フィルタを `Any` / `All` で指定できます。
- トリガ解放や aim 外れによる中断は `Backflow` に遷移し、対象を途中状態のまま残しません。
- `AdoptCommitted(...)` により、外部で開始した対象や collider を持たない対象も source の中断ライフサイクルへ預けられます。
- `SuctionAbsorbable` は状態ごとの `UnityEvent` と任意 VFX prefab を持ちます。

## 主要型

| Type | 役割 |
| --- | --- |
| `ISuctionTarget` | 吸引可能な対象のインターフェースです。 |
| `SuctionAbsorbable` | メッシュ対象に付ける `MonoBehaviour` です。`MeshFilter` が必要です。5 状態ライフサイクル、メッシュ変形、イベント/VFX、`Suckable`、`SuctionResistance`、`Progress`、`Categories` を提供します。 |
| `SuctionContext` | コントローラが毎フレーム生成するノズル入力です。ノズル transform/位置、aim ray、power、最大距離、held 状態を持ちます。見た目の style はここには入れません。 |
| `SuctionMotionProfile` | source から commit 時に対象へ渡される、吸引モーションの設定です。 |
| `SuctionSource` | ノズル側のドライバです。取得方式、Point/Spot 形状、範囲、spot angle、同時吸引数、aim-leave 中断、motion profile、カテゴリフィルタを持ちます。 |
| `SampleSuctionMover` | 吸引されていない間だけ対象を揺らす、サンプル用の補助コンポーネントです。 |

## ライフサイクル

```text
Idle --(power 不足 / suckable=false)--> Failed --> Idle
Idle --(commit)--> Starting --> Absorbing --(完了)--> Completed --> Destroy または hide
                                  |
                                  +--(解放 / aim 外れ)--> Backflow --> Idle
```

対象が commit された後は、必ず `Completed` または `Backflow` に解決します。`Failed` ではメッシュを変形しません。失敗した対象はトリガ保持中に再 Failed しないようデバウンスされ、トリガ解放後に再試行できます。

## クイックスタート

1. メッシュ対象に `SuctionAbsorbable` を追加します。
   - `MeshFilter` が必要です。
   - `SuctionSource` から取得したい場合は `Collider` も付けます。
   - `requiredSuctionPower` / `SuctionResistance` を設定します。
   - 必要に応じて `categories`、VFX prefab、イベント、subdivisions、再生オプションを設定します。

2. ノズルに `SuctionSource` を追加します。
   - `AcquisitionMode` を選びます。照準直撃型なら `Raycast`、範囲吸引なら `Volume` を使います。
   - `suckMask`、`range`、`spotAngle`、`maxConcurrent`、`aimLeaveAngle` を設定します。
   - `MotionProfile` をノズル側でまとめて調整します。
   - 必要に応じて `allowedCategories` と `filterMode` を設定します。

3. 毎フレーム `SuctionContext` を作り、source へ渡します。

```csharp
using UnityEngine;
using Vacuum;

[RequireComponent(typeof(SuctionSource))]
public sealed class MyNozzle : MonoBehaviour
{
    [SerializeField] Camera aimCamera;
    [SerializeField] float power = 1f;
    [SerializeField] float maxDistance = 30f;

    SuctionSource source;

    void Awake()
    {
        source = GetComponent<SuctionSource>();
    }

    void Update()
    {
        bool held = Input.GetMouseButton(0);
        Ray aim = aimCamera.ScreenPointToRay(Input.mousePosition);

        var ctx = new SuctionContext(
            nozzle: transform,
            nozzlePosition: transform.position,
            aimRay: aim,
            power: power,
            maxDistance: maxDistance,
            held: held);

        // 解放時の中断を Backflow へ進めるため、held=false のフレームでも Tick します。
        bool consumed = source.Tick(ctx);

        // true のフレームは吸引対象に触れている、または吸引/逆流を解決中です。
        // 同じトリガで別の beam/erase 処理を走らせる場合は抑制に使えます。
    }
}
```

## 取得方式の詳細

### Raycast

`Raycast` は `SuctionContext.AimRay` と `MaxDistance` を使います。ray が最初に当たった collider だけを調べます。

- その collider が `ISuctionTarget` を持ち、カテゴリフィルタを通過すれば commit を試みます。
- 最初に当たった collider が対象でなければ、そこで取得を止めます。壁などの遮蔽物越しに背後の対象は吸いません。

commit 後の aim-leave 中断は、対象の現在位置ではなく commit 時に記録した照準方向と現在の照準方向を比較します。これにより、吸引アニメーションで対象自身が曲線や螺旋に沿って移動しても自己中断しません。

### Volume

`Volume` は解決済みノズル位置を中心に `Physics.OverlapSphereNonAlloc` を使い、`maxConcurrent` まで対象を取得します。

- `Point`: `range` 半径の方向不問の球です。
- `Spot`: `SuctionContext.AimRay.direction` に沿った `spotAngle` の円錐です。

## 外部 commit 対象

通常の source 取得以外で対象を開始した場合も、source に預けるとトリガ解放や aim-leave 中断を管理できます。

```csharp
if (target.TryBeginAbsorb(ctx, source.MotionProfile))
{
    source.AdoptCommitted(target, ctx);
}
```

aim-leave 中断で commit 時の照準方向を使いたい場合は、`ctx` 付き overload を使います。runtime 生成物や collider を持たない対象に便利です。

## サンプル

Package Manager から **Basic Vacuum Demo** を import できます（`Vacuum` -> `Samples` -> `Import`）。

サンプル内容:

- 実行時に scene を構築する `VacuumDemoSetup`
- legacy Input Manager だけで動く `SampleVacuumNozzle`
- 移動する対象、吸引 power が足りず失敗する対象を含む primitive ターゲット群
- 遮蔽と直撃判定を確認しやすい default `Raycast` acquisition

Play Mode で左クリックを押しながら対象を狙うと吸引します。吸引中に離すと `Backflow` を確認できます。

## テスト

`Tests/Editor` に EditMode テストがあります。

- source の committed-target ライフサイクルと中断
- `SuctionSource` から対象への motion profile 受け渡し
- Failed のデバウンスとトリガ解放後の再試行
- `maxConcurrent`
- カテゴリ照合
- Raycast 取得、遮蔽、aim-leave
- `SuctionAbsorbable` の状態遷移、progress、中断、resistance alias

## ライセンス

このパッケージは [MIT License](LICENSE.md) で提供されます。
