# Changelog

All notable changes to this package are documented here.
This project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- Japanese README (`README.ja.md`).
- Package Manager local documentation under `Documentation~`.
- Animated suction demo GIF under `Documentation~/media`.
- AI-agent oriented package navigation (`llms.txt`).
- Repository hygiene files (`.gitignore`, `.gitattributes`) for Unity-generated files and stable text line endings.
- MIT `license` field in `package.json`.

### Changed
- EditMode tests moved from `Tests/EditMode` to the common UPM package layout `Tests/Editor`.

## [0.2.1]

### Added
- `SuctionAcquisitionMode` on `SuctionSource`: `Volume` preserves Point/Spot area acquisition;
  `Raycast` acquires only the first collider hit by `SuctionContext.AimRay`.
- Raycast acquisition treats a non-target collider as an occluder, so targets cannot be acquired
  through walls.
- `SuctionSource.AdoptCommitted(target, ctx)` captures the commit-time aim direction for externally
  started targets such as collider-less runtime blobs.

### Changed
- Aim-leave interruption also applies to Raycast acquisition after commit and compares against the
  commit-time aim direction, so curved suction motion does not interrupt itself.
- The Basic Vacuum Demo now defaults to Raycast acquisition.

## [0.2.0]

Source-centric suction configuration (the nozzle now owns the feel). Backward compatible:
the old `ISuctionTarget.TryBeginAbsorb(in SuctionContext)` overload is preserved.

### Added
- `SuctionMotionProfile` (`[Serializable]`): all suction style settings (speed/timing, stretch/volume,
  vortex, trajectory, anticipation, snap, shake). `SuctionSource` holds one and passes it at commit
  via the new `ISuctionTarget.TryBeginAbsorb(in SuctionContext, SuctionMotionProfile)` overload, so a
  single nozzle setting drives every target. `SuctionContext` is unchanged.
- `SuctionShape` (Point / Spot) on `SuctionSource`, plus `range`, `spotAngle`, and `maxConcurrent`.
  Acquisition moved from a single `Physics.Raycast` to `Physics.OverlapSphere` (Spot adds a cone-angle
  filter); the committed target is generalized to a capped list for multi-target suction.
- Optional lightweight string-category filter: `SuctionAbsorbable.categories` +
  `SuctionSource.allowedCategories` + `SuctionFilterMode` (Any / All). Empty allow-list = pass all.
  Project-specific names stay out of the package (dependency-zero preserved).
- `SuctionAbsorbable`: `Suckable` (gate a target on/off by game state), `SuctionResistance`
  (alias of `RequiredSuctionPower`), `Progress` (0..1, read-only for VFX/UI), `Categories`.
- `fallbackProfile` on `SuctionAbsorbable` for the source-less / legacy path (debug self-drive).
- `SuctionSource.AdoptCommitted(target)`: hand an externally-started absorb into the source's
  committed lifecycle so it gets interrupt-on-release / aim-leave backflow even when it carries no
  collider (e.g. runtime-spawned targets not found by `OverlapSphere`). Not counted against
  `maxConcurrent`.

### Changed
- The `SuctionTrajectory` enum moved from a nested `SuctionAbsorbable` type to the top-level `Vacuum`
  namespace (now owned by `SuctionMotionProfile`).

## [0.1.0]

### Added
- Initial extraction of the generic suction system into a dependency-free package.
- `ISuctionTarget`, `SuctionContext`, `SuctionSource`, `SuctionAbsorbable` (5-state genie-effect
  suction with per-state `UnityEvent` / VFX hooks and funnel-warp mesh deformation).
- `SampleSuctionMover` helper for moving targets.
- EditMode tests for the source lifecycle and absorbable state machine.
- "Basic Vacuum Demo" sample.

Originally developed inside the SprayFoam project (LOCAL-0032), now isolated under
namespace `Vacuum` / assembly `Vacuum.Runtime` with no external dependencies.
