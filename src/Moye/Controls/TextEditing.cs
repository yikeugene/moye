using System.Globalization;
using System.Text.RegularExpressions;

namespace Moye.Controls;

/// <summary>Plain-text list edits, separated from WPF focus, IME and undo handling.</summary>
public static class TextEditing
{
    public sealed record Edit(int Start, int Length, string Replacement, int SelectionStart, int SelectionLength)
    {
        public string Apply(string text) => text.Remove(Start, Length).Insert(Start, Replacement);
    }

    private sealed record Line(int Start, string Content, string Ending);
    private static readonly Regex Marker = new(@"^([ \t]*)(?:(?<bullet>[•*-])[ \t]+|(?<number>[0-9]+)[.)][ \t]+)", RegexOptions.CultureInvariant);

    public static Edit ToggleList(string text, int selectionStart, int selectionLength, bool numbered)
    {
        selectionStart = Math.Clamp(selectionStart, 0, text.Length);
        selectionLength = Math.Clamp(selectionLength, 0, text.Length - selectionStart);
        var lines = Lines(text);
        var first = LineAt(lines, selectionStart);
        var last = LineAt(lines, selectionLength == 0 ? selectionStart : selectionStart + selectionLength - 1);
        var selected = lines.GetRange(first, last - first + 1);
        var matches = selected.Select(line => Marker.Match(line.Content)).ToArray();
        var remove = matches.All(match => match.Success && match.Groups[numbered ? "number" : "bullet"].Success);
        var changes = new List<(int Start, int OldLength, int NewLength)>();
        var replacements = new List<string>();
        for (var i = 0; i < selected.Count; i++)
        {
            var line = selected[i];
            var match = matches[i];
            var indent = match.Success ? match.Groups[1].Value : new string(line.Content.TakeWhile(c => c is ' ' or '\t').ToArray());
            var oldPrefixLength = match.Success ? match.Length : indent.Length;
            var prefix = remove ? indent : indent + (numbered ? (i + 1).ToString(CultureInfo.InvariantCulture) + ". " : "• ");
            changes.Add((line.Start, oldPrefixLength, prefix.Length));
            replacements.Add(prefix + line.Content[oldPrefixLength..] + (i < selected.Count - 1 ? line.Ending : ""));
        }
        int Map(int position)
        {
            var delta = 0;
            foreach (var change in changes)
            {
                if (position < change.Start) break;
                if (position <= change.Start + change.OldLength) return change.Start + delta + change.NewLength;
                delta += change.NewLength - change.OldLength;
            }
            return position + delta;
        }
        var start = selected[0].Start;
        var end = selected[^1].Start + selected[^1].Content.Length;
        var mappedStart = Map(selectionStart);
        return new Edit(start, end - start, string.Concat(replacements), mappedStart, Map(selectionStart + selectionLength) - mappedStart);
    }

    /// <summary>Returns null for ordinary paragraphs or a nonempty selection.</summary>
    public static Edit? ContinueList(string text, int selectionStart, int selectionLength)
    {
        if (selectionLength != 0 || selectionStart < 0 || selectionStart > text.Length) return null;
        var lines = Lines(text);
        var line = lines[LineAt(lines, selectionStart)];
        var match = Marker.Match(line.Content);
        if (!match.Success || selectionStart < line.Start + match.Length) return null;
        var indent = match.Groups[1].Value;
        if (string.IsNullOrWhiteSpace(line.Content[match.Length..]))
            return new Edit(line.Start, line.Content.Length, indent, line.Start + indent.Length, 0);
        var marker = "• ";
        if (match.Groups["number"].Success)
        {
            if (!int.TryParse(match.Groups["number"].Value, CultureInfo.InvariantCulture, out var number) || number == int.MaxValue) return null;
            marker = (number + 1).ToString(CultureInfo.InvariantCulture) + ". ";
        }
        var newline = line.Ending.Length > 0 ? line.Ending : lines.FirstOrDefault(candidate => candidate.Ending.Length > 0)?.Ending ?? Environment.NewLine;
        var insertion = newline + indent + marker;
        return new Edit(selectionStart, 0, insertion, selectionStart + insertion.Length, 0);
    }

    private static int LineAt(List<Line> lines, int position)
    {
        for (var i = lines.Count - 1; i >= 0; i--) if (position >= lines[i].Start) return i;
        return 0;
    }

    private static List<Line> Lines(string text)
    {
        var lines = new List<Line>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\r' or '\n')) continue;
            var ending = text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? "\r\n" : text[i].ToString();
            lines.Add(new Line(start, text[start..i], ending));
            i += ending.Length - 1;
            start = i + 1;
        }
        lines.Add(new Line(start, text[start..], ""));
        return lines;
    }
}
