namespace PerfTuner.Core.Hardware;

public enum ThemeFamily
{
    /// <summary>The six original, soft (rounded) themes.</summary>
    Classic,

    /// <summary>Machined metal themes: square corners, bevelled plates, brushed surfaces.</summary>
    Metal,
}

/// <summary>A colour palette as plain hex strings (no UI types, so it is unit-testable).</summary>
public sealed record ThemeColors(
    string Id,
    string Name,
    ThemeFamily Family,
    bool Dark,
    string Background,
    string Sidebar,
    string Surface,
    string SurfaceAlt,
    string Border,
    string Text,
    string Subtle,
    string Accent,
    string AccentAlt,
    string OnAccent,
    string Good,
    string Warn,
    string Bad);

/// <summary>
/// Every predefined palette: 6 Classic (unchanged since v0.6) and 21 Metal (14 dark, 7 light).
/// Metal palettes meet the contrast rule tested in HardwareTests (text ≥ 7:1, subtle ≥ 4.5:1, on-accent ≥ 4.5:1).
/// </summary>
public static class ThemeCatalog
{
    public static IReadOnlyList<ThemeColors> All { get; } =
    [
        // ---------------------------------------------------------------- Classic
        new("lagoon", "Lagoon", ThemeFamily.Classic, true, "#0F1419", "#0B0F13", "#171E26", "#212A34", "#2A3542", "#E8EEF3", "#8D9AA8",
            "#2BC4B4", "#3D8BFD", "#04201D", "#3DD68C", "#F5B83D", "#F0616D"),
        new("ember", "Ember", ThemeFamily.Classic, true, "#16120F", "#110E0B", "#211B16", "#2C241D", "#3A3027", "#F3ECE6", "#A69A8F",
            "#FF8A3D", "#FF4E6A", "#2A1205", "#7AD67A", "#FFC53D", "#FF5C5C"),
        new("violet", "Violet Night", ThemeFamily.Classic, true, "#121022", "#0D0B1A", "#1B1832", "#252142", "#322C55", "#ECE9FF", "#9C96C2",
            "#9B7BFF", "#E46BFF", "#170B33", "#4FD1A1", "#F7C14B", "#FF6B8B"),
        new("paper", "Paper", ThemeFamily.Classic, false, "#F4F5F7", "#E9ECF1", "#FFFFFF", "#F0F2F6", "#DADFE6", "#1C2330", "#5E6A7C",
            "#2563EB", "#7C3AED", "#FFFFFF", "#16A34A", "#D97706", "#DC2626"),
        new("mint", "Mint", ThemeFamily.Classic, false, "#F1F7F4", "#E3EFE9", "#FFFFFF", "#EDF5F1", "#D0E2D9", "#14261E", "#4F6B5E",
            "#0F9D6E", "#0E7FA8", "#FFFFFF", "#15803D", "#C2780A", "#D33B3B"),
        new("sunset", "Sunset", ThemeFamily.Classic, false, "#FBF4EF", "#F4E6DC", "#FFFFFF", "#FAEFE8", "#EBD7CA", "#2B1D16", "#7A6457",
            "#E5533D", "#D9367A", "#FFFFFF", "#2F9E5B", "#D48A0A", "#C93030"),

        // ---------------------------------------------------------------- Metal · dark
        new("gunmetal", "Gunmetal", ThemeFamily.Metal, true, "#1A1D21", "#141619", "#23272C", "#2D3238", "#3C434B", "#E9EDF1", "#A3ACB6",
            "#5B9BD5", "#8FB8E0", "#0B1520", "#4CC38A", "#F2B54A", "#F0646E"),
        new("titanium", "Titanium", ThemeFamily.Metal, true, "#1C1F22", "#16181A", "#262A2E", "#30353A", "#41474E", "#ECEFF1", "#A8B0B7",
            "#4FD1E0", "#8AE6F0", "#062226", "#5AD08F", "#F5BF4F", "#F26C6C"),
        new("blackchrome", "Black Chrome", ThemeFamily.Metal, true, "#0E0F11", "#09090B", "#17181B", "#202226", "#2E3136", "#F2F4F7", "#A6ABB3",
            "#DDE3EA", "#A9B4C2", "#14181D", "#58D68D", "#F4C152", "#F36A6A"),
        new("carbon", "Carbon", ThemeFamily.Metal, true, "#121212", "#0C0C0C", "#1C1C1C", "#262626", "#363636", "#EFEFEF", "#A8A8A8",
            "#F5D90A", "#FFB020", "#1E1A00", "#5BD08C", "#FFB020", "#FF5F57"),
        new("tungsten", "Tungsten", ThemeFamily.Metal, true, "#1B1A18", "#151412", "#25231F", "#2F2C27", "#413D36", "#F0ECE6", "#AEA79C",
            "#FFB347", "#FF8C42", "#2A1600", "#6CCB8A", "#FFD166", "#FF6B6B"),
        new("cobalt", "Cobalt", ThemeFamily.Metal, true, "#0F1522", "#0B101A", "#17203A", "#1F2A48", "#2C3A60", "#E8EEFF", "#9FADD4",
            "#2563EB", "#00C2FF", "#FFFFFF", "#3DDC97", "#FFC145", "#FF5C7A"),
        new("anodred", "Anodized Red", ThemeFamily.Metal, true, "#1A1214", "#140D0F", "#24181B", "#2E1F23", "#43292F", "#F5EBED", "#B9A3A8",
            "#FF4D5E", "#FF8A5C", "#2B0408", "#55D38E", "#FFC24D", "#FF7A85"),
        new("anodblue", "Anodized Blue", ThemeFamily.Metal, true, "#10161D", "#0B1016", "#182230", "#202C3D", "#2D3D54", "#E9F1FA", "#A0B1C6",
            "#2FA8FF", "#6FE3FF", "#021A2E", "#4FD69C", "#FFC54F", "#FF6B7D"),
        new("anodviolet", "Anodized Violet", ThemeFamily.Metal, true, "#16121E", "#100D17", "#201A2C", "#2A2238", "#3C3150", "#F1ECFA", "#B3A8C8",
            "#B57BFF", "#FF7BD8", "#1A0833", "#55D4A0", "#FFC857", "#FF6F91"),
        new("anodgreen", "Anodized Green", ThemeFamily.Metal, true, "#111814", "#0C120E", "#18231C", "#203026", "#2D4235", "#EAF5EE", "#A3BCAD",
            "#3DE38B", "#C2F261", "#04230F", "#3DE38B", "#FFC94F", "#FF6B6B"),
        new("copper", "Copper", ThemeFamily.Metal, true, "#1A1716", "#141110", "#25201E", "#2F2926", "#433A35", "#F3ECE8", "#B5A8A0",
            "#E8875A", "#F2B279", "#2A1206", "#6BCB8E", "#F4C35A", "#F06A6A"),
        new("bronze", "Bronze", ThemeFamily.Metal, true, "#1A1814", "#14120F", "#25221C", "#2F2B24", "#433D33", "#F2EDE3", "#B3AA98",
            "#CD9B4E", "#E8C27A", "#241703", "#78C98C", "#F2C14E", "#EE6B5E"),
        new("damascus", "Damascus", ThemeFamily.Metal, true, "#141819", "#0F1213", "#1D2325", "#262E30", "#364144", "#E9F0F1", "#A2B2B5",
            "#2EC4B6", "#7DD3C0", "#03201D", "#5CD18F", "#F6C453", "#F2696F"),
        new("rust", "Rust", ThemeFamily.Metal, true, "#1B1512", "#15100D", "#271E19", "#322620", "#47362D", "#F5ECE6", "#B9A69A",
            "#E2622F", "#F59E42", "#200B02", "#79C98A", "#F5B342", "#FF5E5E"),

        // ---------------------------------------------------------------- Metal · light
        new("brushedsteel", "Brushed Steel", ThemeFamily.Metal, false, "#E3E6E9", "#D5D9DD", "#F2F4F6", "#E8EBEE", "#B9C0C7", "#15191E", "#4A535D",
            "#1F5FAD", "#3A86C8", "#FFFFFF", "#17803D", "#A35E00", "#C0262D"),
        new("aluminium", "Aluminium", ThemeFamily.Metal, false, "#ECEEF0", "#DFE2E5", "#FAFBFC", "#F0F2F4", "#C6CCD2", "#16191D", "#4B5460",
            "#0E6E8C", "#1695B5", "#FFFFFF", "#15803D", "#A15C00", "#BE2A2A"),
        new("chrome", "Chrome", ThemeFamily.Metal, false, "#E9ECF0", "#DADFE5", "#F8F9FB", "#EEF1F4", "#BEC6CF", "#101318", "#454E5A",
            "#2B3440", "#56657A", "#FFFFFF", "#127A3A", "#9C5A00", "#B82430"),
        new("brass", "Brass", ThemeFamily.Metal, false, "#EFE9DC", "#E4DCC9", "#FAF6EE", "#F3EDE1", "#D3C7AC", "#211A0C", "#5C5038",
            "#8A6312", "#B5872B", "#FFFFFF", "#2E7D32", "#9A5800", "#B3261E"),
        new("rosegold", "Rose Gold", ThemeFamily.Metal, false, "#F2E8E5", "#E8DAD5", "#FCF7F5", "#F5ECE9", "#D9C2BA", "#24171A", "#634D52",
            "#A94F5E", "#C97B6B", "#FFFFFF", "#2E7D4F", "#9A5A00", "#B3262E"),
        new("verdigris", "Verdigris", ThemeFamily.Metal, false, "#E3EEEA", "#D3E3DD", "#F4F9F7", "#E9F2EF", "#B6CFC6", "#10201B", "#405A51",
            "#1E7A66", "#3FA38A", "#FFFFFF", "#1B7F3B", "#9A5B00", "#B8282E"),
        new("gold", "Gold", ThemeFamily.Metal, false, "#F3EDDC", "#E9E0C6", "#FCF9EF", "#F6F0E0", "#D9CBA3", "#221B07", "#5E5233",
            "#8C6A00", "#C49A1A", "#FFFFFF", "#2C7A35", "#9A5600", "#B3261E"),
    ];
}
