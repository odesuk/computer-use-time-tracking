using System.Globalization;
using ComputerUse.TimeTracking.Services;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace ComputerUse.TimeTracking.Reports;

/// <summary>Lays out a <see cref="DailyAppUsageReport"/> as a PDF (doc 03 §2).</summary>
public sealed class AppUsagePdfWriter
{
    private const double Margin = 50;
    private const double FooterHeight = 42;
    private const double RowHeight = 17;
    private const string FontFamily = "Arial";

    private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("en-US");
    private static readonly XColor Accent = XColor.FromArgb(22, 48, 92);
    private static readonly XColor Track = XColor.FromArgb(228, 231, 236);
    private static readonly XColor Muted = XColor.FromArgb(100, 106, 115);
    private static readonly XColor Rule = XColor.FromArgb(210, 214, 220);

    public AppUsagePdfWriter()
    {
        SystemFontResolver.EnsureConfigured();
    }

    public byte[] Render(DailyAppUsageReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        using var document = new PdfDocument();
        document.Info.Title = $"{ProductContext.Current.DisplayName} Daily Activity Report {report.Date:yyyy-MM-dd}";
        document.Info.Subject = report.IncludesTitles
            ? "Active time by application, including window titles and email subjects."
            : "Active time by application (app totals only).";
        document.Info.Author = ProductContext.Current.DisplayName;

        new Layout(document, report).Draw();

        using var output = new MemoryStream();
        document.Save(output);
        return output.ToArray();
    }

    private sealed class Layout
    {
        private readonly PdfDocument _document;
        private readonly DailyAppUsageReport _report;
        private readonly XFont _title = new(FontFamily, 18, XFontStyleEx.Bold);
        private readonly XFont _heading = new(FontFamily, 12.5, XFontStyleEx.Bold);
        private readonly XFont _body = new(FontFamily, 9.5, XFontStyleEx.Regular);
        private readonly XFont _bodyBold = new(FontFamily, 9.5, XFontStyleEx.Bold);
        private readonly XFont _small = new(FontFamily, 8, XFontStyleEx.Regular);
        private readonly XFont _statValue = new(FontFamily, 15, XFontStyleEx.Bold);
        private readonly XFont _badge = new(FontFamily, 10, XFontStyleEx.Bold);

        private PdfPage _page = null!;
        private XGraphics _graphics = null!;
        private double _y;

        public Layout(PdfDocument document, DailyAppUsageReport report)
        {
            _document = document;
            _report = report;
        }

        private double Width => _page.Width.Point - (2 * Margin);

        private double Bottom => _page.Height.Point - Margin - FooterHeight;

        public void Draw()
        {
            NewPage();
            DrawHeader();
            DrawStats();

            if (_report.ActiveSeconds <= 0)
            {
                _y += 10;
                Text("No activity was recorded for this date.", _body, XBrushes.Black, Margin, RowHeight);
            }
            else
            {
                DrawAppTable();
                if (_report.IncludesTitles) DrawDetails();
            }

            _graphics.Dispose();
            DrawFooters();
        }

        private void NewPage()
        {
            _graphics?.Dispose();
            _page = _document.AddPage();
            _page.Size = PageSize.Letter;
            _graphics = XGraphics.FromPdfPage(_page);
            _y = Margin;
        }

        private void EnsureSpace(double height)
        {
            if (_y + height <= Bottom) return;
            NewPage();
        }

        private void DrawHeader()
        {
            Text($"{ProductContext.Current.ShortName} Daily Activity Report", _title, XBrushes.Black, Margin, 26);

            string badge = _report.IsFinal
                ? "Final"
                : $"Partial — as of {_report.GeneratedAt.ToString("HH:mm", DisplayCulture)}";
            XSize badgeSize = _graphics.MeasureString(badge, _badge);
            double badgeWidth = badgeSize.Width + 16;
            double badgeX = Margin + Width - badgeWidth;
            var badgeBrush = new XSolidBrush(_report.IsFinal ? Accent : XColor.FromArgb(176, 112, 0));
            _graphics.DrawRoundedRectangle(badgeBrush, badgeX, Margin + 1, badgeWidth, 18, 6, 6);
            _graphics.DrawString(badge, _badge, XBrushes.White,
                new XRect(badgeX, Margin + 1, badgeWidth, 18), XStringFormats.Center);

            string date = _report.Date.ToDateTime(TimeOnly.MinValue).ToString("dddd, MMMM d, yyyy", DisplayCulture);
            string generated = _report.GeneratedAt.ToString("d MMM HH:mm", DisplayCulture);
            Text($"{date}  ·  {_report.TimeZoneName}  ·  generated {generated}", _body, new XSolidBrush(Muted), Margin, RowHeight + 6);
            HorizontalRule();
            _y += 8;
        }

