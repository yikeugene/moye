using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Moye;
using Moye.Controls;
using Moye.Models;
using Moye.Services;
using Moye.ViewModels;

namespace Moye.UiPreview;

internal static class Program
{
    // No Application.Run, Window.Show, input injection, UI Automation client,
    // desktop capture, or user database is involved in this renderer.
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var repositoryRoot = Path.GetFullPath(args.FirstOrDefault() ?? Directory.GetCurrentDirectory());
            var output = Path.Combine(repositoryRoot, "artifacts");
            Directory.CreateDirectory(output);
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources = ReadApplicationResources(Path.Combine(repositoryRoot, "src", "Moye", "App.xaml"));
            using var repository = new FixtureRepository();
            var window = new MainWindow(repository);
            VerifyWritingCommands(window);
            if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("The preview must not create a native application window.");

            window.ViewModel.InitializeAsync().GetAwaiter().GetResult();
            if (!window.ViewModel.IsLibraryVisible || window.ViewModel.Document is not null)
                throw new InvalidOperationException("Startup should display the library without opening a notebook.");
            // Detach the real compiled MainWindow.Content so its invisible Window
            // parent cannot affect rendering. Keep inherited presentation values.
            var content = (FrameworkElement)window.Content;
            window.Content = null;
            content.DataContext = window.ViewModel;
            content.Resources.MergedDictionaries.Add(window.Resources);
            TextElement.SetFontFamily(content, window.FontFamily);
            TextElement.SetFontSize(content, window.FontSize);
            TextElement.SetForeground(content, window.Foreground);
            content.UseLayoutRounding = window.UseLayoutRounding;
            content.SnapsToDevicePixels = window.SnapsToDevicePixels;

            var reports = new List<PreviewReport>();
            foreach (var state in new[] { "library", "library-empty", "library-search" })
            {
                repository.IncludeDocument = state != "library-empty";
                window.ViewModel.Search = state == "library-search" ? "no matching notebook" : "";
                window.ViewModel.RefreshLibraryAsync().GetAwaiter().GetResult();
                foreach (var (width, height) in new[] { (1400, 960), (1024, 700) })
                {
                    var fileName = $"ui-preview-{state}-{width}.png";
                    SaveImage(output, fileName, RenderElement(content, width, height));
                    VerifyDetached(content, window);
                    reports.Add(MeasureButtons(content, width, height, fileName, state, "HomeNewNotebookButton"));
                }
            }
            repository.IncludeDocument = true;
            window.ViewModel.Search = "";
            window.ViewModel.RefreshLibraryAsync().GetAwaiter().GetResult();
            window.ViewModel.OpenAsync(repository.Document.Id).GetAwaiter().GetResult();
            if (!window.ViewModel.IsEditorVisible)
                throw new InvalidOperationException("Opening a notebook should display the editor.");
            window.ViewModel.Status = "Saved on this device";
            var penButton = (Button)window.FindName("PenButton");
            penButton.Background = new SolidColorBrush(Color.FromRgb(237, 242, 254));
            penButton.Foreground = (Brush)application.Resources["Accent"];
            foreach (var page in window.ViewModel.Pages)
            {
                var editor = CreateEditor(page, repository);
                editor.LayoutTransform = Transform.Identity;
                page.Thumbnail = RenderElement(editor, page.Page.Width, page.Page.Height, .16);
            }

