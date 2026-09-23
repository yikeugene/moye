using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Moye.Models;
using Moye.ViewModels;

namespace Moye;

public partial class MainWindow
{
    internal sealed record PageActionTarget(NotebookDocument Document, string SectionId, string PageId);
    private PageActionTarget? _paperActionTarget;

    // A menu retains the clicked page, not whichever page scrolling selects later.
    // Replaced documents (including undo), removed pages and section changes invalidate it.
    internal static PageViewModel? ResolvePageActionTarget(MainViewModel viewModel, PageActionTarget target)
    {
        if (!ReferenceEquals(viewModel.Document, target.Document) || viewModel.IsLibraryVisible ||
            viewModel.IsBusy || viewModel.SelectedSection?.Id != target.SectionId) return null;
        return viewModel.Pages.FirstOrDefault(page => page.Page.Id == target.PageId && page.Page.SectionId == target.SectionId);
    }

    private void ThumbnailRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // ListBoxItem normally selects on right-button down, which would scroll
        // the writing viewport before the page menu can appear.
        if (sender is ListBoxItem { DataContext: PageViewModel }) e.Handled = true;
    }

    private void PageContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (e.Handled || sender is not FrameworkElement { DataContext: PageViewModel page } owner ||
            HasInputAncestor<TextBoxBase>(e.OriginalSource as DependencyObject)) return;
        if (ViewModel.Document is null || ViewModel.IsBusy || ViewModel.IsLibraryVisible || AnyPenDown) return;
        var menu = CreatePageContextMenu(page);
        if (menu is null) return;
        ClearTouches(); CloseSettingsPopups();
        menu.PlacementTarget = owner;
        menu.Placement = e.CursorLeft < 0 ? PlacementMode.Center : PlacementMode.MousePoint;
        // Open explicitly instead of assigning an ancestor ContextMenu: text
        // boxes and future child controls keep their own editing menus.
        e.Handled = true;
        menu.IsOpen = true;
    }

    private ContextMenu? CreatePageContextMenu(PageViewModel page)
    {
        if (ViewModel.Document is not { } document) return null;
        var target = new PageActionTarget(document, page.Page.SectionId, page.Page.Id);
        if (ResolvePageActionTarget(ViewModel, target) is null) return null;
        var menu = new ContextMenu { Tag = target };
        var itemStyle = new Style(typeof(MenuItem), TryFindResource(typeof(MenuItem)) as Style);
        itemStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 44.0));
        // ContextMenu also generates Separator containers. Apply this style to
        // MenuItems explicitly so opening the menu never styles a Separator as one.
        menu.Items.Add(new MenuItem { Header = $"Page {page.Number}", IsEnabled = false, MinHeight = 32, Style = itemStyle });
        menu.Items.Add(new Separator());
        Add("Duplicate Page", DuplicatePageClick);
        Add("Move Page Up", MovePageUpClick, ViewModel.Pages.IndexOf(page) > 0);
        Add("Move Page Down", MovePageDownClick, ViewModel.Pages.IndexOf(page) < ViewModel.Pages.Count - 1);
        var sections = new MenuItem { Header = "Move Page to Section", Tag = target, IsEnabled = ViewModel.Sections.Count > 1, Style = itemStyle, ItemContainerStyle = itemStyle };
        // A placeholder lets WPF show the submenu arrow before it is populated.
        sections.Items.Add(new MenuItem());
        sections.SubmenuOpened += MovePageSectionMenuOpened;
        menu.Items.Add(sections);
        menu.Items.Add(new Separator());
        Add("Paper Style…", PageSettingsClick);
        Add("Delete Page (Undo Available)", DeletePageClick);
        return menu;

        void Add(string title, RoutedEventHandler click, bool enabled = true)
        {
            var item = new MenuItem { Header = title, Tag = target, IsEnabled = enabled, Style = itemStyle };
            item.Click += click; menu.Items.Add(item);
        }
    }

    private bool ActivatePageAction(PageActionTarget target)
    {
        if (AnyPenDown || ResolvePageActionTarget(ViewModel, target) is null) return false;
        CommitEditors();
        if (ResolvePageActionTarget(ViewModel, target) is not { } page) return false;
        var previous = _suppressPageSelection;
        _suppressPageSelection = true;
        try { ViewModel.SelectedPage = page; SyncPageTemplate(); UpdateTextToolbar(); }
        finally { _suppressPageSelection = previous; }
        return true;
    }

    private void RunPageAction(object sender, Action action)
    {
        if (sender is not MenuItem { Tag: PageActionTarget target } || !ActivatePageAction(target)) return;
        action(); ScrollToSelected();
    }
}
