using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Moye.Controls;
using Moye.Models;

namespace Moye;

public partial class MainWindow
{
    private NoteText _textDefaults = new() { FontFamily = "Segoe UI", FontSize = 22, Color = "#FF25334A" };
    private bool _syncingTextToolbar, _applyingTextFormat;
    private int _typingRequest;
    private PageEditor? _textPickerEditor;
    private NoteText? _textPickerSelection;
    private bool _textPickerHasTarget;

    private void InitializeTypingUi()
    {
        foreach (ComboBoxItem item in TextSizePicker.Items)
            item.Content = double.Parse((string)item.Content, CultureInfo.InvariantCulture).ToString("0.##", CultureInfo.CurrentCulture);
    }
    private void TextPickerGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (((ComboBox)sender).IsKeyboardFocusWithin) return;
        _textPickerEditor = CurrentEditor;
        _textPickerSelection = CurrentEditor?.SelectedText;
        _textPickerHasTarget = true;
    }
    private bool CanCommitTextPicker()
    {
        // A page click can activate another editor before the old picker loses
        // focus. Cancel that uncommitted value instead of styling another page.
        if (!_textPickerHasTarget || ReferenceEquals(CurrentEditor, _textPickerEditor) && ReferenceEquals(CurrentEditor?.SelectedText, _textPickerSelection)) return true;
        UpdateTextToolbar();
        return false;
    }

    private async void TypeClick(object sender, RoutedEventArgs e) => await StartTypingAsync();
    private async Task StartTypingAsync(bool newBox = false)
    {
        if (ViewModel.Document is null || ViewModel.IsLibraryVisible || ViewModel.IsBusy) return;
        var revision = ++_typingRequest;
        var page = ViewModel.SelectedPage;
        SetTool(InkTool.Text);
        if (CurrentEditor is null)
        {
            ScrollToSelected(); PageList.UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        }
        if (revision != _typingRequest || _tool != InkTool.Text || ViewModel.SelectedPage != page || ViewModel.IsLibraryVisible || ViewModel.IsBusy || CurrentEditor is not { } editor) return;
        editor.SetTextDefaults(_textDefaults);
        if (newBox)
        {
            var previous = editor.SelectedText ?? editor.Page.Texts.LastOrDefault();
            var y = previous is null ? 72 : previous.Y + previous.Height + 24;
            // Keep a new box on the current page; it can be dragged anywhere.
            if (y > editor.Page.Height - 130) y = Math.Min(96, Math.Max(0, editor.Page.Height - 130));
            editor.AddTextAt(new Point(Math.Min(72, Math.Max(0, editor.Page.Width - 100)), y));
        }
        else editor.BeginTyping();
        UpdateTextToolbar();
    }
    private async void AddTextBoxClick(object sender, RoutedEventArgs e) => await StartTypingAsync(true);
    private void FinishTyping()
    {
        _typingRequest++;
        CommitEditors(); Keyboard.ClearFocus(); SetTool(InkTool.Pen); PageList.Focus();
    }
    private void TextSelectionChanged(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, CurrentEditor)) return;
        UpdateTextToolbar();
    }
    private void UpdateTextToolbar()
    {
        if (TextToolbar is null || _syncingTextToolbar) return;
        bool typing = _tool == InkTool.Text || _tool == InkTool.Select && CurrentEditor?.SelectedText is not null;
        TextToolbar.Visibility = typing ? Visibility.Visible : Visibility.Collapsed;
        FavouriteToolbar.Visibility = typing ? Visibility.Collapsed : Visibility.Visible;
        if (!typing) return;
        // A delayed autosave/selection notification must not replace a font size
        // the user is still entering in the toolbar.
        if (TextFontPicker.IsKeyboardFocusWithin || TextSizePicker.IsKeyboardFocusWithin || TextAlignmentPicker.IsDropDownOpen) return;
        var text = CurrentEditor?.SelectedText ?? _textDefaults;
        _textDefaults = new NoteText { FontFamily = text.FontFamily, FontSize = text.FontSize, Bold = text.Bold, Italic = text.Italic, Color = text.Color, Alignment = text.Alignment };
        foreach (var editor in _editors.Values) editor.SetTextDefaults(_textDefaults);
        _syncingTextToolbar = true;
        try
        {
            TextFontPicker.Text = text.FontFamily;
            TextSizePicker.Text = (text.FontSize * .75).ToString("0.##", CultureInfo.CurrentCulture);
            TextAlignmentPicker.SelectedIndex = (int)text.Alignment;
            TextBoldButton.Background = text.Bold ? new SolidColorBrush(Color.FromRgb(237, 242, 254)) : Brushes.Transparent;
            TextItalicButton.Background = text.Italic ? new SolidColorBrush(Color.FromRgb(237, 242, 254)) : Brushes.Transparent;
            System.Windows.Automation.AutomationProperties.SetHelpText(TextBoldButton, text.Bold ? "Bold is on" : "Bold is off");
            System.Windows.Automation.AutomationProperties.SetHelpText(TextItalicButton, text.Italic ? "Italic is on" : "Italic is off");
            TextColorButton.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(text.Color));
            bool overflow = CurrentEditor?.HasTextOverflow == true;
            TypingHint.Text = overflow
                ? "Text exceeds this page. Move the extra text to a new page before PDF export."
                : "Type here, or click the paper to add another box. Ctrl+Enter returns to Pen.";
            TypingHint.Foreground = overflow ? Brushes.Firebrick : Brushes.SlateGray;
        }
        finally { _syncingTextToolbar = false; }
    }
    private void ApplyTextFormatting(string? fontFamily = null, double? fontSize = null, bool? bold = null,
        bool? italic = null, NoteTextAlignment? alignment = null, Color? color = null, bool restoreFocus = true)
    {
        if (_syncingTextToolbar || _applyingTextFormat || ViewModel.IsBusy || ViewModel.IsLibraryVisible) return;
        _applyingTextFormat = true;
        try
        {
            if (fontFamily is not null) _textDefaults.FontFamily = fontFamily;
            if (fontSize is not null) _textDefaults.FontSize = fontSize.Value;
            if (bold is not null) _textDefaults.Bold = bold.Value;
            if (italic is not null) _textDefaults.Italic = italic.Value;
            if (alignment is not null) _textDefaults.Alignment = alignment.Value;
            if (color is not null) _textDefaults.Color = color.Value.ToString();
            foreach (var editor in _editors.Values) editor.SetTextDefaults(_textDefaults);
            CurrentEditor?.ApplyTextStyle(fontFamily, fontSize, bold, italic, alignment, color, restoreFocus);
            UpdateTextToolbar();
        }
        finally { _applyingTextFormat = false; }
    }
    private void TextBoldClick(object sender, RoutedEventArgs e) => ApplyTextFormatting(bold: !(CurrentEditor?.SelectedText ?? _textDefaults).Bold);
    private void TextItalicClick(object sender, RoutedEventArgs e) => ApplyTextFormatting(italic: !(CurrentEditor?.SelectedText ?? _textDefaults).Italic);
    private void TextFontChanged(object? sender, EventArgs e) => CommitTextFont(true);
    private void CommitTextFont(bool restoreFocus)
    {
        if (_syncingTextToolbar || !CanCommitTextPicker() || TextFontPicker.IsDropDownOpen || string.IsNullOrWhiteSpace(TextFontPicker.Text)) return;
        ApplyTextFormatting(fontFamily: TextFontPicker.Text.Trim(), restoreFocus: restoreFocus);
    }
    private void TextSizeChanged(object? sender, EventArgs e) => CommitTextSize(true);
    private void CommitTextSize(bool restoreFocus)
    {
        if (_syncingTextToolbar || !CanCommitTextPicker() || TextSizePicker.IsDropDownOpen) return;
        if (!double.TryParse(TextSizePicker.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var points) || !double.IsFinite(points) || points < 6 || points > 96)
        { TypingHint.Text = "Enter a text size from 6 to 96 pt."; TypingHint.Foreground = Brushes.Firebrick; return; }
        ApplyTextFormatting(fontSize: points / .75, restoreFocus: restoreFocus);
    }
    private void TextPickerLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (((ComboBox)sender).IsKeyboardFocusWithin || ((ComboBox)sender).IsDropDownOpen) return;
        CommitTextPicker(sender, false);
    }
    private void CommitTextPicker(object sender, bool restoreFocus)
    {
        if (sender == TextFontPicker) CommitTextFont(restoreFocus);
        else if (sender == TextSizePicker) CommitTextSize(restoreFocus);
        else CommitTextAlignment(restoreFocus);
    }
    private void TextPickerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _syncingTextToolbar = true;
            try { ((ComboBox)sender).IsDropDownOpen = false; }
            finally { _syncingTextToolbar = false; }
            CommitTextPicker(sender, true);
            e.Handled = true;
        }
        if (e.Key == Key.Escape)
        {
            // Reset before focus leaves: the LostKeyboardFocus handler must not
            // commit the value the user has just cancelled.
            _syncingTextToolbar = true;
            try
            {
                ((ComboBox)sender).IsDropDownOpen = false;
                var text = CurrentEditor?.SelectedText ?? _textDefaults;
                TextFontPicker.Text = text.FontFamily;
                TextSizePicker.Text = (text.FontSize * .75).ToString("0.##", CultureInfo.CurrentCulture);
                TextAlignmentPicker.SelectedIndex = (int)text.Alignment;
            }
            finally { _syncingTextToolbar = false; }
            CurrentEditor?.FocusSelectedText(); e.Handled = true;
        }
    }
    private void TextAlignmentChanged(object sender, EventArgs e) => CommitTextAlignment(true);
    private void CommitTextAlignment(bool restoreFocus)
    {
        if (!_syncingTextToolbar && CanCommitTextPicker() && !TextAlignmentPicker.IsDropDownOpen && TextAlignmentPicker.SelectedIndex >= 0)
            ApplyTextFormatting(alignment: (NoteTextAlignment)TextAlignmentPicker.SelectedIndex, restoreFocus: restoreFocus);
    }
    private void TextColorClick(object sender, RoutedEventArgs e)
    {
        var text = CurrentEditor?.SelectedText ?? _textDefaults;
        var dialog = new ColorPickerDialog(this, (Color)ColorConverter.ConvertFromString(text.Color), "Text Color");
        if (dialog.ShowDialog() != true) return;
        ApplyTextFormatting(color: dialog.SelectedColor);
    }
    private void TextBulletsClick(object sender, RoutedEventArgs e) => CurrentEditor?.ToggleTextList(false);
    private void TextNumberingClick(object sender, RoutedEventArgs e) => CurrentEditor?.ToggleTextList(true);
}
