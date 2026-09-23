using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using Taste.Interop;
using Taste.Services;

namespace Taste;

public partial class UpdateDialog : Window
{
    // Matches **bold** and `code` spans.
    private static readonly Regex InlinePattern = new(
        @"\*\*(?<bold>.+?)\*\*|`(?<code>[^`]+?)`",
        RegexOptions.Compiled);

    private static readonly Regex BulletLinePattern = new(@"^[-*]\s+", RegexOptions.Compiled);
    private static readonly Regex HeadingLinePattern = new(@"^(#{1,6})\s+", RegexOptions.Compiled);

    // GitHub's auto-generated changelog format: "<description> by @user in <url>" — the
    // link is redundant noise in a short "what's new" summary, so it's dropped.
    private static readonly Regex TrailingByUserLink = new(
        @"\s+by\s+@[\w.-]+\s+in\s+https?://\S+\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A standalone "**Full Changelog**: <url>" line — pure link, no description, so the whole line is dropped.
    private static readonly Regex FullChangelogLine = new(
        @"^\*\*Full Changelog\*\*:?\s*https?://\S+\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BareUrl = new(@"\s*https?://\S+", RegexOptions.Compiled);

    public bool UpdateAccepted { get; private set; }

    public UpdateDialog(UpdateInfo update)
    {
        InitializeComponent();
        Loaded += UpdateDialog_Loaded;
        TitleText.Text = $"Taste Viewer {update.Version} is available";

        NotesFlowDoc.Blocks.Clear();
        if (string.IsNullOrWhiteSpace(update.ReleaseNotes))
        {
            NotesFlowDoc.Blocks.Add(new Paragraph(new Run("No release notes were provided for this version.")));
        }
        else
        {
            foreach (var block in ParseMarkdown(update.ReleaseNotes.Trim()))
                NotesFlowDoc.Blocks.Add(block);
        }
    }

    private void UpdateDialog_Loaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        ShellInterop.SetDarkTitleBar(hwnd);
    }

    private IEnumerable<Block> ParseMarkdown(string markdown)
    {
        foreach (var rawLine in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (FullChangelogLine.IsMatch(line)) continue;

            var headingMatch = HeadingLinePattern.Match(line);
            if (headingMatch.Success)
            {
                var heading = new Paragraph { Margin = new Thickness(0, 8, 0, 6), FontWeight = FontWeights.SemiBold };
                AddInlines(heading, StripLinks(line[headingMatch.Length..]));
                yield return heading;
                continue;
            }

            var bulletMatch = BulletLinePattern.Match(line);
            if (bulletMatch.Success)
            {
                var bullet = new Paragraph { Margin = new Thickness(12, 0, 0, 6), TextIndent = -12 };
                bullet.Inlines.Add(new Run("•  "));
                AddInlines(bullet, StripLinks(line[bulletMatch.Length..]));
                yield return bullet;
                continue;
            }

            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
            AddInlines(paragraph, StripLinks(line));
            yield return paragraph;
        }
    }

    // Drops the "by @user in <url>" suffix GitHub appends to each auto-generated changelog
    // entry, then any other bare URL left in the line, so only the plain description remains.
    private static string StripLinks(string text) =>
        BareUrl.Replace(TrailingByUserLink.Replace(text, ""), "").TrimEnd();

    private void AddInlines(Paragraph paragraph, string text)
    {
        var pos = 0;
        foreach (Match m in InlinePattern.Matches(text))
        {
            if (m.Index > pos)
                paragraph.Inlines.Add(new Run(text[pos..m.Index]));

            if (m.Groups["bold"].Success)
                paragraph.Inlines.Add(new Bold(new Run(m.Groups["bold"].Value)));
            else if (m.Groups["code"].Success)
                paragraph.Inlines.Add(new Run(m.Groups["code"].Value) { FontFamily = new FontFamily("Consolas") });

            pos = m.Index + m.Length;
        }

        if (pos < text.Length)
            paragraph.Inlines.Add(new Run(text[pos..]));
    }

    private void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        UpdateAccepted = true;
        Close();
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        UpdateAccepted = false;
        Close();
    }
}