            foreach (var (width, height) in new[] { (1400, 960), (1024, 700) })
            foreach (var fitMode in new[] { "page", "width" })
            {
                Arrange(content, width, height);
                var viewport = (FrameworkElement)window.FindName("Viewport");
                var pageList = (ListBox)window.FindName("PageList");
                var page = window.ViewModel.SelectedPage!.Page;
                // Apply the shell's fit calculations as model values. These
                // checks exercise the resulting layout, without routed input.
                window.ViewModel.Zoom = fitMode == "width"
                    ? (viewport.ActualWidth - pageList.Padding.Left - pageList.Padding.Right - SystemParameters.VerticalScrollBarWidth - 4) / page.Width
                    : Math.Min((viewport.ActualWidth - 90) / page.Width, (viewport.ActualHeight - 106) / page.Height);
                ((Button)window.FindName("FitWidthButton")).Foreground = fitMode == "width"
                    ? (Brush)application.Resources["Accent"] : new SolidColorBrush(Color.FromRgb(104, 117, 138));
                Arrange(content, width, height);
                // Template bindings need not appear in ReadLocalValue; use the
                // resolved dimensions to identify the actual page-host Border.
                foreach (var host in Descendants<Border>(pageList).Where(b => b.DataContext is PageViewModel &&
                             double.IsFinite(b.Width) && double.IsFinite(b.Height)))
                {
                    var item = (PageViewModel)host.DataContext;
                    if (host.Child is not null) continue;
                    host.Child = CreateEditor(item, repository);
                }
                foreach (var editor in Descendants<PageEditor>(content))
                    editor.LayoutTransform = new ScaleTransform(window.ViewModel.Zoom, window.ViewModel.Zoom);
                if (!Descendants<PageEditor>(content).Any())
                    throw new InvalidOperationException("The preview did not initialize a visible page editor.");
                Arrange(content, width, height);
                VerifyDetached(content, window);
                var bitmap = RenderElement(content, width, height);
                var fileName = fitMode == "width" ? $"ui-preview-fit-width-{width}.png" : $"ui-preview-{width}.png";
                SaveImage(output, fileName, bitmap);
                var report = MeasureButtons(content, width, height, fileName, fitMode == "width" ? "editor-fit-width" : "editor", "PenButton", "PenSettingsButton", "FitWidthButton") with
                {
                    ViewportWidth = Round(viewport.ActualWidth), ViewportHeight = Round(viewport.ActualHeight),
                    Zoom = window.ViewModel.Zoom
                };
                if (fitMode == "width")
                {
                    var host = Descendants<Border>(pageList).FirstOrDefault(b => b.DataContext == window.ViewModel.SelectedPage && b.Child is PageEditor);
                    if (host is null) throw new InvalidOperationException("Fit Width did not realize the selected page editor.");
                    var bounds = host.TransformToAncestor(viewport).TransformBounds(new Rect(host.RenderSize));
                    var rightGutter = viewport.ActualWidth - bounds.Right;
                    if (bounds.Left < -.5 || rightGutter < -.5 || bounds.Left > 60 || rightGutter > 60 || bounds.Width < viewport.ActualWidth * .85)
                        throw new InvalidOperationException($"Fit Width should keep both page edges within the viewport with small gutters. Actual left: {bounds.Left:F2}, right: {rightGutter:F2}, page: {bounds.Width:F2}, viewport: {viewport.ActualWidth:F2} DIP.");
                    var editor = (PageEditor)host.Child;
                    var editorBounds = editor.TransformToAncestor(viewport).TransformBounds(new Rect(editor.RenderSize));
                    if (Math.Abs(editorBounds.Left - bounds.Left) > 1 || Math.Abs(editorBounds.Right - bounds.Right) > 1)
                        throw new InvalidOperationException("The Fit Width paper host and realized editor do not align.");
                    report = report with { PageLeftGutter = Round(bounds.Left), PageRightGutter = Round(rightGutter), PageDisplayWidth = Round(bounds.Width) };
                }
                reports.Add(report);
            }

            // Exercise the compiled focus chrome without changing WindowStyle or
            // creating a native window. Save-error UI remains separately visible.
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(MainWindow).GetField("_focusMode", flags)!.SetValue(window, true);
            typeof(MainWindow).GetMethod("ApplyFocusChrome", flags)!.Invoke(window, null);
            ((FrameworkElement)window.FindName("Sidebar")).Visibility = Visibility.Collapsed;
            ((ColumnDefinition)window.FindName("SidebarColumn")).Width = new GridLength(0);
            foreach (var (width, height) in new[] { (1400, 960), (1024, 700) })
            {
                var fileName = $"ui-preview-focus-{width}.png";
                SaveImage(output, fileName, RenderElement(content, width, height));
                VerifyDetached(content, window);
                reports.Add(MeasureButtons(content, width, height, fileName, "focus", "ExitFocusButton"));
                if (((FrameworkElement)window.FindName("Viewport")).ActualHeight < height - 1)
                    throw new InvalidOperationException("Focus mode must reclaim the header, tools and footer space.");
            }
            typeof(MainWindow).GetField("_focusMode", flags)!.SetValue(window, false);
            typeof(MainWindow).GetMethod("ApplyFocusChrome", flags)!.Invoke(window, null);

