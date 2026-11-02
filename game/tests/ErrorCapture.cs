using System.Collections.Generic;
using Godot;

namespace Roadman.Tests;

/// <summary>
/// Collects the errors and warnings Godot reports (for example <see cref="GD.PushError(string)"/>) while it is
/// registered with <see cref="OS.AddLogger"/>, so a test can check what was pushed. Godot may call it from any thread.
/// </summary>
public partial class ErrorCapture : Logger
{
    private readonly object _lock = new();
    private readonly List<string> _errors = [];
    private readonly List<string> _errorsOnly = [];

    /// <summary>Godot's <c>ErrorHandlerType</c> for a warning (<c>ERR_HANDLER_WARNING</c>).</summary>
    private const int WarningType = 1;

    /// <summary>Every captured error and warning, as "code rationale".</summary>
    public IReadOnlyList<string> Errors
    {
        get
        {
            lock (_lock)
            {
                return [.. _errors];
            }
        }
    }

    /// <summary>The captured errors without the warnings (every other error type), as "code rationale".</summary>
    public IReadOnlyList<string> ErrorsOnly
    {
        get
        {
            lock (_lock)
            {
                return [.. _errorsOnly];
            }
        }
    }

    public override void _LogError(
        string function,
        string file,
        int line,
        string code,
        string rationale,
        bool editorNotify,
        int errorType,
        Godot.Collections.Array<ScriptBacktrace> scriptBacktraces)
    {
        lock (_lock)
        {
            _errors.Add($"{code} {rationale}");
            if (errorType != WarningType)
            {
                _errorsOnly.Add($"{code} {rationale}");
            }
        }
    }
}
