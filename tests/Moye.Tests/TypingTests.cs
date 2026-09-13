using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Moye.Controls;
using Moye.Models;
using Moye.ViewModels;

namespace Moye.Tests;

/// <summary>Deterministic text/layout tests; these do not simulate physical IME composition or the system clipboard.</summary>
public sealed class TypingTests
{
    [Fact]
    public void NewTypingUsesWideIndependentDefaultsInsteadOfHighlighterColor()
    {
        Sta(() =>
        {
            var editor = CreateEditor(new());
            editor.SetTool(InkTool.Highlighter, Colors.Yellow, 20);
            var text = editor.BeginTyping();

            Assert.Same(text, editor.SelectedText);
            Assert.Equal("#FF25334A", text.Color);
            Assert.Equal("Segoe UI, Microsoft JhengHei", text.FontFamily);
            Assert.Equal(22, text.FontSize);
            Assert.Equal(640, text.Width);
            Assert.Equal(72, text.X);
            Assert.Equal(72, text.Y);
            Assert.False(Box(editor).IsReadOnly);
            Assert.True(InputMethod.GetIsInputMethodEnabled(Box(editor)));
        });
    }

    [Fact]
    public void ReturningToTypeResumesRecentTextWithoutDuplicatingOrMovingItsCaret()
    {
        Sta(() =>
        {
            var editor = CreateEditor(new());
            editor.AddTextAt(new Point(30, 40), "first");
            var latest = editor.AddTextAt(new Point(80, 200), "lecture notes");
            var box = Boxes(editor).Last();
            box.Select(2, 5);
            var changes = 0;
            editor.ContentChanged += (_, _) => changes++;

            editor.SetTool(InkTool.Pen, Colors.Red, 3);
            Assert.Null(editor.SelectedText);
            Assert.Same(latest, editor.BeginTyping());
            Assert.Same(latest, editor.BeginTyping());

            Assert.Equal(2, editor.Page.Texts.Count);
            Assert.Equal(2, box.SelectionStart);
            Assert.Equal(5, box.SelectionLength);
            Assert.Equal(0, changes);
        });
    }

    [Fact]
    public void ReopenedPageResumesLastBoxAndPreservesSavedTypography()
    {
        Sta(() =>
        {
            var first = new NoteText { Text = "first" };
            var last = new NoteText { Text = "繁體中文", Bold = true, Italic = true, Alignment = NoteTextAlignment.Right, FontSize = 28 };
            var editor = CreateEditor(new NotePage { Texts = [first, last] });

            Assert.Same(last, editor.BeginTyping());
            var box = Boxes(editor).Last();
            Assert.Equal(FontWeights.Bold, box.FontWeight);
            Assert.Equal(FontStyles.Italic, box.FontStyle);
            Assert.Equal(TextAlignment.Right, box.TextAlignment);
            Assert.Equal(28, box.FontSize);
            Assert.Equal(new Thickness(0), box.Padding);
            Assert.Equal(new Thickness(0), box.BorderThickness);
        });
    }

    [Fact]
    public void WholeBoxFormattingIsOneUndoableEditAndKeepsSelection()
    {
        Sta(() =>
        {
            var page = new NotePage();
            var document = new NotebookDocument { Pages = [page] };
            var editor = CreateEditor(page);
            var text = editor.AddTextAt(new Point(72, 72), "繁體中文 lecture notes");
            var box = Box(editor);
            box.Select(2, 7);
            var history = new NotebookHistory();
            history.Reset(document);
            var changes = 0;
            editor.ContentChanged += (_, _) => { changes++; history.Record(document); };

            editor.ApplyTextStyle(fontFamily: "Arial", fontSize: 30, bold: true, italic: true, alignment: NoteTextAlignment.Center, color: Colors.DarkBlue);

            Assert.Equal(1, changes);
            Assert.Equal(2, box.SelectionStart);
            Assert.Equal(7, box.SelectionLength);
            Assert.Equal("繁體中文 lecture notes", box.Text);
            Assert.Equal(FontWeights.Bold, box.FontWeight);
            Assert.Equal(FontStyles.Italic, box.FontStyle);
            Assert.Equal(TextAlignment.Center, box.TextAlignment);
            Assert.Equal(30, box.FontSize);
            Assert.Equal(Colors.DarkBlue, ((SolidColorBrush)box.Foreground).Color);
            var before = Assert.Single(Assert.Single(history.Undo()!.Pages).Texts);
            Assert.False(before.Bold);
            Assert.False(before.Italic);
            Assert.Equal(22, before.FontSize);
            var after = Assert.Single(Assert.Single(history.Redo()!.Pages).Texts);
            Assert.True(after.Bold);
            Assert.True(after.Italic);
            Assert.Equal(NoteTextAlignment.Center, after.Alignment);
        });
    }

