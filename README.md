# Vacuum

Dependency-free generic **mesh suction** for Unity. Aim a nozzle at a mesh target and
pull it into a single point with a stylized genie/vortex deformation, driven by a
deterministic 5-state lifecycle.

- **Package id**: `jp.pokkur.vacuum`
- **Version**: `0.2.1`
- **Assembly**: `Vacuum.Runtime`
- **Namespace**: `Vacuum`
- **Unity**: `6000.4` or newer
- **Dependencies**: none beyond `UnityEngine`
- **Sample**: `Basic Vacuum Demo`

Japanese documentation is available in [README.ja.md](README.ja.md). Package
Manager local documentation is in [Documentation~/index.md](Documentation~/index.md).
AI-agent oriented navigation is available in [llms.txt](llms.txt).

![Vacuum sucking a mesh target into a nozzle](Documentation~/media/vacuum-demo.gif)

## Features

- Source-centric setup: configure acquisition, range, direction, target count,
  category filtering, interruption, and motion style on `SuctionSource`.
- Two acquisition modes:
  - `Raycast`: only the first collider hit by `SuctionContext.AimRay` can be
    acquired; non-target colliders block targets behind them.
  - `Volume`: finds targets inside a Point sphere or Spot cone around the nozzle.
- `SuctionMotionProfile` controls speed, timing, stretch, volume preservation,
  vortex, curve/spiral trajectory, anticipation, snap, and shake from one nozzle.
- `maxConcurrent` supports single-target or multi-target suction.
- Optional string category filtering with `Any` / `All` matching.
- Aim-leave or trigger-release interruption resolves through `Backflow`, restoring
  the target instead of leaving it in an intermediate state.
- `AdoptCommitted(...)` lets externally-started or collider-less targets join the
  source lifecycle so release and aim-leave interruption still work.
- Per-state `UnityEvent` hooks and optional VFX prefabs on `SuctionAbsorbable`.

## Concepts

| Type | Role |
| --- | --- |
| `ISuctionTarget` | Interface for anything that can be suctioned. |
| `SuctionAbsorbable` | Opt-in `MonoBehaviour` for mesh targets. Requires a `MeshFilter`, runs the 5-state lifecycle, performs funnel-warp deformation, exposes events/VFX hooks, and provides runtime gates such as `Suckable`, `SuctionResistance`, `Progress`, and `Categories`. |
| `SuctionContext` | Per-frame nozzle input built by your controller: nozzle transform/position, aim ray, power, max distance, and held state. Style settings are intentionally not stored here. |
| `SuctionMotionProfile` | Serializable motion-style settings passed from the source to targets at commit time. |
| `SuctionSource` | Nozzle-side driver. Owns acquisition mode, Point/Spot shape, range, spot angle, max concurrent targets, aim-leave interruption, motion profile, and optional category filtering. |
| `SampleSuctionMover` | Optional sample helper that oscillates/spins a target while it is not being suctioned. |

## Lifecycle

```text
Idle --(power too low / not suckable)--> Failed --> Idle
Idle --(commit)--> Starting --> Absorbing --(finish)--> Completed --> Destroy or hide
                                  |
                                  +--(release / aim leave)--> Backflow --> Idle
```

After a target commits, it always resolves to either `Completed` or `Backflow`.
`Failed` does not deform the mesh. Failed targets are debounced while the trigger is
held and become retryable after release.

## Quick Start

1. Add `SuctionAbsorbable` to a mesh target.
   - A `MeshFilter` is required.
   - Add a `Collider` if the target should be acquired by `SuctionSource`.
   - Set `requiredSuctionPower` / `SuctionResistance`.
   - Optionally set `categories`, VFX prefabs, events, subdivisions, and playback options.

2. Add `SuctionSource` to your nozzle.
   - Choose `AcquisitionMode`: `Raycast` for reticle-style suction, `Volume` for
     Point/Spot area suction.
   - Set `suckMask`, `range`, `spotAngle`, `maxConcurrent`, and `aimLeaveAngle`.
   - Tune `MotionProfile` once on the nozzle.
   - Optionally set `allowedCategories` and `filterMode`.

3. Drive the source every frame with a `SuctionContext`.

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

        // Tick even when not held so release interruption can resolve through Backflow.
        bool consumed = source.Tick(ctx);

        // When true, this frame touched or is resolving a suction target.
        // Use it to suppress any other beam/erase effect driven by the same trigger.
    }
}
```

## Acquisition Details

### Raycast

`Raycast` mode uses `SuctionContext.AimRay` and `MaxDistance`. It inspects only the
first collider hit by the ray:

- if that collider belongs to an `ISuctionTarget` and passes the category filter,
  the source attempts to commit it;
- if the first collider is not a target, acquisition stops there, so walls and
  other objects naturally occlude targets behind them.

After commit, aim-leave interruption compares the current aim direction with the
direction captured at commit time. This prevents the target's own curved or spiral
suction motion from interrupting itself.

### Volume

`Volume` mode uses `Physics.OverlapSphereNonAlloc` around the resolved nozzle
position and can acquire up to `maxConcurrent` targets.

- `Point`: direction-independent sphere with radius `range`.
- `Spot`: cone filter using `spotAngle` along `SuctionContext.AimRay.direction`.

## External Targets

If a target is started outside normal source acquisition, pass it to the source so
the source can still manage release and aim-leave interruption:

```csharp
if (target.TryBeginAbsorb(ctx, source.MotionProfile))
{
    source.AdoptCommitted(target, ctx);
}
```

Use the overload with `ctx` when you want aim-leave interruption to use the commit
direction. This is useful for runtime-generated or collider-less suction targets.

## Sample

Import **Basic Vacuum Demo** from Package Manager (`Vacuum` -> `Samples` -> `Import`).
The sample includes:

- a runtime scene setup component (`VacuumDemoSetup`);
- a dependency-free nozzle driver (`SampleVacuumNozzle`) using the legacy Input
  Manager;
- several primitive targets, including a moving target and a too-strong target;
- default `Raycast` acquisition so aiming at a target demonstrates occlusion and
  direct-hit behavior.

In Play Mode, hold the left mouse button and aim at a target. Release during suction
to see the `Backflow` path.

## Tests

The package includes EditMode tests under `Tests/Editor` for:

- source committed-target lifecycle and interruption behavior;
- motion profile handoff from `SuctionSource` to targets;
- failed-target debounce and retry after release;
- `maxConcurrent` behavior;
- category matching;
- Raycast acquisition, occlusion, and aim-leave behavior;
- `SuctionAbsorbable` state transitions, progress, interruption, and resistance aliases.

## License

This package is available under the [MIT License](LICENSE.md).
