using System.IO;
using PdfSharp.Fonts;

namespace ComputerUse.TimeTracking.Services;

/// <summary>
/// Supplies sans-serif TrueType fonts to PDFsharp on macOS and Linux, where PDFsharp has no
/// built-in font lookup. On Windows, PDFsharp's Core build only reads the installed fonts when
/// <see cref="GlobalFontSettings.UseWindowsFontsUnderWindows"/> is set.
/// </summary>
public sealed class SystemFontResolver : IFontResolver
{
    private const string RegularFace = "TrackerSans";
    private const string BoldFace = "TrackerSans-Bold";
    private static readonly object ConfigurationLock = new();
    private static bool _configured;

    private static readonly string[] RegularCandidates =
    {
        "/System/Library/Fonts/Supplemental/Arial.ttf",
        "/Library/Fonts/Arial.ttf",
        "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        "/usr/share/fonts/truetype/freefont/FreeSans.ttf"
    };

    private static readonly string[] BoldCandidates =
    {
        "/System/Library/Fonts/Supplemental/Arial Bold.ttf",
        "/Library/Fonts/Arial Bold.ttf",
        "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
        "/usr/share/fonts/truetype/freefont/FreeSansBold.ttf"
    };

    private readonly string _regularPath;
    private readonly string? _boldPath;

    private SystemFontResolver(string regularPath, string? boldPath)
    {
        _regularPath = regularPath;
        _boldPath = boldPath;
    }

    public static void EnsureConfigured()
    {
        lock (ConfigurationLock)
        {
            if (_configured) return;
            if (OperatingSystem.IsWindows())
            {
                // Throws if already read or set elsewhere; in that case it is already decided.
                try
                {
                    if (GlobalFontSettings.FontResolver is null) GlobalFontSettings.UseWindowsFontsUnderWindows = true;
                }
                catch (InvalidOperationException)
                {
                }

                _configured = true;
                return;
            }

            string regular = RegularCandidates.FirstOrDefault(File.Exists)
                ?? throw new InvalidOperationException("No supported TrueType font was found for PDF reports.");
            GlobalFontSettings.FontResolver = new SystemFontResolver(regular, BoldCandidates.FirstOrDefault(File.Exists));
            _configured = true;
        }
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        // Every family maps to the one sans-serif face; bold falls back to simulation when missing.
        if (isBold && _boldPath != null) return new FontResolverInfo(BoldFace);
        return new FontResolverInfo(RegularFace, isBold, isItalic);
    }

    public byte[]? GetFont(string faceName) => faceName switch
    {
        BoldFace when _boldPath != null => File.ReadAllBytes(_boldPath),
        RegularFace => File.ReadAllBytes(_regularPath),
        _ => null
    };
}
