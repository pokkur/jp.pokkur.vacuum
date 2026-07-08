using System.Runtime.CompilerServices;

// Grant the EditMode test assembly access to internal test helpers
// (e.g. SuctionAbsorbable.TickForTest, which advances the warp deterministically in tests).
[assembly: InternalsVisibleTo("Vacuum.Tests.EditMode")]
