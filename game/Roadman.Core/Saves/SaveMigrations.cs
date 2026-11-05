using System.Text.Json.Nodes;

namespace Roadman.Core.Saves;

/// <summary>
/// The chain of JSON-level migrations that bring an older save up to <see cref="SaveFormat.CurrentVersion"/>, one
/// version at a time: the step registered for version <c>n</c> turns a version-<c>n</c> file into a version-<c>n+1</c>
/// one. <see cref="Default"/> is the game's chain (empty for version 1). Tests build their own with
/// <see cref="Register"/> and pass it to <see cref="SaveReader.Read(string, SaveMigrations)"/>.
/// </summary>
public sealed class SaveMigrations
{
    private readonly Dictionary<int, Func<JsonObject, JsonObject>> _steps = [];
    private bool _readOnly;

    /// <summary>
    /// The game's migrations, read-only (<see cref="Register"/> on it throws). None exist for format version 1; add
    /// later ones in <see cref="CreateDefault"/>.
    /// </summary>
    public static SaveMigrations Default { get; } = CreateDefault();

    /// <summary>The versions that have a step registered, ascending.</summary>
    public IReadOnlyList<int> FromVersions => [.. _steps.Keys.Order()];

    /// <summary>
    /// Registers the step from <paramref name="fromVersion"/> to <paramref name="fromVersion"/> + 1. The step gets
    /// the file's top-level object (it may change and return it, or return a new one); <see cref="Apply"/> then sets
    /// <c>format_version</c> to <paramref name="fromVersion"/> + 1, so the step needn't.
    /// </summary>
    /// <returns>This instance, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="fromVersion"/> is negative or not below <see cref="SaveFormat.CurrentVersion"/>.
    /// </exception>
    /// <exception cref="ArgumentException">A step for <paramref name="fromVersion"/> is already registered.</exception>
    /// <exception cref="InvalidOperationException">This is <see cref="Default"/>.</exception>
    public SaveMigrations Register(int fromVersion, Func<JsonObject, JsonObject> step)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fromVersion);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(fromVersion, SaveFormat.CurrentVersion);
        ArgumentNullException.ThrowIfNull(step);
        if (_readOnly)
        {
            throw new InvalidOperationException("SaveMigrations.Default is read-only; add the game's migrations in CreateDefault");
        }

        if (!_steps.TryAdd(fromVersion, step))
        {
            throw new ArgumentException($"a migration from version {fromVersion} is already registered", nameof(fromVersion));
        }

        return this;
    }

    private static SaveMigrations CreateDefault()
    {
        var migrations = new SaveMigrations();

        // Register each format change here: migrations.Register(1, Version1To2);
        migrations._readOnly = true;
        return migrations;
    }

    /// <summary>
    /// Runs every step from <paramref name="version"/> up to <see cref="SaveFormat.CurrentVersion"/>. A file already
    /// at the current version is returned unchanged.
    /// </summary>
    /// <exception cref="SaveFormatException">
    /// (key path <c>format_version</c>) A step is missing, or a step threw or returned null.
    /// </exception>
    public JsonObject Apply(JsonObject root, int version)
    {
        ArgumentNullException.ThrowIfNull(root);
        for (var from = version; from < SaveFormat.CurrentVersion; from++)
        {
            if (!_steps.TryGetValue(from, out var step))
            {
                throw new SaveFormatException(
                    "format_version",
                    $"'format_version' {version} is older than {SaveFormat.CurrentVersion}, and there is no migration from version {from}");
            }

            JsonObject? next;
            try
            {
                next = step(root);
            }
            catch (Exception ex) when (ex is not SaveFormatException)
            {
                throw new SaveFormatException(
                    "format_version", $"'format_version' the migration from version {from} failed: {ex.Message}", innerException: ex);
            }

            root = next ?? throw new SaveFormatException(
                "format_version", $"'format_version' the migration from version {from} returned nothing");
            root["format_version"] = from + 1;
        }

        return root;
    }
}