            var presetFixture = WritingPreferences.CreateDefault();
            presetFixture.Presets[0].Width = .5; presetFixture.Presets[0].Opacity = .65;
            var presetDialog = new PresetManagerDialog(null!, presetFixture);
            var presetContent = (FrameworkElement)presetDialog.Content; presetDialog.Content = null;
            TextElement.SetFontFamily(presetContent, window.FontFamily);
            TextElement.SetFontSize(presetContent, window.FontSize);
            TextElement.SetForeground(presetContent, window.Foreground);
            if (!(bool)typeof(PresetManagerDialog).GetMethod("StoreEditor", flags)!.Invoke(presetDialog, null)! ||
                presetDialog.Preferences.Presets[0].Width != .5 || presetDialog.Preferences.Presets[0].Opacity != .65)
                throw new InvalidOperationException("Opening and saving presets must preserve exact thickness and opacity.");
            var managerList = (ListBox)typeof(PresetManagerDialog).GetField("_list", flags)!.GetValue(presetDialog)!;
            managerList.SelectedIndex = 2;
            if (!(bool)typeof(PresetManagerDialog).GetMethod("CommitSelection", flags)!.Invoke(presetDialog, null)! ||
                presetDialog.Preferences.LastPresetId != presetDialog.Preferences.Presets[2].Id ||
                presetFixture.LastPresetId != presetFixture.Presets[0].Id)
                throw new InvalidOperationException("The manager must activate its chosen preset and keep the original preferences isolated.");
            managerList.SelectedIndex = 0;
            var presetSurface = new Border { Background = Brushes.White, Child = presetContent };
            SaveImage(output, "ui-preview-presets.png", RenderElement(presetSurface, 744, 620));
            VerifyDetached(presetSurface, presetDialog);
            reports.Add(MeasureButtons(presetSurface, 744, 620, "ui-preview-presets.png", "presets"));

