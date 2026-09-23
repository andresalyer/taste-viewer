using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using Taste.Services;

namespace Taste;

public partial class UpdateDialog : Window
{
    // Matches **bold**, `code`, [text](url) markdown links, and bare https:// URLs
    // (GitHub's auto-generated release notes use the bare-URL form: "* ... by @user in https://...").
    private static readonly Regex InlinePattern = new(
        @"\*\*(?<bold>.+?)\*\*|`(?<code>[^`]+?)`|\[(?<linktext>[^\]]+)\]\((?<linkurl>[^)]+)\)|(?<bareurl>https?://\S+)",
        RegexOptions.Compiled);

    private static readonly Regex BulletLinePattern = new(@"^[-*]\s+", RegexOptions.Compiled);
    private static readonly Regex HeadingLinePattern = new(@"^(#{1,6})\s+", RegexOptions.Compiled);

    public bool UpdateAccepted { get; private set; }

    public UpdateDialog(UpdateInfo update)
    {
        InitializeComponent();
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

    private IEnumerable<Block> ParseMarkdown(string markdown)
    {
        foreach (var rawLine in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var headingMatch = HeadingLinePattern.Match(line);
            if (headingMatch.Success)
            {
                var heading = new Paragraph { Margin = new Thickness(0, 8, 0, 6), FontWeight = FontWeights.SemiBold };
                AddInlines(heading, line[headingMatch.Length..]);
                yield return heading;
                continue;
            }

            var bulletMatch = BulletLinePattern.Match(line);
            if (bulletMatch.Success)
            {
                var bullet = new Paragraph { Margin = new Thickness(12, 0, 0, 6), TextIndent = -12 };
                bullet.Inlines.Add(new Run("•  "));
                AddInlines(bullet, line[bulletMatch.Length..]);
                yield return bullet;
                continue;
            }

            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
            AddInlines(paragraph, line);
            yield return paragraph;
        }
    }

    private void AddInlines(Paragraph paragraph, string text)
    {
        var pos = 0;
        foreach (Match m in InlinePattern.Matches(text))
        {
            if (m.Index > pos)
                paragraph.Inlines.Add(new Run(text[pos..m.Index]));

            if (m.Groups["bold"].Success)
            {
                paragraph.Inlines.Add(new Bold(new Run(m.Groups["bold"].Value)));
            }
            else if (m.Groups["code"].Success)
            {
                paragraph.Inlines.Add(new Run(m.Groups["code"].Value) { FontFamily = new FontFamily("Consolas") });
            }
            else if (m.Groups["linktext"].Success)
            {
                paragraph.Inlines.Add(MakeHyperlink(m.Groups["linktext"].Value, m.Groups["linkurl"].Value));
            }
            else if (m.Groups["bareurl"].Success)
            {
                var url = m.Groups["bareurl"].Value.TrimEnd('.', ',', ')');
                paragraph.Inlines.Add(MakeHyperlink(url, url));
            }

            pos = m.Index + m.Length;
        }

        if (pos < text.Length)
            paragraph.Inlines.Add(new Run(text[pos..]));
    }

    private Inline MakeHyperlink(string text, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new Run(text);

        var link = new Hyperlink(new Run(text))
        {
            NavigateUri = uri,
            Foreground = (Brush)FindResource("Accent"),
        };
        link.RequestNavigate += Hyperlink_RequestNavigate;
        return link;
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
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
