namespace FrpDesk;

/// <summary>
/// 中性色令牌的两套值。浅色那一份是对参照应用逐像素量出来的，深色那一份是它的同族对应：
/// 分层仍然只靠填充差和 1px 轮廓，明暗倒置，冷调保持不变。
/// 点缀色不在这里，见 ThemePalette。
/// </summary>
public static class ThemeScheme
{
    public static IReadOnlyDictionary<string, (Windows.UI.Color Light, Windows.UI.Color Dark)> Neutrals { get; } =
        new Dictionary<string, (Windows.UI.Color Light, Windows.UI.Color Dark)>(StringComparer.Ordinal)
        {
            // 窗口底色（材质不可用时的回退）与主体卡
            ["CanvasBrush"] = (Hex("F6F6F6"), Hex("1B1E23")),
            ["ContentCardBrush"] = (Hex("FFFFFF"), Hex("262A30")),
            ["ContentCardBackdropBrush"] = (Hex("96FFFFFF"), Hex("96262A30")),
            // 卡内子卡与填充带
            ["PaperBrush"] = (Hex("FFFFFF"), Hex("2E333A")),
            ["PaperBackdropBrush"] = (Hex("BFFFFFFF"), Hex("BF2E333A")),
            // 浮层始终不透明，所以没有材质态变体
            ["FloatingSurfaceBrush"] = (Hex("FFFFFF"), Hex("262A30")),
            ["SoftBrush"] = (Hex("F2F3F4"), Hex("3A414A")),
            ["SoftBackdropBrush"] = (Hex("BFF2F3F4"), Hex("BF3A414A")),
            // 选中面：浅底用压暗，深底用提亮
            ["SelectionPillBrush"] = (Hex("E5E5E5"), Hex("4A525C")),
            ["SelectionPillBackdropBrush"] = (Hex("140A1420"), Hex("24FFFFFF")),
            ["TableSelectionBrush"] = (Hex("EDEEEF"), Hex("3F454C")),
            // 日志行
            ["LogSurfaceBrush"] = (Hex("00FAFDFF"), Hex("00FAFDFF")),
            ["LogWarningRowBrush"] = (Hex("FFFDF9"), Hex("3A3428")),
            ["LogErrorRowBrush"] = (Hex("FFFAFA"), Hex("3C2E2E")),
            // 文字三档
            ["InkBrush"] = (Hex("17283A"), Hex("E9EEF3")),
            ["InkSoftBrush"] = (Hex("365066"), Hex("C2CCD6")),
            ["MutedBrush"] = (Hex("586A7A"), Hex("8E9DAB")),
            // 轮廓与控件描边
            ["RuleBrush"] = (Hex("E6E6E6"), Hex("3E454E")),
            ["ControlBorderBrush"] = (Hex("7D90A4"), Hex("93A6B8")),
            ["SelectionBorderBrush"] = (Hex("B1C4D6"), Hex("5D6A78")),
            // 开关轨道与拇指
            ["SwitchTrackOffBrush"] = (Hex("DDE2E8"), Hex("4E5761")),
            ["SwitchThumbBrush"] = (Hex("FFFFFF"), Hex("EDEFF2")),
            // 语义色：深色下必须提亮，否则在深底上读不出来
            ["AlertBrush"] = (Hex("A33C3C"), Hex("E4898B")),
            ["AlertSoftBrush"] = (Hex("F8EAEA"), Hex("432E30")),
            ["WarningBrush"] = (Hex("805914"), Hex("E6BC6A")),
            ["WarningSoftBrush"] = (Hex("F6EFDF"), Hex("3F3726")),
            ["SuccessBrush"] = (Hex("1C704D"), Hex("79C9A2")),
            ["SuccessSoftBrush"] = (Hex("E7F2EB"), Hex("273E35"))
        };

    /// <summary>支持 #RGB / #RRGGBB / #AARRGGBB，只为让令牌表本身可读。</summary>
    public static Windows.UI.Color Hex(string value)
    {
        var text = value.StartsWith('#') ? value[1..] : value;
        var alpha = text.Length == 8 ? Convert.ToByte(text[..2], 16) : (byte)0xFF;
        var offset = text.Length == 8 ? 2 : 0;
        return Windows.UI.Color.FromArgb(
            alpha,
            Convert.ToByte(text.Substring(offset, 2), 16),
            Convert.ToByte(text.Substring(offset + 2, 2), 16),
            Convert.ToByte(text.Substring(offset + 4, 2), 16));
    }
}
