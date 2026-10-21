namespace FrpDesk;

/// <summary>
/// 一套点缀色主题。做成用户可自由切换的主题而不是锁一个色，
/// 所以这里只描述"切换一次要改哪几个 brush"，不描述任何布局。
/// 项目开了 WinForms，Color 这个名字有两套，一律写全。
/// </summary>
public sealed record ThemePalette(
    string Key,
    string Label,
    AccentSet Light,
    AccentSet Dark)
{
    public AccentSet Resolve(bool isDark) => isDark ? Dark : Light;

    public static readonly ThemePalette Graphite = new("ink", "石墨黑",
        new AccentSet(Hex("23282E"), Hex("1A1E23"), Hex("EDEFF2"), Hex("F5F6F7"), Hex("FFFFFF")),
        new AccentSet(Hex("E6E9EC"), Hex("C3CAD2"), Hex("33383F"), Hex("2A2E34"), Hex("1A1E23")));

    public static readonly ThemePalette Pine = new("pine", "松墨绿",
        new AccentSet(Hex("15624A"), Hex("0F4A38"), Hex("E7F2ED"), Hex("F2F8F5"), Hex("FFFFFF")),
        new AccentSet(Hex("A8DCC6"), Hex("7FC7A8"), Hex("27463C"), Hex("1E3A31"), Hex("0F3327")));

    public static readonly ThemePalette Clay = new("clay", "陶土赭",
        new AccentSet(Hex("A9502C"), Hex("8A3F20"), Hex("F8EEE8"), Hex("FCF5F1"), Hex("FFFFFF")),
        new AccentSet(Hex("EFB79E"), Hex("E09A78"), Hex("4A3227"), Hex("3B2820"), Hex("55220F")));

    public static readonly ThemePalette Iris = new("iris", "鸢尾紫",
        new AccentSet(Hex("4B4494"), Hex("38326F"), Hex("EDECF7"), Hex("F6F5FB"), Hex("FFFFFF")),
        new AccentSet(Hex("C6C1EE"), Hex("A79FE0"), Hex("343152"), Hex("2A2745"), Hex("241F52")));

    public static IReadOnlyList<ThemePalette> All { get; } = [Graphite, Pine, Clay, Iris];

    public static ThemePalette Find(string? key) =>
        All.FirstOrDefault(palette => palette.Key == key) ?? Graphite;

    private static Windows.UI.Color Hex(string value) => ThemeScheme.Hex(value);
}

/// <summary>
/// 一个点缀色主题在一种明暗下的五个值。深色那条规则是整套统一的：
/// 实底按钮改成同色系的浅色、字用同色系的深色，浅底上那套"深底白字"整个反过来。
/// </summary>
public sealed record AccentSet(
    Windows.UI.Color Signal,
    Windows.UI.Color Deep,
    Windows.UI.Color Soft,
    Windows.UI.Color Wash,
    Windows.UI.Color OnSignal);
