namespace Editor.Core;

internal static class EncodingDetector
{
    public static TextEncodingProfile Detect(ReadOnlySpan<byte> sample, bool sampleIsEntireFile, bool detectionEnabled, int ansiCodePage)
    {
        if (sample.Length >= 3 && sample[0] == 0xEF && sample[1] == 0xBB && sample[2] == 0xBF)
            return TextEncodingProfile.Utf8(ansiCodePage, bom: true);
        if (sample.Length >= 2 && sample[0] == 0xFF && sample[1] == 0xFE)
            return TextEncodingProfile.Utf16Le(ansiCodePage);
        if (sample.Length >= 2 && sample[0] == 0xFE && sample[1] == 0xFF)
            return TextEncodingProfile.Utf16Be(ansiCodePage);

        if (!detectionEnabled)
            return TextEncodingProfile.Utf8(ansiCodePage);

        // A BOM-less file is UTF-8 when the examined bytes are strict UTF-8.
        // Large files are classified from the prefix so open stays proportional
        // to the first page. Unmodified save still writes the original bytes.
        if (Utf8Validator.IsValid(sample))
            return TextEncodingProfile.Utf8(ansiCodePage);

        _ = sampleIsEntireFile;
        return TextEncodingProfile.Ansi(ansiCodePage);
    }
}
