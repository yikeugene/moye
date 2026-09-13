using Moye.Models;
using Moye.ViewModels;

namespace Moye.Tests;

public sealed class HistoryTests
{
    [Fact]
    public void UndoRedoRestoresPageOrderDeletionAndContent()
    {
        var document = new NotebookDocument { Pages = [new NotePage { Texts = [new NoteText { Text = "甲" }] }, new(), new()] };
        var originalIds = document.Pages.Select(p => p.Id).ToArray();
        var history = new NotebookHistory();
        history.Reset(document);
        document.Pages = [document.Pages[2], document.Pages[0], document.Pages[1]];
        history.Record(document);
        document.Pages.RemoveAt(1);
        document.Title = "刪頁後";
        history.Record(document);
        var reordered = history.Undo();
        Assert.Equal(new[] { originalIds[2], originalIds[0], originalIds[1] }, reordered!.Pages.Select(p => p.Id));
        Assert.Equal("甲", reordered.Pages[1].Texts[0].Text);
        var initial = history.Undo();
        Assert.Equal(originalIds, initial!.Pages.Select(p => p.Id));
        Assert.False(history.CanUndo);
        Assert.Null(history.Undo());
        history.Redo();
        var deleted = history.Redo();
        Assert.Equal(new[] { originalIds[2], originalIds[1] }, deleted!.Pages.Select(p => p.Id));
        Assert.Equal("刪頁後", deleted.Title);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void SnapshotsAndReturnedStatesCannotMutateHistoryMetadata()
    {
        var document = new NotebookDocument { Title = "原稿", Pages = [new NotePage { InkData = [1, 2], Texts = [new NoteText { Text = "原文" }], Images = [new NoteImage { AssetId = new string('a', 64) }] }] };
        var history = new NotebookHistory();
        history.Reset(document);
        document.Title = "修改";
        document.Pages[0].Texts[0].Text = "改字";
        document.Pages[0].Images[0].X = 200;
        document.Pages[0].InkData = [8, 9]; // Ink arrays follow the immutable/replace contract.
        history.Record(document);
        var initial = history.Undo();
        Assert.Equal("原稿", initial!.Title);
        Assert.Equal("原文", initial.Pages[0].Texts[0].Text);
        Assert.Equal(72, initial.Pages[0].Images[0].X);
        Assert.Equal(new byte[] { 1, 2 }, initial.Pages[0].InkData);
        initial.Pages[0].Texts[0].Text = "不應改寫歷史";
        initial.Pages.Clear();
        history.Redo();
        var initialAgain = history.Undo();
        Assert.Single(initialAgain!.Pages);
        Assert.Equal("原文", initialAgain.Pages[0].Texts[0].Text);
    }

    [Fact]
    public void DuplicatePageRemainsIndependentThroughUndoAndRedo()
    {
        var document = new NotebookDocument { Pages = [new NotePage { Texts = [new NoteText { Text = "來源" }], InkData = [1, 4] }] };
        var history = new NotebookHistory();
        history.Reset(document);
        var duplicate = document.Pages[0].Snapshot();
        duplicate.Id = Guid.NewGuid().ToString("N");
        duplicate.Texts[0].Id = Guid.NewGuid().ToString("N");
        duplicate.Texts[0].Text = "副本";
        document.Pages.Add(duplicate);
        history.Record(document);
        Assert.Single(history.Undo()!.Pages);
        var restored = history.Redo();
        Assert.Equal(2, restored!.Pages.Count);
        Assert.NotEqual(restored.Pages[0].Id, restored.Pages[1].Id);
        Assert.NotEqual(restored.Pages[0].Texts[0].Id, restored.Pages[1].Texts[0].Id);
        Assert.Equal("來源", restored.Pages[0].Texts[0].Text);
        Assert.Equal("副本", restored.Pages[1].Texts[0].Text);
    }

    [Fact]
    public void EditingAfterUndoDiscardsRedoBranchAndResetClearsBothStacks()
    {
        var document = new NotebookDocument { Title = "起點" };
        var history = new NotebookHistory();
        history.Reset(document);
        document.Title = "A";
        history.Record(document);
        document.Title = "B";
        history.Record(document);
        var branch = history.Undo()!;
        Assert.True(history.CanRedo);
        branch.Title = "C";
        history.Record(branch);
        Assert.False(history.CanRedo);
        Assert.Null(history.Redo());
        Assert.Equal("A", history.Undo()!.Title);
        history.Reset(new NotebookDocument());
        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.Null(history.Undo());
        Assert.Null(history.Redo());
    }

    [Fact]
    public void HistoryRetainsExactlyOneHundredUndoSteps()
    {
        var document = new NotebookDocument { Title = "0" };
        var history = new NotebookHistory();
        history.Reset(document);
        for (var revision = 1; revision <= 150; revision++)
        {
            document.Title = revision.ToString();
            history.Record(document);
        }
        var undoCount = 0;
        NotebookDocument? oldest = null;
        while (history.CanUndo) { oldest = history.Undo(); undoCount++; }
        Assert.Equal(100, undoCount);
        Assert.Equal("50", oldest!.Title);
        var redoCount = 0;
        NotebookDocument? latest = null;
        while (history.CanRedo) { latest = history.Redo(); redoCount++; }
        Assert.Equal(100, redoCount);
        Assert.Equal("150", latest!.Title);
    }
}
