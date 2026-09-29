namespace InputSwitcher;

internal sealed class DdcException : Exception
{
    public DdcException(string message) : base(message) { }
}

internal sealed class InputSource
{
    public InputSource(int code, string name)
    {
        Code = code;
        Name = name;
    }

    public int Code { get; }
    public string Name { get; }
    public string HexCode => $"0x{Code:x2}";
    public bool IsHdmi => Name.Contains("hdmi", StringComparison.OrdinalIgnoreCase);
}

internal sealed class Display
{
    public int Number { get; set; }
    public IntPtr Handle { get; init; }
    public string Model { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public string Connector { get; init; } = "";
    public List<InputSource> Inputs { get; set; } = new();
    public int? CurrentCode { get; set; }
    public string InputError { get; set; } = "";
    public bool HasDdc { get; set; }

    public string Title =>
        !string.IsNullOrWhiteSpace(Model) ? Model :
        !string.IsNullOrWhiteSpace(DeviceName) ? DeviceName :
        $"Display {Number}";

    public string CurrentName
    {
        get
        {
            if (CurrentCode is not int code)
                return "Unknown";
            foreach (var source in Inputs)
            {
                if (source.Code == code)
                    return source.Name;
            }
            return InputCatalog.LabelFor(code, Connector);
        }
    }

    /// <summary>How this PC is linked, from Windows — not the monitor's VCP label.</summary>
    public string LinkSummary =>
        !string.IsNullOrWhiteSpace(Connector) ? $"Via {Connector}" : "";
}

internal static class InputCatalog
{
    // VESA MCCS feature 60 labels — used only to name codes the monitor already reported.
    static readonly Dictionary<int, string> Standard = new()
    {
        [0x01] = "VGA-1",
        [0x02] = "VGA-2",
        [0x03] = "DVI-1",
        [0x04] = "DVI-2",
        [0x05] = "Composite video 1",
        [0x06] = "Composite video 2",
        [0x07] = "S-Video-1",
        [0x08] = "S-Video-2",
        [0x09] = "Tuner-1",
        [0x0A] = "Tuner-2",
        [0x0B] = "Tuner-3",
        [0x0C] = "Component video 1",
        [0x0D] = "Component video 2",
        [0x0E] = "Component video 3",
        [0x0F] = "DisplayPort-1",
        [0x10] = "DisplayPort-2",
        [0x11] = "HDMI-1",
        [0x12] = "HDMI-2",
        [0x13] = "HDMI-3",
        [0x14] = "HDMI-4",
    };

    public static string? NameFor(int code) =>
        Standard.TryGetValue(code, out var name) ? name : null;

    public static string Label(int code) => NameFor(code) ?? $"Input {code:X2}";

    /// <summary>
    /// Names a VCP input code. When Windows reports HDMI/DP for this PC's cable but the
    /// monitor returns a legacy VESA label (VGA/DVI/…) for the active code, prefer the
    /// Windows connector name — many panels reuse DVI codes for HDMI ports.
    /// </summary>
    public static string LabelFor(int code, string? windowsConnector)
    {
        var standard = Label(code);
        if (string.IsNullOrWhiteSpace(windowsConnector))
            return standard;
        if (NamesAgree(standard, windowsConnector))
            return PreferNumbered(standard, windowsConnector);
        if (IsDigitalCable(windowsConnector) && IsLegacyVideoLabel(standard))
            return windowsConnector.Trim();
        return standard;
    }

    static bool NamesAgree(string vesaName, string connector)
    {
        var a = vesaName.Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        var b = connector.Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        return a.Contains(b, StringComparison.OrdinalIgnoreCase) ||
               b.Contains(a, StringComparison.OrdinalIgnoreCase) ||
               (a.Contains("HDMI", StringComparison.OrdinalIgnoreCase) &&
                b.Contains("HDMI", StringComparison.OrdinalIgnoreCase)) ||
               (a.Contains("DisplayPort", StringComparison.OrdinalIgnoreCase) &&
                b.Contains("DisplayPort", StringComparison.OrdinalIgnoreCase));
    }

    static string PreferNumbered(string vesaName, string connector) =>
        vesaName.StartsWith(connector, StringComparison.OrdinalIgnoreCase) ? vesaName : vesaName;

    static bool IsDigitalCable(string connector) =>
        connector.Contains("HDMI", StringComparison.OrdinalIgnoreCase) ||
        connector.Contains("DisplayPort", StringComparison.OrdinalIgnoreCase) ||
        connector.Contains("USB-C", StringComparison.OrdinalIgnoreCase);

