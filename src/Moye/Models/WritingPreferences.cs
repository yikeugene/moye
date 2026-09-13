namespace Moye.Models;

/// <summary>A reusable writing tool. Width is measured in unscaled page DIP.</summary>
public sealed class WritingPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Pen";
    public InkTool Tool { get; set; } = InkTool.Pen;
    public string Color { get; set; } = "#FF25334A";
    public double Width { get; set; } = 1.7008;
    public double Opacity { get; set; } = 1;
    public bool PressureSensitivity { get; set; } = true;
    public bool Smoothing { get; set; } = true;
    public bool IsFavorite { get; set; } = true;

    public WritingPreset Snapshot() => new()
    {
        Id = Id, Name = Name, Tool = Tool, Color = Color, Width = Width,
        Opacity = Opacity, PressureSensitivity = PressureSensitivity, Smoothing = Smoothing, IsFavorite = IsFavorite
    };
}

/// <summary>Local tool settings, stored separately from notebook documents.</summary>
public sealed class WritingPreferences
{
    public const int CurrentVersion = 1;
    public const double MinimumWidth = .5;
    public const double MaximumWidth = 24;
    public const double MinimumEraserSize = 12;
    public const double MaximumEraserSize = 120;

    public int Version { get; set; } = CurrentVersion;
    public List<WritingPreset> Presets { get; set; } = [];
    public string LastPresetId { get; set; } = "";
    public InkTool EraserTool { get; set; } = InkTool.PointEraser;
    public double EraserSize { get; set; } = 20;
    public bool EraseHighlightOnly { get; set; }
    public bool HoldToStraightenEnabled { get; set; } = true;

    public static WritingPreferences CreateDefault() => new()
    {
        LastPresetId = "default-black-pen",
        Presets =
        [
            new() { Id = "default-black-pen", Name = "Black Pen", Color = "#FF25334A" },
            new() { Id = "default-blue-pen", Name = "Blue Pen", Color = "#FF326AE8" },
            new() { Id = "default-red-pen", Name = "Red Pen", Color = "#FFE36072", Width = 1.3228 },
            new() { Id = "default-yellow-highlighter", Name = "Yellow Highlighter", Tool = InkTool.Highlighter,
                Color = "#FFF4CF58", Width = 11.3386, Opacity = .5, PressureSensitivity = false }
        ]
    };

    public WritingPreferences Snapshot() => new()
    {
        Version = Version,
        Presets = Presets?.Where(p => p is not null).Select(p => p.Snapshot()).ToList() ?? [],
        LastPresetId = LastPresetId,
        EraserTool = EraserTool,
        EraserSize = EraserSize,
        EraseHighlightOnly = EraseHighlightOnly,
        HoldToStraightenEnabled = HoldToStraightenEnabled
    };

    /// <summary>Returns a validated copy; the caller's pending settings remain untouched.</summary>
    public WritingPreferences Normalize()
    {
        var result = Snapshot();
        result.Version = CurrentVersion;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var preset in result.Presets)
        {
            preset.Tool = preset.Tool is InkTool.Pen or InkTool.Highlighter ? preset.Tool : InkTool.Pen;
            var id = preset.Id?.Trim() ?? "";
            if (id.Length == 0 || id.Length > 128 || !ids.Add(id))
            {
                do { id = Guid.NewGuid().ToString("N"); } while (!ids.Add(id));
            }
            preset.Id = id;
            var name = preset.Name?.Trim() ?? "";
            preset.Name = name.Length == 0 ? preset.Tool == InkTool.Highlighter ? "Highlighter" : "Pen" : name[..Math.Min(name.Length, 160)];
            preset.Color = NormalizeColor(preset.Color, preset.Tool == InkTool.Highlighter ? "#FFF4CF58" : "#FF25334A");
            preset.Width = FiniteClamp(preset.Width, MinimumWidth, MaximumWidth, preset.Tool == InkTool.Highlighter ? 11.3386 : 1.7008);
            // WPF renders highlighters with its fixed native half-opacity.
            preset.Opacity = preset.Tool == InkTool.Highlighter ? .5 : FiniteClamp(preset.Opacity, .1, 1, 1);
        }
        if (result.Presets.Count == 0) result.Presets = CreateDefault().Presets;
        var selectedId = result.LastPresetId?.Trim() ?? "";
        result.LastPresetId = result.Presets.Any(p => p.Id == selectedId) ? selectedId : result.Presets[0].Id;
        result.EraserTool = result.EraserTool is InkTool.PointEraser or InkTool.StrokeEraser ? result.EraserTool : InkTool.PointEraser;
        result.EraserSize = FiniteClamp(result.EraserSize, MinimumEraserSize, MaximumEraserSize, 20);
        return result;
    }

    private static double FiniteClamp(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private static string NormalizeColor(string? value, string fallback)
    {
        var color = value?.Trim() ?? "";
        if (color.Length is not (7 or 9) || color[0] != '#' || !color.AsSpan(1).ToString().All(Uri.IsHexDigit)) return fallback;
        return (color.Length == 7 ? "#FF" + color[1..] : color).ToUpperInvariant();
    }
}
