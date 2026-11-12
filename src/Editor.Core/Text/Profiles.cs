namespace Editor.Core;

/// <summary>How the document is decoded for display and re-encoded after an edit.</summary>
public readonly record struct TextEncodingProfile
{
    private static readonly byte[] Utf8BomBytes = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] Utf16LeBomBytes = [0xFF, 0xFE];
    private static readonly byte[] Utf16BeBomBytes = [0xFE, 0xFF];

    public TextEncodingKind Kind { get; init; }
    public bool HasBom { get; init; }
    public int AnsiCodePage { get; init; }

    public BufferEncoding PieceEncoding => Kind switch
    {
        TextEncodingKind.Utf16Le => BufferEncoding.Utf16Le,
        TextEncodingKind.Utf16Be => BufferEncoding.Utf16Be,
        TextEncodingKind.Ansi => BufferEncoding.Ansi,
        _ => BufferEncoding.Utf8
    };

    public int BomLength => Kind switch
    {
        TextEncodingKind.Utf8 when HasBom => 3,
        TextEncodingKind.Utf16Le when HasBom => 2,
        TextEncodingKind.Utf16Be when HasBom => 2,
        _ => 0
    };

    public string Label => Kind switch
    {
        TextEncodingKind.Utf8 => HasBom ? "UTF-8 with BOM" : "UTF-8",
        TextEncodingKind.Utf16Le => "UTF-16 LE",
        TextEncodingKind.Utf16Be => "UTF-16 BE",
        TextEncodingKind.Ansi => $"ANSI ({AnsiCodePage})",
        _ => "UTF-8"
    };

    public static TextEncodingProfile Utf8(int ansiCodePage, bool bom = false) => new()
    {
        Kind = TextEncodingKind.Utf8,
        HasBom = bom,
        AnsiCodePage = ansiCodePage
    };

    public static TextEncodingProfile Utf16Le(int ansiCodePage) => new()
    {
        Kind = TextEncodingKind.Utf16Le,
        HasBom = true,
        AnsiCodePage = ansiCodePage
    };

    public static TextEncodingProfile Utf16Be(int ansiCodePage) => new()
    {
        Kind = TextEncodingKind.Utf16Be,
        HasBom = true,
        AnsiCodePage = ansiCodePage
    };

    public static TextEncodingProfile Ansi(int ansiCodePage) => new()
    {
        Kind = TextEncodingKind.Ansi,
        HasBom = false,
        AnsiCodePage = ansiCodePage
    };

    public ReadOnlySpan<byte> Preamble => Kind switch
    {
        TextEncodingKind.Utf8 when HasBom => Utf8BomBytes,
        TextEncodingKind.Utf16Le when HasBom => Utf16LeBomBytes,
        TextEncodingKind.Utf16Be when HasBom => Utf16BeBomBytes,
        _ => ReadOnlySpan<byte>.Empty
    };
}

/// <summary>Detected line endings. Mixed files keep each break until an explicit conversion.</summary>
public readonly record struct LineEndingProfile
{
    public LineEndingKind Preferred { get; init; }
    public bool IsMixed { get; init; }
    public bool IsProvisional { get; init; }

    public string Label => IsMixed
        ? "Mixed"
        : Preferred switch
        {
            LineEndingKind.Lf => "LF",
            LineEndingKind.Cr => "CR",
            LineEndingKind.None => "None",
            _ => "CRLF"
        };

    public string PreferredNewLine => Preferred switch
    {
        LineEndingKind.Lf => "\n",
        LineEndingKind.Cr => "\r",
        _ => "\r\n"
    };

    public static LineEndingProfile Single(LineEndingKind kind) => new()
    {
        Preferred = kind,
        IsMixed = false,
        IsProvisional = false
    };
}