    static bool IsLegacyVideoLabel(string name) =>
        name.StartsWith("VGA", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("DVI", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Composite", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("S-Video", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Tuner", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Component", StringComparison.OrdinalIgnoreCase);

    public static List<InputSource> Sort(List<InputSource> sources) =>
        sources
            .OrderBy(source => source.IsHdmi ? 0 : 1)
            .ThenBy(source => source.Code)
            .ToList();

    public enum CapsParseResult
    {
        ValuesFound,
        FeaturePresentNoValues,
        FeatureMissing,
    }

    /// <summary>
    /// Reads feature 60 values from a raw DDC capabilities string,
    /// for example <c>vcp(10 12 60(11 12 0F 10))</c> or <c>vcp(14(05 08) 60 AC)</c>.
    /// </summary>
    public static List<InputSource> ParseCapabilitiesInputs(string text) =>
        TryParseCapabilitiesInputs(text, out var sources) == CapsParseResult.ValuesFound
            ? sources
            : new();

    public static CapsParseResult TryParseCapabilitiesInputs(string text, out List<InputSource> sources)
    {
        sources = new();
        if (string.IsNullOrEmpty(text))
            return CapsParseResult.FeatureMissing;

        var vcpInner = FindVcpGroup(text);
        var search = vcpInner ?? text;
        var feature = FindFeature60(search);
        if (feature is null)
            return CapsParseResult.FeatureMissing;

        if (feature.HasValues)
        {
            sources = ParseValueList(feature.Inner);
            return sources.Count > 0 ? CapsParseResult.ValuesFound : CapsParseResult.FeaturePresentNoValues;
        }

        return CapsParseResult.FeaturePresentNoValues;
    }

    static string? FindVcpGroup(string text)
    {
        for (var i = 0; i + 2 < text.Length; i++)
        {
            if (!MatchToken(text, i, "vcp", out var after))
                continue;
            while (after < text.Length && char.IsWhiteSpace(text[after]))
                after++;
            if (after < text.Length && text[after] == '(')
                return ReadGroup(text, after);
        }
        return null;
    }

    sealed class Feature60
    {
        public bool HasValues { get; init; }
        public string Inner { get; init; } = "";
    }

    static Feature60? FindFeature60(string text)
    {
        for (var i = 0; i + 1 < text.Length; i++)
        {
            if (text[i] != '6' || text[i + 1] != '0')
                continue;
            if (i > 0 && IsHex(text[i - 1]))
                continue;
            var after = i + 2;
            if (after < text.Length && IsHex(text[after]))
                continue;

            while (after < text.Length && char.IsWhiteSpace(text[after]))
                after++;
            if (after < text.Length && text[after] == '(')
            {
                var inner = ReadGroup(text, after);
                // Nested groups belong to other features (e.g. 14(05 08)); keep bare tokens only.
                return new Feature60 { HasValues = true, Inner = StripNestedGroups(inner) };
            }

            // Bare "60" with no value list.
            return new Feature60 { HasValues = false };
        }

        return null;
    }

    static string StripNestedGroups(string text)
    {
        var depth = 0;
        var chars = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c == '(')
            {
                depth++;
                continue;
            }
            if (c == ')')
            {
                if (depth > 0)
                    depth--;
                continue;
            }
            if (depth == 0)
                chars.Append(c);
        }
        return chars.ToString();
    }

    static List<InputSource> ParseValueList(string inner)
    {
        var found = new List<InputSource>();
        var seen = new HashSet<int>();
        foreach (var token in Tokenize(inner))
        {
            if (!TryParseHexByte(token, out var code))
                continue;
            if (!seen.Add(code))
                continue;
            found.Add(new InputSource(code, Label(code)));
        }
        return found;
    }

    /// <summary>
    /// Splits capability value lists that brands write with spaces, commas,
    /// 0x prefixes, or trailing h (e.g. <c>11,12,0Fh</c> or <c>0x11 0x12</c>).
    /// </summary>
    static IEnumerable<string> Tokenize(string inner)
    {
        var token = new System.Text.StringBuilder();
        void Flush(List<string> into)
        {
            if (token.Length == 0)
                return;
            into.Add(token.ToString());
            token.Clear();
        }

        var parts = new List<string>();
        foreach (var c in inner)
        {
            if (char.IsWhiteSpace(c) || c is ',' or ';' or '|')
            {
                Flush(parts);
                continue;
            }
            if (c is '(' or ')')
            {
                Flush(parts);
                continue;
            }
            token.Append(c);
        }
        Flush(parts);
        return parts;
    }

    static bool TryParseHexByte(string raw, out int code)
    {
        code = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var hex = raw.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            hex.StartsWith("0X", StringComparison.Ordinal))
            hex = hex[2..];
        else if (hex.StartsWith('x') || hex.StartsWith('X'))
            hex = hex[1..];

        if (hex.EndsWith('h') || hex.EndsWith('H'))
            hex = hex[..^1];

        hex = hex.Trim();
        if (hex.Length is 0 or > 2 || !hex.All(IsHex))
            return false;

        code = Convert.ToInt32(hex, 16);
        return true;
    }

    static bool MatchToken(string text, int index, string token, out int after)
    {
        after = index;
        if (index + token.Length > text.Length)
            return false;
        for (var i = 0; i < token.Length; i++)
        {
            if (char.ToLowerInvariant(text[index + i]) != token[i])
                return false;
        }
        after = index + token.Length;
        if (index > 0 && IsIdent(text[index - 1]))
            return false;
        if (after < text.Length && IsIdent(text[after]))
            return false;
        return true;
    }

    static bool IsIdent(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_';

    static string ReadGroup(string text, int openParen)
    {
        var depth = 0;
        var start = openParen + 1;
        for (var i = openParen; i < text.Length; i++)
        {
            if (text[i] == '(')
                depth++;
            else if (text[i] == ')')
            {
                depth--;
                if (depth == 0)
                    return text[start..i];
            }
        }
        return text[start..];
    }

    static bool IsHex(char c) =>
        c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
}
