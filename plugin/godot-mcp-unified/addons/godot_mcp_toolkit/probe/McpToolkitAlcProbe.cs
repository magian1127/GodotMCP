using System;
using System.IO;
using Godot;

/// <summary>
/// Optional assembly-reload probe for the MCP toolkit's C# build job.
/// Ported from GDEditorBridge's GdEditorBridgeAlcProbe.cs (MIT) — see
/// ATTRIBUTIONS.md in the addon root.
///
/// The engine prints assembly-load-context (ALC) activity to stdout but never
/// writes a marker, so a caller cannot tell whether a build actually
/// hot-reloaded. This node is compiled into the host project's assembly, which
/// means its static <see cref="AlcId"/> is regenerated on every assembly load,
/// and the <see cref="ISerializationListener"/> callbacks fire around the
/// unload. The GDScript side (editor_csharp_build.gd) reads the marker files
/// and turns them into the alc.status verdict.
///
/// It is instantiated and mounted lazily by the GDScript command module, so it
/// registers no editor plugin and needs no Project Settings > Plugins entry.
/// Projects without C# simply never compile this file (no .csproj → the file
/// is inert).
///
/// The written payload is:
///   {"producer":"mcp-toolkit","alc":"&lt;8 hex&gt;","phase":"&lt;phase&gt;","unix_ms":&lt;ms&gt;}
/// with phase one of: loading, loaded, unloading, unloaded, deserialized,
/// load-failed. It is mirrored to res://.godot/ (project-local, git-ignored)
/// and to user:// (survives a project move).
///
/// The class name must stay identical to the file name for Godot to bind it,
/// and it must stay namespace-less (global) for the GDScript-side load() to
/// resolve it the same way across Godot 4.x C# project layouts.
/// </summary>
[Tool]
public partial class McpToolkitAlcProbe : Node, ISerializationListener
{
    private const string MirrorPath = "res://.godot/mcp-toolkit-alc.json";
    private const string UserPath = "user://mcp-toolkit-alc.json";
    private const string Producer = "mcp-toolkit";

    /// <summary>Fresh on every assembly load, so a changed id means a real reload.</summary>
    public static readonly string AlcId = Guid.NewGuid().ToString("N")[..8];

    private static bool _announcedLoad;

    public override void _Ready()
    {
        if (!_announcedLoad)
        {
            _announcedLoad = true;
            Emit("loading");
        }

        Emit("loaded");
    }

    /// <summary>Called by the .NET runtime immediately before the ALC is unloaded.</summary>
    public void OnBeforeSerialize()
    {
        Emit("unloading");
    }

    /// <summary>Called when this instance is restored after an assembly reload.</summary>
    public void OnAfterDeserialize()
    {
        Emit("deserialized");
    }

    private static void Emit(string phase)
    {
        long unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string payload =
            $"{{\"producer\":\"{Producer}\",\"alc\":\"{AlcId}\",\"phase\":\"{phase}\",\"unix_ms\":{unixMs}}}";
        Write(UserPath, payload);
        Write(MirrorPath, payload);
    }

    private static void Write(string resourcePath, string payload)
    {
        try
        {
            string absolute = ProjectSettings.GlobalizePath(resourcePath);
            string? directory = Path.GetDirectoryName(absolute);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(absolute, payload);
        }
        catch (Exception exception)
        {
            GD.PrintErr($"[mcp-toolkit] ALC probe could not write {resourcePath}: {exception.Message}");
        }
    }
}