    [Fact]
    public void FormattingDoesNotClearNativeTextUndoAndIgnoresInvalidSizes()
    {
        Sta(() =>
        {
            var editor = CreateEditor(new());
            editor.AddTextAt(new Point(72, 72), "lecture");
            Layout(editor);
            var box = Box(editor);
            box.CaretIndex = box.Text.Length;
            box.SelectedText = " notes";
            editor.CommitPendingEdits();

            editor.ApplyTextStyle(bold: true, fontSize: double.NaN);
            Assert.True(box.CanUndo);
            box.Undo();
            editor.CommitPendingEdits();

            Assert.Equal("lecture", box.Text);
            Assert.Equal("lecture", editor.SelectedText!.Text);
            Assert.True(editor.SelectedText.Bold);
            Assert.Equal(22, editor.SelectedText.FontSize);
        });
    }

    [Fact]
    public void IdenticalFormattingDoesNotCreateAHistoryEntry()
    {
        Sta(() =>
        {
            var editor = CreateEditor(new());
            var text = editor.BeginTyping();
            var changes = 0;
            editor.ContentChanged += (_, _) => changes++;

            editor.ApplyTextStyle(fontFamily: text.FontFamily, fontSize: text.FontSize, bold: text.Bold, italic: text.Italic, alignment: text.Alignment);

            Assert.Equal(0, changes);
        });
    }

    [Fact]
    public void TextDefaultsCopyOnlyStyleAndDoNotModifyExistingContent()
    {
        Sta(() =>
        {
            var editor = CreateEditor(new());
            var existing = editor.AddTextAt(new Point(20, 25), "existing");
            var template = new NoteText { Text = "do not copy", X = 333, Y = 444, Width = 12, FontFamily = "Arial", FontSize = 32, Bold = true, Italic = true, Alignment = NoteTextAlignment.Right, Color = "#FF112233" };
            editor.SetTextDefaults(template);
            template.FontSize = 90;
            editor.SetTool(InkTool.Highlighter, Colors.Yellow, 20);
            var added = editor.AddTextAt(new Point(50, 60));

            Assert.Equal("", added.Text);
            Assert.Equal(50, added.X);
            Assert.Equal(60, added.Y);
            Assert.Equal(32, added.FontSize);
            Assert.Equal("#FF112233", added.Color);
            Assert.True(added.Bold);
            Assert.True(added.Italic);
            Assert.Equal(NoteTextAlignment.Right, added.Alignment);
            Assert.NotEqual(template.Id, added.Id);
            Assert.Equal("existing", existing.Text);
            Assert.Equal(22, existing.FontSize);
            Assert.False(existing.Bold);
        });
    }

    [Fact]
    public void TypingGrowsTheFrameToThePageBottomAndReportsOverflowWithoutLosingText()
    {
        Sta(() =>
        {
            var editor = CreateEditor(new NotePage { Width = 500, Height = 300 });
            var text = editor.BeginTyping();
            var box = Box(editor);
            var paragraphs = string.Join("\r\n", Enumerable.Range(1, 30).Select(i => $"第 {i} 段課堂筆記"));
            var notifications = 0;
            editor.TextSelectionChanged += (_, _) => notifications++;
            box.Text = paragraphs;
            editor.CommitPendingEdits();
            Layout(editor);

            Assert.Equal(228, text.Height);
            Assert.True(editor.HasTextOverflow);
            Assert.True(notifications > 0);
            Assert.Equal(paragraphs, text.Text);
            Assert.Equal(ScrollBarVisibility.Auto, box.VerticalScrollBarVisibility);
            Assert.True(box.ExtentHeight > box.ViewportHeight);
            Assert.InRange(box.ViewportWidth, NoteTextLayout.ContentWidth(text.Width) - .01, NoteTextLayout.ContentWidth(text.Width) + .01);
            Assert.Contains(Descendants<ScrollBar>(box), bar => bar.Orientation == Orientation.Vertical && bar.Visibility == Visibility.Visible);

            var overflowWidth = box.ViewportWidth;
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            editor.UpdateLayout();
            Assert.Equal(overflowWidth, box.ViewportWidth, 3);
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

            box.Text = "short note";
            editor.CommitPendingEdits();
            Assert.False(editor.HasTextOverflow);
            Assert.Equal(228, text.Height);
        });
    }

    [Fact]
    public void ThumbnailPreservesTextSelectionScrollOffsetAndModeWithoutSelectionEvents()
    {
        Sta(() =>
        {
            var editor = CreateEditor(new NotePage { Width = 500, Height = 300 });
            var text = editor.AddTextAt(new Point(72, 72), string.Join("\r\n", Enumerable.Range(1, 30).Select(i => $"Line {i}")));
            var box = Box(editor);
            Layout(editor);
            box.Select(12, 7);
            box.ScrollToEnd();
            editor.UpdateLayout();
            var offset = box.VerticalOffset;
            Assert.True(offset > 0);
            var notifications = 0;
            var changes = 0;
            editor.TextSelectionChanged += (_, _) => notifications++;
            editor.ContentChanged += (_, _) => changes++;

            Assert.NotEmpty(editor.CreateThumbnail());
            editor.UpdateLayout();

            Assert.Same(text, editor.SelectedText);
            Assert.Equal(12, box.SelectionStart);
            Assert.Equal(7, box.SelectionLength);
            Assert.Equal(offset, box.VerticalOffset, 3);
            Assert.Equal(ScrollBarVisibility.Auto, box.VerticalScrollBarVisibility);
            Assert.False(box.IsReadOnly);
            Assert.Equal(0, notifications);
            Assert.Equal(0, changes);
        });
    }

