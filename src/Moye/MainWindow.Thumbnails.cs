using System.Windows;
using Moye.Controls;
using Moye.ViewModels;

namespace Moye;

public partial class MainWindow
{
    private readonly HashSet<PageViewModel> _pendingThumbnails = [];
    private readonly Dictionary<PageViewModel, PageEditor> _preparedThumbnails = [];
    private readonly Dictionary<PageViewModel, int> _thumbnailRevisions = [];
    private int _thumbnailGeneration;
    private bool _thumbnailLoadActive;

    private void QueueThumbnail(PageEditor editor)
    {
        _dirtyThumbnails.Add(editor);
        _thumbnailTimer.Start();
    }

    private void InvalidateThumbnail(PageEditor editor)
    {
        var item = ViewModel.Pages.FirstOrDefault(p => ReferenceEquals(p.Page, editor.Page));
        if (item is null) return;
        item.Thumbnail = null;
        _thumbnailRevisions[item] = _thumbnailRevisions.GetValueOrDefault(item) + 1;
        _preparedThumbnails.Remove(item);
        // Keep work by page, even if virtualization detaches the dirty editor
        // before the idle timer. A cached image must never hide a later edit.
        _pendingThumbnails.Add(item);
    }

    private void ThumbnailLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PageViewModel item } && item.Thumbnail is null)
        { _pendingThumbnails.Add(item); _thumbnailTimer.Start(); }
    }

    private async void ProcessThumbnailWork(object? sender, EventArgs e)
    {
        try { await ProcessThumbnailWorkAsync(); }
        catch (Exception ex)
        {
            // A failed optional thumbnail must not tear down the input loop.
            if (!_closing) ViewModel.Status = "Unable to refresh page preview: " + ex.Message;
        }
    }

    private async Task ProcessThumbnailWorkAsync()
    {
        if (_closing) { _thumbnailTimer.Stop(); return; }
        if (DeferViewportBackgroundWork || AnyPenDown || ViewModel.IsBusy || _thumbnailLoadActive) return;
        // One bitmap per background tick, rather than a burst of layout + PNG
        // encoding when several virtualized pages are realized in one scroll.
        foreach (var editor in _dirtyThumbnails.ToArray())
        {
            _dirtyThumbnails.Remove(editor);
            var item = ViewModel.Pages.FirstOrDefault(p => ReferenceEquals(p.Page, editor.Page));
            if (item is null || !editor.IsLoaded) continue;
            UpdateThumbnail(editor);
            _pendingThumbnails.Remove(item); _preparedThumbnails.Remove(item);
            return;
        }
        while (_pendingThumbnails.Count > 0)
        {
            var item = _pendingThumbnails.First(); _pendingThumbnails.Remove(item);
            if (!ViewModel.Pages.Contains(item)) { _preparedThumbnails.Remove(item); continue; }
            if (_preparedThumbnails.Remove(item, out var prepared))
            {
                item.Thumbnail = DecodeBitmap(prepared.CreateThumbnail(180));
                return;
            }
            if (item.Thumbnail is not null) continue;
            var live = _editors.Values.FirstOrDefault(editor => ReferenceEquals(editor.Page, item.Page) && editor.IsLoaded);
            if (live is not null) { UpdateThumbnail(live); return; }
            await PrepareSidebarThumbnailAsync(item);
            return;
        }
        _thumbnailTimer.Stop();
    }

    private async Task PrepareSidebarThumbnailAsync(PageViewModel item)
    {
        var generation = _thumbnailGeneration;
        var revision = _thumbnailRevisions.GetValueOrDefault(item);
        bool Current() => !_closing && generation == _thumbnailGeneration && ViewModel.Pages.Contains(item) &&
            revision == _thumbnailRevisions.GetValueOrDefault(item);
        _thumbnailLoadActive = true;
        try
        {
            var editor = new PageEditor(item.Page.Snapshot(), id => ViewModel.Repository.GetAssetAsync(id));
            void QueuePrepared()
            {
                if (!Current()) return;
                _preparedThumbnails[item] = editor; _pendingThumbnails.Add(item); _thumbnailTimer.Start();
            }
            // Asset completion may arrive during a drag. Queue its ready editor;
            // never render or encode a thumbnail from that input-time callback.
            editor.VisualContentChanged += (_, _) => QueuePrepared();
            if (item.Page.Pdf is not null)
            {
                var bitmap = await ViewModel.Pdf.RenderAsync(item.Page, .2);
                if (Current()) editor.SetPdfBackground(bitmap);
            }
            QueuePrepared();
        }
        catch { /* The full page reports load failures when opened. */ }
        finally { _thumbnailLoadActive = false; }
    }

    private void ResetThumbnailWork()
    {
        _thumbnailGeneration++;
        _dirtyThumbnails.Clear(); _pendingThumbnails.Clear(); _preparedThumbnails.Clear(); _thumbnailRevisions.Clear();
        _thumbnailTimer.Stop();
    }
}