            var picker = new PaperTemplatePicker { SelectedTemplate = PaperTemplate.Ruled };
            var pickerPanel = new StackPanel();
            pickerPanel.Children.Add(new TextBlock { Text = "Choose your paper", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
            pickerPanel.Children.Add(picker);
            var pickerContent = new Border { Padding = new Thickness(20), Background = Brushes.White, Child = pickerPanel };
            TextElement.SetFontFamily(pickerContent, window.FontFamily);
            TextElement.SetFontSize(pickerContent, window.FontSize);
            TextElement.SetForeground(pickerContent, window.Foreground);
            SaveImage(output, "ui-preview-paper-templates.png", RenderElement(pickerContent, 454, 374));
            VerifyDetached(pickerContent, window);
            if (Descendants<RadioButton>(picker).Count() != 6 || Descendants<RadioButton>(picker).Count(b => b.IsChecked == true) != 1)
                throw new InvalidOperationException("The paper picker must show six choices and one selected template.");
            reports.Add(MeasureButtons(pickerContent, 454, 374, "ui-preview-paper-templates.png", "paper-templates"));

            // A native owner is unnecessary for detached content and WPF rejects
            // assigning an owner that has never been shown. Null creates no HWND.
            var notebookDialog = new NotebookDialog(null!);
            var dialogContent = (FrameworkElement)notebookDialog.Content;
            notebookDialog.Content = null;
            TextElement.SetFontFamily(dialogContent, notebookDialog.FontFamily);
            TextElement.SetFontSize(dialogContent, notebookDialog.FontSize);
            TextElement.SetForeground(dialogContent, notebookDialog.Foreground);
            dialogContent.Language = notebookDialog.Language;
            if (dialogContent is Control dialogControl) dialogControl.Background = notebookDialog.Background;
            dialogContent.Measure(new Size(544, double.PositiveInfinity));
            var desiredDialogHeight = Math.Ceiling(dialogContent.DesiredSize.Height);
            var availableDialogHeight = Math.Floor(SystemParameters.WorkArea.Height - SystemParameters.WindowCaptionHeight - 2 * SystemParameters.ResizeFrameVerticalBorderWidth);
            var dialogHeight = (int)Math.Min(desiredDialogHeight, availableDialogHeight);
            if (dialogHeight <= 0 || dialogHeight > SystemParameters.WorkArea.Height)
                throw new InvalidOperationException("The new-notebook content must fit within the desktop work area.");
            SaveImage(output, "ui-preview-new-notebook.png", RenderElement(dialogContent, 544, dialogHeight));
            VerifyDetached(dialogContent, notebookDialog);
            VerifyDetached(content, window);
            if (Descendants<RadioButton>(dialogContent).Count() != 6 || !Descendants<Button>(dialogContent).Any(b => Equals(b.Content, "Create Notebook")))
                throw new InvalidOperationException("The new-notebook dialog must render its paper choices and create action.");
            reports.Add(MeasureButtons(dialogContent, 544, dialogHeight, "ui-preview-new-notebook.png", "new-notebook"));

            foreach (var (popupName, scene, width, requiredNames) in new[]
            {
                ("EraserSettingsPopup", "eraser-settings", 330, new[] { "PointEraseOption", "StrokeEraseOption" }),
                ("PenSettingsPopup", "pen-settings", 310, new[] { "HoldToStraightenToggle" })
            })
            {
                var popup = (Popup)window.FindName(popupName);
                if (popup.IsOpen) throw new InvalidOperationException("Settings previews must never open a native popup.");
                var popupContent = (FrameworkElement)popup.Child;
                popup.Child = null;
                TextElement.SetFontFamily(popupContent, window.FontFamily);
                TextElement.SetFontSize(popupContent, window.FontSize);
                TextElement.SetForeground(popupContent, window.Foreground);
                popupContent.Language = window.Language;
                // Match the initial remembered Pixel Eraser selection, as the
                // main-window Loaded handler is deliberately never dispatched.
                if (scene == "eraser-settings")
                {
                    var selected = (Button)window.FindName("PointEraseOption");
                    selected.Background = new SolidColorBrush(Color.FromRgb(237, 242, 254));
                    selected.Foreground = (Brush)application.Resources["Accent"];
                    selected.BorderBrush = (Brush)application.Resources["Accent"];
                    selected.BorderThickness = new Thickness(1);
                }
                popupContent.Measure(new Size(width, double.PositiveInfinity));
                var height = (int)Math.Ceiling(popupContent.DesiredSize.Height);
                if (height <= 0 || height > SystemParameters.WorkArea.Height)
                    throw new InvalidOperationException($"The {scene} popup must have a usable height within the work area.");
                var fileName = $"ui-preview-{scene}.png";
                SaveImage(output, fileName, RenderElement(popupContent, width, height));
                VerifyDetached(popupContent, window);
                reports.Add(MeasureButtons(popupContent, width, height, fileName, scene, requiredNames));
            }

            foreach (var report in reports)
                Console.WriteLine($"{report.Image}: {report.Width} x {report.Height} DIP; {report.Buttons.Count} buttons; " +
                    $"{report.Under44Dip.Count} below 44 DIP; {report.Overlaps.Count} overlaps; {report.Clipped.Count} clipped.");
            File.WriteAllText(Path.Combine(output, "ui-preview-layout.json"),
                JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            File.WriteAllText(Path.Combine(output, "ui-preview-layout.md"), Describe(reports), Encoding.UTF8);
            window.ViewModel.Dispose();
            if (reports.Any(report => report.Under44Dip.Count > 0 || report.Overlaps.Count > 0 || report.Clipped.Count > 0))
            {
                Console.Error.WriteLine("UI layout validation failed. See artifacts/ui-preview-layout.md for details.");
                return 1;
            }
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static void VerifyDetached(FrameworkElement content, Window window)
    {
        if (PresentationSource.FromVisual(content) is not null || new WindowInteropHelper(window).Handle != IntPtr.Zero)
            throw new InvalidOperationException("The preview unexpectedly became attached to a native window.");
    }

    private static void VerifyWritingCommands(MainWindow window)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        object? Get(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window);
        void Set(string name, object value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        object? Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        var defaults = WritingPreferences.CreateDefault();
        var blue = defaults.Presets[1]; blue.Opacity = .65;
        Call("ApplyPreset", blue, false);
        Set("_width", 4.75); Set("_color", Colors.Indigo);
        Call("SetTool", InkTool.Highlighter); Call("SetTool", InkTool.Pen);
        if (((WritingPreset)Get("_workingPreset")!).Id != blue.Id || (double)Get("_width")! != 4.75 ||
            (Color)Get("_color")! != Colors.Indigo || ((WritingPreset)Get("_workingPreset")!).Opacity != .65)
            throw new InvalidOperationException("Switching between pen and highlighter must recall the last working pen.");
        Set("_canSavePreferences", false); Set("_ready", true);
        Call("SetTool", InkTool.PointEraser);
        if ((long)Get("_preferencesRevision")! != 0)
            throw new InvalidOperationException("Read-only writing settings must not queue a save that blocks closing.");
        ((Task)Call("SavePreferencesAsync", true)!).GetAwaiter().GetResult();
        Set("_ready", false); Set("_canSavePreferences", true);
        ((FrameworkElement)window.FindName("PreferencesRetryButton")).Visibility = Visibility.Collapsed;
        Call("ApplyPreset", defaults.Presets[0], false);
    }

    private static void SaveImage(string output, string fileName, BitmapSource bitmap)
    {
        using var file = File.Create(Path.Combine(output, fileName));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(file);
    }

    private static ResourceDictionary ReadApplicationResources(string path)
    {
        var app = XDocument.Load(path).Root!;
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = new XElement(wpf + "ResourceDictionary",
            app.Attributes().Where(a => a.IsNamespaceDeclaration),
            app.Element(wpf + "Application.Resources")!.Elements());
        return (ResourceDictionary)XamlReader.Parse(resources.ToString());
    }

    private static PageEditor CreateEditor(PageViewModel item, FixtureRepository repository) => new(item.Page.Snapshot(), repository.GetAssetAsync)
    {
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        LayoutTransform = new ScaleTransform(item.Zoom, item.Zoom)
    };

    private static void Arrange(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static BitmapSource RenderElement(FrameworkElement element, double width, double height, double scale = 1)
    {
        Arrange(element, width, height);
        // Render directly. VisualBrush.AutoLayoutContent would remeasure a detached
        // root with unbounded size and change the next preview's viewport geometry.
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        bitmap.Freeze();
        return bitmap;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static PreviewReport MeasureButtons(FrameworkElement content, int width, int height, string image, string scene, params string[] requiredNames)
    {
        // IsVisible requires a presentation source and is false for this deliberately
        // detached tree. Check the declared visibility through all local ancestors.
        var visibleButtons = Descendants<ButtonBase>(content)
            .Where(b => b is Button or RadioButton or CheckBox)
            .Where(b => HasVisibleAncestors(b) && b.ActualWidth > 0 && b.ActualHeight > 0).ToList();
        var buttons = visibleButtons.Select(button =>
            {
                var bounds = button.TransformToAncestor(content).TransformBounds(new Rect(button.RenderSize));
                // This reads an attached label on an in-memory WPF object. It is
                // not a UI Automation client or an inspection of the desktop.
                var name = AutomationProperties.GetName(button);
                if (string.IsNullOrEmpty(name)) name = button.Content as string ?? button.ToolTip as string ?? button.Name;
                return new ButtonBounds(name, Round(bounds.X), Round(bounds.Y), Round(bounds.Width), Round(bounds.Height), button.IsEnabled);
            }).ToList();
        if (buttons.Count == 0 || requiredNames.Any(name => !visibleButtons.Any(button => button.Name == name)))
            throw new InvalidOperationException($"The required {scene} controls were not laid out; missing controls cannot count as a passing preview.");
        var overlaps = new List<string>();
        for (var i = 0; i < buttons.Count; i++)
        for (var j = i + 1; j < buttons.Count; j++)
        {
            var intersection = Rect.Intersect(buttons[i].Rect, buttons[j].Rect);
            if (!intersection.IsEmpty && intersection.Width > .5 && intersection.Height > .5)
                overlaps.Add($"{buttons[i].Name} / {buttons[j].Name}: {Round(intersection.Width)} × {Round(intersection.Height)} DIP");
        }
        return new PreviewReport(image, width, height, buttons,
            buttons.Where(b => b.Width < 43.99 || b.Height < 43.99).Select(b => b.Name).ToList(), overlaps,
            buttons.Where(b => b.X < -.5 || b.Y < -.5 || b.X + b.Width > width + .5 || b.Y + b.Height > height + .5).Select(b => b.Name).ToList()) { Scene = scene };
    }

    private static double Round(double value) => Math.Round(value, 2);

    private static bool HasVisibleAncestors(DependencyObject element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }

    private static string Describe(IEnumerable<PreviewReport> reports)
    {
        var text = new StringBuilder("# Moye WPF offscreen layout preview\n\nUses the actual MainWindow.Content, application resources, and PageEditor with an in-memory test fixture. No user database is accessed. No windows are displayed, no input is sent, and no UI Automation client or desktop capture is used.\n\nThis Measure / Arrange / RenderTargetBitmap preview checks layout only. It does not validate live UI interaction, touch, pen input, window DPI, popup menus, or the system title bar. Image dimensions describe the content area in DIP, rendered at 96 DPI.\n\n");
        foreach (var report in reports)
        {
            text.AppendLine($"## {report.Scene}: {report.Width} × {report.Height}\n\nImage: {report.Image}\n");
            if (report.Scene.StartsWith("editor", StringComparison.Ordinal)) text.AppendLine($"Viewport: {report.ViewportWidth} × {report.ViewportHeight} DIP. Zoom: {report.Zoom:P0}.\n");
            if (report.Scene == "editor-fit-width") text.AppendLine($"Page width: {report.PageDisplayWidth} DIP. Left gutter: {report.PageLeftGutter} DIP; right gutter: {report.PageRightGutter} DIP. Both page edges and the realized editor align within the viewport.\n");
            text.AppendLine($"Buttons: {report.Buttons.Count}; below 44 × 44 DIP: {report.Under44Dip.Count}; overlapping: {report.Overlaps.Count}; outside the content area: {report.Clipped.Count}.\n");
            foreach (var issue in report.Overlaps) text.AppendLine($"- Overlap: {issue}");
            foreach (var issue in report.Under44Dip) text.AppendLine($"- Below 44 DIP: {issue}");
            foreach (var issue in report.Clipped) text.AppendLine($"- Outside the content area: {issue}");
            text.AppendLine("\n| Button | X | Y | Width | Height | Enabled |\n|---|---:|---:|---:|---:|---|");
            foreach (var button in report.Buttons)
                text.AppendLine($"| {button.Name.Replace("|", "/")} | {button.X} | {button.Y} | {button.Width} | {button.Height} | {button.Enabled} |");
            text.AppendLine();
        }
        return text.ToString();
    }

    private sealed record ButtonBounds(string Name, double X, double Y, double Width, double Height, bool Enabled)
    {
        [System.Text.Json.Serialization.JsonIgnore] public Rect Rect => new(X, Y, Width, Height);
    }
    private sealed record PreviewReport(string Image, int Width, int Height, List<ButtonBounds> Buttons,
        List<string> Under44Dip, List<string> Overlaps, List<string> Clipped)
    {
        public string Scene { get; init; } = "";
        public double ViewportWidth { get; init; }
        public double ViewportHeight { get; init; }
        public double Zoom { get; init; }
        public double PageDisplayWidth { get; init; }
        public double PageLeftGutter { get; init; }
        public double PageRightGutter { get; init; }
    }

    private sealed class FixtureRepository : INotebookRepository
    {
        public bool IncludeDocument { get; set; } = true;
        public NotebookDocument Document { get; } = new()
        {
            Id = "offline-ui-preview-fixture", Title = "Everyday Ideas", Folder = "My Notebooks",
            Pages =
            [
                new()
                {
                    Template = PaperTemplate.Ruled,
                    Texts =
                    [
                        new() { X = 58, Y = 28, Width = 670, Height = 66, FontSize = 32, Text = "Sunday Ideas" },
                        new() { X = 58, Y = 108, Width = 670, Height = 240, FontSize = 22,
                            Text = "Put your ideas on paper. Make sense of them later.\n\nA few things to remember today\n• Keep a notebook ready for the next idea\n• Use a little color to organize your thoughts\n• Leave some space for your next project" },
                        new() { X = 58, Y = 410, Width = 670, Height = 70, FontSize = 18, Color = "#FF326AE8",
                            Text = "This local Moye test note is used to check offscreen layout." }
                    ]
                },
                new() { Template = PaperTemplate.Grid },
                new()
            ]
        };
        public NotebookSummary Summary => new() { Id = Document.Id, Title = Document.Title, Folder = Document.Folder, PageCount = Document.Pages.Count, ModifiedUtc = Document.ModifiedUtc };
        public Task InitializeAsync() => Task.CompletedTask;
        public Task<IReadOnlyList<NotebookSummary>> ListAsync() => Task.FromResult<IReadOnlyList<NotebookSummary>>(IncludeDocument ? [Summary] : []);
        public Task<NotebookDocument?> LoadAsync(string id) => Task.FromResult<NotebookDocument?>(id == Document.Id ? Document.Snapshot() : null);
        public Task SaveAsync(NotebookDocument document) => throw new InvalidOperationException("This preview fixture does not support persistence.");
        public Task DeleteAsync(string id) => throw new InvalidOperationException("This preview fixture does not support deletion.");
        public Task<AssetData> PutAssetAsync(string fileName, string contentType, byte[] bytes) => throw new InvalidOperationException("This preview fixture does not support asset writes.");
        public Task<AssetData> GetAssetAsync(string id) => throw new InvalidOperationException("The preview only contains paper and text and has no external assets.");
        public void Dispose() { }
    }
}
