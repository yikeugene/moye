using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Moye.Controls;
using Moye.ViewModels;

namespace Moye;

public partial class MainWindow
{
    private bool _changingSection;

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
        if (ViewModel.SelectedSection is not { } section || ViewModel.IsBusy) return;
        CommitEditors();
        var dialog = new InputDialog(this, "Rename Section", ("Topic or section name", section.Title));
        if (dialog.ShowDialog() == true) ChangeSectionView(() => ViewModel.RenameSection(dialog.Values[0]));
    }

    private void MoveSectionUpClick(object sender, RoutedEventArgs e) => ChangeSectionView(() => ViewModel.MoveSection(-1));
    private void MoveSectionDownClick(object sender, RoutedEventArgs e) => ChangeSectionView(() => ViewModel.MoveSection(1));

    private void DeleteSectionClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedSection is not { } section || ViewModel.IsBusy) return;
        if (ViewModel.Sections.Count <= 1)
        {
            MessageBox.Show(this, "Keep at least one section in your notebook. You can rename this section or delete individual pages.", "Delete Section");
            return;
        }
        CommitEditors();
        if (MessageBox.Show(this, $"Delete \"{section.Title}\" and its {section.PageCountText}?\n\nYou can undo this while the notebook remains open. To keep these pages, move them to another section first.",
            "Delete Section", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
            ChangeSectionView(() => ViewModel.DeleteSection());
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
                ToolTip = section.Title, Tag = target,
                IsEnabled = section.Id != page.Page.SectionId
            };
            item.Click += (_, _) => RunPageAction(item, () => ChangeSectionView(() => ViewModel.MovePageToSection(section.Id)));
            menu.Items.Add(item);
        }
    }
}