        private void DrawStats()
        {
            string first = _report.FirstActivity?.ToString("HH:mm", DisplayCulture) ?? "—";
            string last = _report.LastActivity?.ToString("HH:mm", DisplayCulture) ?? "—";
            (string Label, string Value)[] stats =
            {
                ("Active time", DurationText.Format(_report.ActiveSeconds)),
                ("First activity", first),
                ("Last activity", last),
                ("Apps used", _report.AppCount.ToString(DisplayCulture)),
            };

            double cell = Width / stats.Length;
            var muted = new XSolidBrush(Muted);
            for (int i = 0; i < stats.Length; i++)
            {
                double x = Margin + (i * cell);
                _graphics.DrawString(stats[i].Label, _small, muted, new XRect(x, _y, cell, 12), XStringFormats.TopLeft);
                _graphics.DrawString(stats[i].Value, _statValue, XBrushes.Black, new XRect(x, _y + 13, cell, 20), XStringFormats.TopLeft);
            }

            _y += 44;
        }

        private void DrawAppTable()
        {
            EnsureSpace(30 + RowHeight * 3);
            Text("Time by application", _heading, XBrushes.Black, Margin, 22);

            double nameWidth = Width * 0.34;
            double durationWidth = 62;
            double percentWidth = 38;
            double barWidth = Width - nameWidth - durationWidth - percentWidth - 16;

            foreach (AppUsage app in _report.Apps)
            {
                DrawAppRow(app.DisplayName, app.Seconds, app.Share, nameWidth, durationWidth, barWidth, percentWidth);
            }

            if (_report.OtherApps is { } other)
            {
                string label = $"Other ({other.Count} apps)";
                DrawAppRow(label, other.Seconds, other.Share, nameWidth, durationWidth, barWidth, percentWidth);
                EnsureSpace(RowHeight);
                string names = Ellipsize(string.Join(", ", other.DisplayNames), _small, Width - 12);
                Text(names, _small, new XSolidBrush(Muted), Margin + 12, RowHeight);
            }

            _y += 10;
        }

        private void DrawAppRow(string name, long seconds, double share, double nameWidth, double durationWidth, double barWidth, double percentWidth)
        {
            EnsureSpace(RowHeight);
            double x = Margin;
            _graphics.DrawString(Ellipsize(name, _body, nameWidth - 6), _body, XBrushes.Black,
                new XRect(x, _y, nameWidth, RowHeight), XStringFormats.CenterLeft);
            x += nameWidth;
            _graphics.DrawString(DurationText.Format(seconds), _body, XBrushes.Black,
                new XRect(x, _y, durationWidth, RowHeight), XStringFormats.CenterRight);
            x += durationWidth + 8;

            double barHeight = 8;
            double barY = _y + ((RowHeight - barHeight) / 2);
            _graphics.DrawRectangle(new XSolidBrush(Track), x, barY, barWidth, barHeight);
            double filled = Math.Max(share > 0 ? 1.5 : 0, barWidth * Math.Clamp(share, 0, 1));
            if (filled > 0) _graphics.DrawRectangle(new XSolidBrush(Accent), x, barY, filled, barHeight);
            x += barWidth + 8;

            _graphics.DrawString(FormatShare(share), _body, XBrushes.Black,
                new XRect(x, _y, percentWidth, RowHeight), XStringFormats.CenterRight);
            _y += RowHeight;
        }

