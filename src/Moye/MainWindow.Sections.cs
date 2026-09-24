using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Moye.Controls;
using Moye.Models;
using Moye.ViewModels;

namespace Moye;

public partial class MainWindow
{
    private bool _changingSection;
    internal sealed record SectionActionTarget(NotebookDocument Document, string SectionId);

    internal static SectionViewModel? ResolveSectionActionTarget(MainViewModel viewModel, SectionActionTarget target)
    {
        if (!ReferenceEquals(viewModel.Document, target.Document) || viewModel.IsLibraryVisible || viewModel.IsBusy) return null;
        return viewModel.Sections.FirstOrDefault(section => section.Id == target.SectionId);
    }

    private void SectionRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Opening a menu should not navigate away from the page being edited.
        if (sender is ListBoxItem { DataContext: SectionViewModel }) e.Handled = true;
    }

    private void SectionContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (e.Handled || sender is not FrameworkElement owner) return;
        var section = owner.DataContext as SectionViewModel ?? ViewModel.SelectedSection;
        if (section is null) return;
        e.Handled = true;
        OpenSectionContextMenu(section, owner, e.CursorLeft < 0 ? PlacementMode.Center : PlacementMode.MousePoint);
    }

    private void SectionOptionsClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement owner && ViewModel.SelectedSection is { } section)
            OpenSectionContextMenu(section, owner, PlacementMode.Bottom);
    }

    private void OpenSectionContextMenu(SectionViewModel section, FrameworkElement owner, PlacementMode placement)
    {
        if (_closing || AnyPenDown || _changingSection || ViewModel.Document is not { } document) return;
        var target = new SectionActionTarget(document, section.Id);
        if (ResolveSectionActionTarget(ViewModel, target) is not { } current) return;
        var menu = new ContextMenu { Tag = target, PlacementTarget = owner, Placement = placement };
        menu.Items.Add(CreateMenuHeading(current.Title));
        menu.Items.Add(new Separator());
        Add("Rename Section…", "\uE8AC", RenameSectionClick);
        var index = ViewModel.Sections.IndexOf(current);
        Add("Move Section Up", "\uE74A", MoveSectionUpClick, index > 0);
        Add("Move Section Down", "\uE74B", MoveSectionDownClick, index < ViewModel.Sections.Count - 1);
        menu.Items.Add(new Separator());
        Add("Delete Section…", "\uE74D", DeleteSectionClick, ViewModel.CanDeleteSection, danger: true);
        ClearTouches(); CloseSettingsPopups();
        menu.IsOpen = true;

        void Add(string title, string glyph, RoutedEventHandler click, bool enabled = true, bool danger = false)
        {
            var item = new MenuItem { Header = title, Icon = CreateMenuIcon(glyph), Tag = target, IsEnabled = enabled };
            if (danger)
            {
                item.Style = (Style)FindResource("DangerMenuItem");
                item.ToolTip = enabled ? "Delete this section and its pages · Undo available" : "Keep at least one section in your notebook";
                ToolTipService.SetShowOnDisabled(item, true);
            }
            item.Click += click;
            menu.Items.Add(item);
        }
    }

    private void RunSectionAction(object sender, Action action)
    {
        if (_closing || AnyPenDown || sender is not MenuItem { Tag: SectionActionTarget target } ||
            ResolveSectionActionTarget(ViewModel, target) is null) return;
        ChangeSectionView(() =>
        {
            // Re-resolve after committing editors: never fall back to the selected section.
            if (ResolveSectionActionTarget(ViewModel, target) is not { } section) return;
            ViewModel.SelectedSection = section;
            action();
        });
    }

    private void SectionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _changingSection || ViewModel.IsBusy ||
            SectionList.SelectedItem is not SectionViewModel section ||
            section == ViewModel.SelectedSection) return;
        ChangeSectionView(() => ViewModel.SelectedSection = section);
    }

    private void ChangeSectionView(Action change)
    {
        if (ViewModel.Document is null || ViewModel.IsBusy || _changingSection) return;
        _changingSection = true;
        try
        {
            // Commit text and ink before a section replaces the visible page editors.
            CommitEditors(); ClearTouches(); ResetViewportInputStability(); CloseSettingsPopups();
            _zoomNavigationRevision++; _zoomNavigationActive = false;
            change();
            ScrollToSelected();
            var sectionId = ViewModel.SelectedSection?.Id;
            var revision = _zoomNavigationRevision;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (_closing || AnyPenDown || revision != _zoomNavigationRevision || ViewModel.IsLibraryVisible || ViewModel.SelectedSection?.Id != sectionId) return;
                ScrollToSelected();
                if (_fitWidthActive) FitWidth();
            }));
        }
        finally { _changingSection = false; }
    }

    private void AddSectionClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Document is null || ViewModel.IsBusy) return;
        CommitEditors();
        var dialog = new InputDialog(this, "Add Section", ("Topic or section name", "New Section"));
        if (dialog.ShowDialog() == true) ChangeSectionView(() => ViewModel.AddSection(dialog.Values[0]));
    }

    private void RenameSectionClick(object sender, RoutedEventArgs e)
    {
        if (AnyPenDown || sender is not MenuItem { Tag: SectionActionTarget target } ||
            ResolveSectionActionTarget(ViewModel, target) is null) return;
        CommitEditors();
        if (ResolveSectionActionTarget(ViewModel, target) is not { } section) return;
        var dialog = new InputDialog(this, "Rename Section", ("Topic or section name", section.Title));
        if (dialog.ShowDialog() == true) RunSectionAction(sender, () => ViewModel.RenameSection(dialog.Values[0]));
    }

    private void MoveSectionUpClick(object sender, RoutedEventArgs e) => RunSectionAction(sender, () => ViewModel.MoveSection(-1));
    private void MoveSectionDownClick(object sender, RoutedEventArgs e) => RunSectionAction(sender, () => ViewModel.MoveSection(1));

    private void DeleteSectionClick(object sender, RoutedEventArgs e)
    {
        if (AnyPenDown || sender is not MenuItem { Tag: SectionActionTarget target } ||
            ResolveSectionActionTarget(ViewModel, target) is null) return;
        if (ViewModel.Sections.Count <= 1)
        {
            MessageBox.Show(this, "Keep at least one section in your notebook. You can rename this section or delete individual pages.", "Delete Section");
            return;
        }
        CommitEditors();
        if (ResolveSectionActionTarget(ViewModel, target) is not { } section) return;
        if (MessageBox.Show(this, $"Delete \"{section.Title}\" and its {section.PageCountText}?\n\nYou can undo this while the notebook remains open. To keep these pages, move them to another section first.",
            "Delete Section", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
            RunSectionAction(sender, () => ViewModel.DeleteSection());
    }

    private void MovePageSectionMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: PageActionTarget target } menu || !ReferenceEquals(e.OriginalSource, menu)) return;
        menu.Items.Clear();
        if (ResolvePageActionTarget(ViewModel, target) is not { } page) return;
        foreach (var section in ViewModel.Sections)
        {
            // Header as a TextBlock keeps underscores in user titles literal.
            var item = new MenuItem
            {
                Header = new TextBlock { Text = section.Title, MaxWidth = 280, TextTrimming = TextTrimming.CharacterEllipsis },
                Icon = CreateMenuIcon("\uE8F1"),
                ToolTip = section.Title, Tag = target,
                IsEnabled = section.Id != page.Page.SectionId
            };
            item.Click += (_, _) => RunPageAction(item, () => ChangeSectionView(() => ViewModel.MovePageToSection(section.Id)));
            menu.Items.Add(item);
        }
    }
}
