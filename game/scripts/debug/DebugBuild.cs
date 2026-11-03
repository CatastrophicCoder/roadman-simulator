using Godot;

namespace Roadman.Debug;

/// <summary>
/// The one debug-build helper of ADR 0016: every debug-only decision (the debug camera, the debug views, the place
/// inspector, debug screenshot flags) asks <see cref="IsDebug"/>. It is the only code that calls
/// <see cref="OS.IsDebugBuild"/>; review checks that nothing else does.
/// </summary>
public static class DebugBuild
{
    /// <summary>
    /// Tests only: when set, <see cref="IsDebug"/> returns it instead of <see cref="OS.IsDebugBuild"/>, so a test can
    /// build the release layout (no <see cref="DebugCamera"/>, no debug views, <c>--player</c> refused) in a debug
    /// build. Internal, because the gdUnit4 tests in <c>tests/</c> are in this same assembly and nothing else may set
    /// it. Reset it to null after the test.
    /// </summary>
    internal static bool? Override { get; set; }

    /// <summary>Whether this is a debug build (<see cref="OS.IsDebugBuild"/>), unless <see cref="Override"/> is set.</summary>
    public static bool IsDebug => Override ?? OS.IsDebugBuild();
}