        private void DrawDetails()
        {
            EnsureSpace(30 + RowHeight * 3);
            Text("Details", _heading, XBrushes.Black, Margin, 22);

            double durationWidth = 62;
            double indent = 14;
            double titleWidth = Width - indent - durationWidth - 8;
            var muted = new XSolidBrush(Muted);

            foreach (AppUsage app in _report.Apps)
            {
                if (app.TopTitles.Count == 0 && app.OtherTitlesCount == 0) continue;

                EnsureSpace(RowHeight * 2 + 4);
                _y += 4;
                _graphics.DrawString($"{Ellipsize(app.DisplayName, _bodyBold, Width - 80)} — {DurationText.Format(app.Seconds)}",
                    _bodyBold, XBrushes.Black, new XRect(Margin, _y, Width, RowHeight), XStringFormats.CenterLeft);
                _y += RowHeight;

                foreach (TitleUsage title in app.TopTitles)
                {
                    IReadOnlyList<string> lines = Wrap(title.Title, _body, titleWidth);
                    EnsureSpace(RowHeight * Math.Min(lines.Count, 3));
                    for (int i = 0; i < lines.Count; i++)
                    {
                        EnsureSpace(RowHeight);
                        _graphics.DrawString(lines[i], _body, XBrushes.Black,
                            new XRect(Margin + indent, _y, titleWidth, RowHeight), XStringFormats.CenterLeft);
                        if (i == 0)
                        {
                            _graphics.DrawString(DurationText.Format(title.Seconds), _body, XBrushes.Black,
                                new XRect(Margin + Width - durationWidth, _y, durationWidth, RowHeight), XStringFormats.CenterRight);
                        }

                        _y += RowHeight - 2;
                    }

                    _y += 2;
                }

                if (app.OtherTitlesCount > 0)
                {
                    EnsureSpace(RowHeight);
                    string label = app.OtherTitlesCount == 1 ? "+ 1 other title" : $"+ {app.OtherTitlesCount} other titles";
                    _graphics.DrawString(label, _body, muted,
                        new XRect(Margin + indent, _y, titleWidth, RowHeight), XStringFormats.CenterLeft);
                    _graphics.DrawString(DurationText.Format(app.OtherTitlesSeconds), _body, muted,
                        new XRect(Margin + Width - durationWidth, _y, durationWidth, RowHeight), XStringFormats.CenterRight);
                    _y += RowHeight;
                }
            }
        }

        // Footers go on after every page exists, so "Page X of Y" knows Y.
        private void DrawFooters()
        {
            string note = $"Idle time over {_report.IdleThresholdMinutes} minutes is excluded. Times are local ({_report.TimeZoneName}).";
            string confidentiality = _report.IncludesTitles
                ? "Contains window titles and email subjects — treat as confidential."
                : "App totals only; window titles and email subjects are not included.";
            var muted = new XSolidBrush(Muted);
            int count = _document.PageCount;
            for (int index = 0; index < count; index++)
            {
                PdfPage page = _document.Pages[index];
                using XGraphics graphics = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                double width = page.Width.Point - (2 * Margin);
                double top = page.Height.Point - Margin - FooterHeight + 14;
                graphics.DrawLine(new XPen(Rule, 0.6), Margin, top, Margin + width, top);
                graphics.DrawString(note, _small, muted, new XRect(Margin, top + 4, width, 11), XStringFormats.TopLeft);
                graphics.DrawString(confidentiality, _small, muted, new XRect(Margin, top + 16, width, 11), XStringFormats.TopLeft);
                graphics.DrawString($"Page {index + 1} of {count}", _small, muted,
                    new XRect(Margin, top + 16, width, 11), XStringFormats.TopRight);
            }
        }

        private void HorizontalRule()
        {
            _graphics.DrawLine(new XPen(Rule, 0.8), Margin, _y, Margin + Width, _y);
        }

        private void Text(string text, XFont font, XBrush brush, double x, double advance)
        {
            _graphics.DrawString(text, font, brush, new XRect(x, _y, Margin + Width - x, advance), XStringFormats.TopLeft);
            _y += advance;
        }

        private string Ellipsize(string text, XFont font, double maxWidth)
        {
            if (_graphics.MeasureString(text, font).Width <= maxWidth) return text;
            const string ellipsis = "…";
            int length = text.Length;
            while (length > 1 && _graphics.MeasureString(text[..length] + ellipsis, font).Width > maxWidth) length--;
            return text[..length].TrimEnd() + ellipsis;
        }

        // Word wrap; words wider than the line (URLs, long file names) are broken by characters.
        private IReadOnlyList<string> Wrap(string text, XFont font, double maxWidth)
        {
            var lines = new List<string>();
            string current = string.Empty;
            foreach (string word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = current.Length == 0 ? word : $"{current} {word}";
                if (_graphics.MeasureString(candidate, font).Width <= maxWidth)
                {
                    current = candidate;
                    continue;
                }

                if (current.Length > 0) lines.Add(current);
                current = word;
                while (current.Length > 1 && _graphics.MeasureString(current, font).Width > maxWidth)
                {
                    int cut = current.Length - 1;
                    while (cut > 1 && _graphics.MeasureString(current[..cut], font).Width > maxWidth) cut--;
                    lines.Add(current[..cut]);
                    current = current[cut..];
                }
            }

            if (current.Length > 0) lines.Add(current);
            if (lines.Count == 0) lines.Add(string.Empty);
            return lines;
        }

        private static string FormatShare(double share)
        {
            double percent = share * 100;
            return percent is > 0 and < 1 ? "<1%" : $"{Math.Round(percent):0}%";
        }
    }
}