    [Fact]
    public void ListEditChangesSelectedParagraphsAndIsOneNativeUndoUnit()
    {
        Sta(() =>
        {
            var editor = CreateEditor(new());
            var original = "第一段\r\n第二段\r\n最後一段";
            editor.AddTextAt(new Point(72, 72), original);
            Layout(editor);
            var box = Box(editor);
            box.Select(0, 10);
            var changes = 0;
            editor.ContentChanged += (_, _) => changes++;

            editor.ToggleTextList(numbered: false);

            Assert.Equal("• 第一段\r\n• 第二段\r\n最後一段", box.Text);
            Assert.Equal(box.Text, editor.SelectedText!.Text);
            Assert.Equal(1, changes);
            Assert.True(box.CanUndo);
            box.Undo();
            editor.CommitPendingEdits();
            Assert.Equal(original, box.Text);
        });
    }

    [Theory]
    [InlineData("alpha\r\nbeta\r\ngamma", 0, 13, false, "• alpha\r\n• beta\r\ngamma")]
    [InlineData("• alpha\nbeta", 0, 12, true, "1. alpha\n2. beta")]
    [InlineData("1. alpha\n2. beta", 0, 16, true, "alpha\nbeta")]
    [InlineData("\t• 第一段\r\n  • 第二段", 0, 17, false, "\t第一段\r\n  第二段")]
    [InlineData("alpha\nbeta", 6, 0, false, "alpha\n• beta")]
    [InlineData("", 0, 0, true, "1. ")]
    public void ListTogglePreservesLineEndingsAndConvertsOrRemovesMarkers(string source, int start, int length, bool numbered, string expected)
    {
        var edit = TextEditing.ToggleList(source, start, length, numbered);
        Assert.Equal(expected, edit.Apply(source));
        Assert.InRange(edit.SelectionStart, 0, expected.Length);
        Assert.InRange(edit.SelectionLength, 0, expected.Length - edit.SelectionStart);
    }

    [Fact]
    public void ListToggleKeepsCaretOnTheSameBodyCharacter()
    {
        const string source = "alpha\r\nbeta";
        var edit = TextEditing.ToggleList(source, 9, 0, false);
        Assert.Equal("alpha\r\n• beta", edit.Apply(source));
        Assert.Equal(11, edit.SelectionStart);
        Assert.Equal(0, edit.SelectionLength);
        var reversed = TextEditing.ToggleList(edit.Apply(source), edit.SelectionStart, 0, false);
        Assert.Equal(source, reversed.Apply(edit.Apply(source)));
        Assert.Equal(9, reversed.SelectionStart);
    }

    [Theory]
    [InlineData("• lecture", "• lecture\r\n• ")]
    [InlineData("8. lecture", "8. lecture\r\n9. ")]
    [InlineData("  • ", "  ")]
    [InlineData("12.   ", "")]
    [InlineData("• first\n• second", "• first\n• second\n• ")]
    public void EnterContinuesListsAndEmptyItemsExitTheList(string source, string expected)
    {
        var edit = Assert.IsType<TextEditing.Edit>(TextEditing.ContinueList(source, source.Length, 0));
        Assert.Equal(expected, edit.Apply(source));
        Assert.Equal(expected.Length, edit.SelectionStart);
        Assert.Equal(0, edit.SelectionLength);
    }

    [Fact]
    public void PlainEnterAndSelectedTextRemainNativeTextBoxOperations()
    {
        Assert.Null(TextEditing.ContinueList("plain paragraph", 15, 0));
        Assert.Null(TextEditing.ContinueList("• paragraph", 2, 4));
        Assert.Null(TextEditing.ContinueList("• paragraph", 0, 0));
    }

    private static PageEditor CreateEditor(NotePage page) => new(page, _ => throw new InvalidOperationException("No image assets in typing fixtures."));
    private static TextBox Box(PageEditor editor) => Assert.Single(Boxes(editor));
    private static IEnumerable<TextBox> Boxes(PageEditor editor) => Descendants<TextBox>(editor);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) yield return found;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void Layout(PageEditor editor)
    {
        editor.Measure(new Size(editor.Width, editor.Height));
        editor.Arrange(new Rect(0, 0, editor.Width, editor.Height));
        editor.UpdateLayout();
    }
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Typing test exceeded 30 seconds.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
