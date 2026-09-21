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
            if (args.Contains("--interactive", StringComparer.Ordinal))
                return InteractivePreview.Run(ReadApplicationResources(Path.Combine(repositoryRoot, "src", "Moye", "App.xaml")),
                    output);
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
                    reports.Add(MeasureButtons(content, width, height, fileName, state,
                        state == "library" ? ["HomeNewNotebookButton", "DeleteNotebookButton"] : ["HomeNewNotebookButton"]));
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
                var report = MeasureButtons(content, width, height, fileName, fitMode == "width" ? "editor-fit-width" : "editor", "PenButton", "PenSettingsButton", "FitWidthButton", "AddSectionButton", "SectionOptionsButton") with
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

            VerifyTypingCommandsAndLayout(window, content, repository, output, reports);
            VerifySectionNavigationLayout(window, content, repository, output, reports);

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
            reports.Add(MeasureSliders(MeasureButtons(presetSurface, 744, 620, "ui-preview-presets.png", "presets", "Cancel", "Save and Use"), presetSurface));
            var presetScroll = Descendants<ScrollViewer>(presetContent).First(viewer => viewer.Content is StackPanel panel && Descendants<StrokeWidthPicker>(panel).Any());
            presetScroll.ScrollToBottom(); presetSurface.UpdateLayout();
            SaveImage(output, "ui-preview-presets-bottom.png", RenderElement(presetSurface, 744, 620));
            reports.Add(MeasureSliders(MeasureButtons(presetSurface, 744, 620, "ui-preview-presets-bottom.png", "presets-bottom", "Cancel", "Save and Use"), presetSurface));

            VerifyColorPickerLayout(output, reports);

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
                ("PenSettingsPopup", "pen-settings", 350, new[] { "HoldToStraightenToggle", "MoreInkColorsButton" })
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
                var report = MeasureButtons(popupContent, width, height, fileName, scene, requiredNames);
                reports.Add(scene == "pen-settings" ? MeasureSliders(report, popupContent) : report);
                if (scene == "pen-settings") VerifyWidthPreviews(window, popupContent, width, height, output, reports);
            }

            foreach (var report in reports)
                Console.WriteLine($"{report.Image}: {report.Width} x {report.Height} DIP; {report.Buttons.Count} buttons; " +
                    $"{report.Under44Dip.Count} below 44 DIP; {report.Overlaps.Count} overlaps; {report.Clipped.Count} clipped.");
            File.WriteAllText(Path.Combine(output, "ui-preview-layout.json"),
                JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            File.WriteAllText(Path.Combine(output, "ui-preview-layout.md"), Describe(reports), Encoding.UTF8);
            VerifyThumbnailScheduling(window, repository, output);
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

    private static void VerifyThumbnailScheduling(MainWindow window, FixtureRepository repository, string output)
    {
        // Exercise the real shell queue after all visual scenes. No native
        // window, touch injection, timers or real notebook storage are needed.
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        object? Call(string name, params object?[] arguments) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, arguments);
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Thumbnail scheduling: " + message); }
        void Tick() => ((Task)Call("ProcessThumbnailWorkAsync")!).GetAwaiter().GetResult();
        void Prepare(PageViewModel page) => ((Task)Call("PrepareSidebarThumbnailAsync", page)!).GetAwaiter().GetResult();

        window.ViewModel.ReplaceDocument(new NotebookDocument
        {
            Title = "Thumbnail scheduling fixture",
            Pages = Enumerable.Range(0, 3).Select(_ => new NotePage { Width = 240, Height = 320 }).ToList()
        }, true);
        Call("ResetThumbnailWork");
        Set("_viewportQuietAfter", 0L);
        var pages = window.ViewModel.Pages.ToArray();
        var prepared = Field<Dictionary<PageViewModel, PageEditor>>("_preparedThumbnails");
        var pending = Field<HashSet<PageViewModel>>("_pendingThumbnails");
        var dirty = Field<HashSet<PageEditor>>("_dirtyThumbnails");
        var editors = Field<Dictionary<Border, PageEditor>>("_editors");
        var touch = Field<TouchNavigationSession>("_touchNavigation");
        int Rendered() => pages.Count(page => page.Thumbnail is not null);
        foreach (var page in pages) Prepare(page);
        Check(prepared.Count == 3 && Rendered() == 0, "preparation must not render a bitmap.");

        touch.BeginContact(1, new Point(100, 200), 0);
        Tick();
        Check(Rendered() == 0 && prepared.Count == 3, "a resting finger must defer prepared bitmap work.");
        touch.MoveContact(1, new Point(100, 120), 40);
        Check(touch.HasPendingFrame, "the movement fixture must contain an unconsumed frame.");
        Tick();
        Check(Rendered() == 0 && prepared.Count == 3, "pending touch motion must defer prepared bitmap work.");
        Check(touch.TryTakeFrame(40, out _), "the movement fixture must produce a frame.");
        touch.EndContact(1, 40);
        Check(touch.IsInertiaActive, "the movement fixture must start inertia.");
        Tick();
        Check(Rendered() == 0 && prepared.Count == 3, "inertia must defer prepared bitmap work.");
        touch.Cancel();

        Set("_viewportPenDeviceId", 42);
        Tick();
        Check(Rendered() == 0, "the early pen-contact guard must defer bitmap work.");
        Set("_viewportPenDeviceId", null);
        var composingEditor = new PageEditor(pages[0].Page, repository.GetAssetAsync);
        var composingTexts = (HashSet<TextBox>)typeof(PageEditor).GetField("_composingTexts", flags)!.GetValue(composingEditor)!;
        composingTexts.Add(new TextBox());
        var composingHost = new Border(); editors.Add(composingHost, composingEditor);
        Tick();
        Check(Rendered() == 0, "text composition must defer bitmap work without committing the editor.");
        composingTexts.Clear(); editors.Remove(composingHost);

        for (var expected = 1; expected <= 3; expected++)
        {
            Tick();
            Check(Rendered() == expected && prepared.Count == 3 - expected,
                "each idle tick must render exactly one of the three prepared pages.");
        }

        // A later edit invalidates both an existing bitmap and a prepared
        // snapshot. Its dirty work must survive virtualization detaching it.
        var item = pages[0];
        var oldBitmap = item.Thumbnail;
        Prepare(item);
        var stalePrepared = prepared[item];
        item.Page.Texts.Add(new NoteText { Text = "A newer edit", X = 20, Y = 25, Width = 200, Height = 60 });
        var changedEditor = new PageEditor(item.Page, repository.GetAssetAsync);
        Call("InvalidateThumbnail", changedEditor);
        Call("QueueThumbnail", changedEditor);
        Check(item.Thumbnail is null && !prepared.ContainsKey(item) && pending.Contains(item),
            "an edit must discard cached and prepared thumbnails while retaining page work.");
        // Reproduce an old asynchronous image completion after invalidation.
        stalePrepared.SetPdfBackground(oldBitmap!);
        Check(!prepared.ContainsKey(item), "a stale asset completion must not revive the old prepared editor.");
        var host = new Border { DataContext = item, Child = changedEditor };
        editors.Add(host, changedEditor);
        Call("DetachPage", host);
        Check(!dirty.Contains(changedEditor) && pending.Contains(item) && host.Child is null,
            "detaching a dirty editor must retain the page's thumbnail work.");
        Tick();
        Check(item.Thumbnail is null && prepared.TryGetValue(item, out var currentPrepared) &&
            currentPrepared.Page.Texts.Single().Text == "A newer edit", "the next preparation must capture the newer page content.");
        Tick();
        Check(item.Thumbnail is not null && !ReferenceEquals(oldBitmap, item.Thumbnail),
            "the idle queue must regenerate the detached page's thumbnail.");
        Call("ResetThumbnailWork");
        stalePrepared.SetPdfBackground(oldBitmap!);
        Check(prepared.Count == 0 && pending.Count == 0, "reset generations must reject late asset completions.");

        const string result = "Thumbnail queue checks passed: touch contact, pending movement, inertia, pen contact and text composition defer rendering; three prepared pages render over three ticks; changed-page invalidation survives detachment; stale asset completions are rejected. These detached checks do not verify hardware gesture timing.";
        Console.WriteLine(result);
        File.WriteAllText(Path.Combine(output, "thumbnail-scheduling-checks.txt"), result + Environment.NewLine, Encoding.UTF8);
    }

    private static void VerifyColorPickerLayout(string output, List<PreviewReport> reports)
    {
        var initial = Color.FromArgb(160, 50, 106, 232);
        var dialog = new ColorPickerDialog(null!, initial, "Choose Ink Color");
        var content = (FrameworkElement)dialog.Content; dialog.Content = null;
        TextElement.SetFontFamily(content, dialog.FontFamily);
        TextElement.SetFontSize(content, dialog.FontSize);
        TextElement.SetForeground(content, dialog.Foreground);
        var surface = new Border { Background = Brushes.White, Child = content };
        var scroll = Descendants<ScrollViewer>(content).FirstOrDefault();
        // The visual tree does not realize the ScrollViewer until the first arrange.
        Arrange(surface, 464, 600);
        scroll ??= Descendants<ScrollViewer>(content).First();
        foreach (var (scene, width, height, custom, bottom) in new[]
        {
            ("color-picker", 464, 600, false, false),
            ("color-picker-custom", 464, 600, true, false),
            ("color-picker-compact", 404, 440, true, false),
            ("color-picker-compact-bottom", 404, 440, true, true)
        })
        {
            if (custom) { dialog.Picker.SetHue(286); dialog.Picker.SetSaturationValue(.66, .82); }
            else dialog.Picker.SelectColor(initial);
            if (dialog.SelectedColor != initial || dialog.Picker.InitialColor != initial || dialog.Picker.SelectedColor.A != initial.A)
                throw new InvalidOperationException("Color preview must preserve original alpha and keep the dialog result unchanged until Apply.");
            Arrange(surface, width, height);
            if (bottom) scroll.ScrollToBottom(); else scroll.ScrollToTop();
            surface.UpdateLayout();
            var image = $"ui-preview-{scene}.png";
            SaveImage(output, image, RenderElement(surface, width, height));
            VerifyDetached(surface, dialog);
            if (dialog.Picker.ColorField.ActualWidth > scroll.ViewportWidth + .5)
                throw new InvalidOperationException("The compact color field extends outside its horizontal scroll viewport.");
            reports.Add(MeasureSliders(MeasureButtons(surface, width, height, image, scene, "Apply", "Cancel"), surface));
        }
    }

    private static void VerifyWidthPreviews(MainWindow window, FrameworkElement content, int width, int height,
        string output, List<PreviewReport> reports)
    {
        var picker = (StrokeWidthPicker)window.FindName("WidthPicker");
        var coloredPixels = new Dictionary<string, int>();
        foreach (var (scene, strokeWidth, highlighter) in new[]
        {
            ("pen-settings-thin", WritingPreferences.MinimumWidth, false),
            ("pen-settings-broad", WritingPreferences.MaximumWidth, false),
            ("pen-settings-highlighter", WritingPreferences.MaximumWidth, true)
        })
        {
            picker.StrokeWidth = strokeWidth;
            picker.ConfigurePreview(highlighter ? Colors.Gold : Colors.Blue, highlighter, 1, !highlighter, true);
            var image = $"ui-preview-{scene}.png";
            var popupBitmap = RenderElement(content, width, height);
            SaveImage(output, image, popupBitmap);
            VerifyDetached(content, window);
            reports.Add(MeasureSliders(MeasureButtons(content, width, height, image, scene, "HoldToStraightenToggle", "MoreInkColorsButton"), content));
            var sample = Descendants<FrameworkElement>(picker).Single(element => element.GetType().Name == "InkSample");
            // Crop the arranged sample in place: re-arranging a child as a
            // standalone root would change the layout being tested next.
            var sampleBounds = sample.TransformToAncestor(content).TransformBounds(new Rect(sample.RenderSize));
            var bitmap = new CroppedBitmap(popupBitmap, new Int32Rect((int)Math.Ceiling(sampleBounds.X), (int)Math.Ceiling(sampleBounds.Y),
                (int)Math.Floor(sampleBounds.Width), (int)Math.Floor(sampleBounds.Height)));
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            var count = 0;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var blue = pixels[i]; var green = pixels[i + 1]; var red = pixels[i + 2];
                if (highlighter ? red > 220 && green > 150 && blue < 200 : blue > red + 40 && blue > green + 40) count++;
            }
            if (count == 0) throw new InvalidOperationException($"The {scene} preview did not render a colored ink sample.");
            coloredPixels[scene] = count;
        }
        if (coloredPixels["pen-settings-broad"] <= coloredPixels["pen-settings-thin"] * 3)
            throw new InvalidOperationException("The broad stroke preview must visibly differ from the thinnest stroke.");
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

    private static void VerifyTypingCommandsAndLayout(MainWindow window, FrameworkElement content, FixtureRepository repository,
        string output, List<PreviewReport> reports)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        var editors = (Dictionary<Border, PageEditor>)typeof(MainWindow).GetField("_editors", flags)!.GetValue(window)!;
        var pageList = (ListBox)window.FindName("PageList");
        var selected = window.ViewModel.SelectedPage!;
        var host = Descendants<Border>(pageList).First(b => b.DataContext == selected && b.Child is PageEditor);
        var original = host.Child;
        var page = selected.Page;
        var originalTexts = page.Texts;
        page.Texts = [new NoteText
        {
            X = 72, Y = 72, Width = 640, Height = 320, FontFamily = "Segoe UI", FontSize = 22,
            Text = "Lecture 07 — Linear Algebra\n\nEigenvalues and eigenvectors\nAv = λv\n\n• Review the worked example\n• Revisit this proof before the tutorial\n\n中文筆記也可以直接輸入。"
        }];
        var editor = new PageEditor(page, repository.GetAssetAsync);
        host.Child = editor;
        // Register only this synthetic editor. No UI Loaded handler, persistence,
        // native keyboard focus, clipboard or input injection is involved.
        editors[host] = editor;
        ((Task)Call("StartTypingAsync", false)!).GetAwaiter().GetResult();
        ((Task)Call("StartTypingAsync", false)!).GetAwaiter().GetResult();
        if (page.Texts.Count != 1 || editor.SelectedText != page.Texts[0])
            throw new InvalidOperationException("Type must resume existing text without adding duplicate boxes.");
        Call("ApplyTextFormatting", "Segoe UI", 24d, true, false, NoteTextAlignment.Left, Colors.DarkSlateBlue, true);
        if (editor.SelectedText is not { Bold: true, FontSize: 24, Alignment: NoteTextAlignment.Left })
            throw new InvalidOperationException("The typing toolbar must update the selected model's typography.");
        if (((FrameworkElement)window.FindName("TextToolbar")).Visibility != Visibility.Visible ||
            ((FrameworkElement)window.FindName("FavouriteToolbar")).Visibility != Visibility.Collapsed ||
            ((ComboBox)window.FindName("TextSizePicker")).Text != "18")
            throw new InvalidOperationException("Type must show contextual formatting with a point-based font size.");

        foreach (var (width, height) in new[] { (1400, 960), (1024, 700) })
        {
            Arrange(content, width, height);
            var viewport = (FrameworkElement)window.FindName("Viewport");
            window.ViewModel.Zoom = Math.Min((viewport.ActualWidth - 90) / page.Width, (viewport.ActualHeight - 106) / page.Height);
            editor.LayoutTransform = new ScaleTransform(window.ViewModel.Zoom, window.ViewModel.Zoom);
            var fileName = $"ui-preview-typing-{width}.png";
            SaveImage(output, fileName, RenderElement(content, width, height));
            VerifyDetached(content, window);
            reports.Add(MeasureButtons(content, width, height, fileName, "typing", "TypeButton", "AddTextBoxButton", "TextBoldButton", "TextItalicButton"));
            foreach (var name in new[] { "TextFontPicker", "TextSizePicker", "TextAlignmentPicker" })
            {
                var picker = (ComboBox)window.FindName(name);
                var bounds = picker.TransformToAncestor(content).TransformBounds(new Rect(picker.RenderSize));
                if (!HasVisibleAncestors(picker) || bounds.Width < 44 || bounds.Height < 44 || bounds.Right > width || bounds.Bottom > height)
                    throw new InvalidOperationException($"The typing picker {name} must be visible and at least 44 DIP.");
            }
        }
        Call("FinishTyping");
        if (((FrameworkElement)window.FindName("TextToolbar")).Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Finishing typing must restore the writing toolbar.");
        page.Texts.Clear();
        editor = new PageEditor(page, repository.GetAssetAsync);
        host.Child = editor; editors[host] = editor;
        ((Task)Call("StartTypingAsync", false)!).GetAwaiter().GetResult();
        if (page.Texts.Count != 1 || editor.SelectedText is not { Text.Length: 0, Bold: true, FontSize: 24 })
            throw new InvalidOperationException("Type on an empty page must create a blank box with typography only, never copied note content.");
        editor.LayoutTransform = new ScaleTransform(window.ViewModel.Zoom, window.ViewModel.Zoom);
        SaveImage(output, "ui-preview-typing-empty.png", RenderElement(content, 1024, 700));
        reports.Add(MeasureButtons(content, 1024, 700, "ui-preview-typing-empty.png", "typing-empty", "TypeButton", "AddTextBoxButton"));
        var box = Descendants<TextBox>(editor).Single();
        box.Text = string.Join("\n", Enumerable.Repeat("More lecture notes — continue on a new page", 70));
        editor.CommitPendingEdits(); Call("UpdateTextToolbar");
        if (!editor.HasTextOverflow || !((TextBlock)window.FindName("TypingHint")).Text.StartsWith("Text exceeds"))
            throw new InvalidOperationException("Overflowing text must remain editable and display a page-boundary warning.");
        SaveImage(output, "ui-preview-typing-overflow.png", RenderElement(content, 1024, 700));
        reports.Add(MeasureButtons(content, 1024, 700, "ui-preview-typing-overflow.png", "typing-overflow", "TypeButton", "AddTextBoxButton"));
        VerifyDetached(content, window);
        Call("FinishTyping"); editors.Clear(); page.Texts = originalTexts; host.Child = original;
    }

    private static void VerifySectionNavigationLayout(MainWindow window, FrameworkElement content, FixtureRepository repository,
        string output, List<PreviewReport> reports)
    {
        var original = window.ViewModel.SelectedSection!;
        var target = window.ViewModel.Sections.Last();
        window.ViewModel.SelectedSection = target;
        var sectionList = (ListBox)window.FindName("SectionList");
        var thumbnailList = (ListBox)window.FindName("ThumbnailList");
        var pageList = (ListBox)window.FindName("PageList");
        foreach (var (width, height) in new[] { (1400, 960), (1024, 700) })
        {
            Arrange(content, width, height);
            if (sectionList.SelectedItem != target || window.ViewModel.Pages.Any(p => p.Page.SectionId != target.Id) ||
                thumbnailList.Items.Count != window.ViewModel.Pages.Count)
                throw new InvalidOperationException("Section navigation must show only that section's page thumbnails and paper.");
            sectionList.ScrollIntoView(target);
            Arrange(content, width, height);
            foreach (var host in Descendants<Border>(pageList).Where(b => b.DataContext is PageViewModel &&
                         double.IsFinite(b.Width) && double.IsFinite(b.Height)))
                host.Child ??= CreateEditor((PageViewModel)host.DataContext, repository);
            foreach (var row in Descendants<ListBoxItem>(sectionList))
                if (row.ActualHeight < 44) throw new InvalidOperationException("Section rows must be at least 44 DIP tall.");
            if (thumbnailList.ActualHeight < 100)
                throw new InvalidOperationException("Sections must leave room to navigate page thumbnails at minimum window size.");
            var fileName = $"ui-preview-sections-{width}.png";
            SaveImage(output, fileName, RenderElement(content, width, height));
            VerifyDetached(content, window);
            reports.Add(MeasureButtons(content, width, height, fileName, "sections", "AddSectionButton", "SectionOptionsButton", "PageOptionsButton"));
        }
        window.ViewModel.SelectedSection = window.ViewModel.Sections.First(s => s.Id == "empty-topic");
        foreach (var (width, height) in new[] { (1400, 960), (1024, 700) })
        {
            var fileName = $"ui-preview-empty-section-{width}.png";
            SaveImage(output, fileName, RenderElement(content, width, height));
            reports.Add(MeasureButtons(content, width, height, fileName, "empty-section", "EmptySectionAddPageButton", "AddSectionButton"));
        }
        window.ViewModel.SelectedSection = original;
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
        var laidOutButtons = Descendants<ButtonBase>(content)
            .Where(b => b is Button or RadioButton or CheckBox)
            .Where(b => HasVisibleAncestors(b) && b.ActualWidth > 0 && b.ActualHeight > 0).ToList();
        var visibleButtons = laidOutButtons.Where(button => InsideScrollViewports(button, content)).ToList();
        var scrollHidden = laidOutButtons.Except(visibleButtons).Select(ControlName).ToList();
        var buttons = visibleButtons.Select(button =>
            {
                var bounds = button.TransformToAncestor(content).TransformBounds(new Rect(button.RenderSize));
                // This reads an attached label on an in-memory WPF object. It is
                // not a UI Automation client or an inspection of the desktop.
                return new ButtonBounds(ControlName(button), Round(bounds.X), Round(bounds.Y), Round(bounds.Width), Round(bounds.Height), button.IsEnabled);
            }).ToList();
        if (buttons.Count == 0 || requiredNames.Any(name => !visibleButtons.Any(button => button.Name == name || ControlName(button) == name)))
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
            buttons.Where(b => b.X < -.5 || b.Y < -.5 || b.X + b.Width > width + .5 || b.Y + b.Height > height + .5).Select(b => b.Name).ToList()) { Scene = scene, ScrollHiddenControls = scrollHidden };
    }

    private static PreviewReport MeasureSliders(PreviewReport report, FrameworkElement content)
    {
        var sliders = new List<SliderBounds>();
        foreach (var slider in Descendants<Slider>(content).Where(s => HasVisibleAncestors(s) && s.ActualWidth > 0 && s.ActualHeight > 0))
        {
            var name = ControlName(slider);
            if (!InsideScrollViewports(slider, content))
            {
                report.ScrollHiddenControls.Add(name);
                continue;
            }
            var bounds = slider.TransformToAncestor(content).TransformBounds(new Rect(slider.RenderSize));
            var thumb = (slider.Template.FindName("PART_Track", slider) as Track)?.Thumb ?? Descendants<Thumb>(slider).Single();
            var thumbBounds = thumb.TransformToAncestor(content).TransformBounds(new Rect(thumb.RenderSize));
            var item = new SliderBounds(name, Round(bounds.X), Round(bounds.Y), Round(bounds.Width), Round(bounds.Height),
                Round(thumbBounds.Width), Round(thumbBounds.Height), slider.Value, slider.Minimum, slider.Maximum);
            sliders.Add(item);
            if (bounds.Width < 43.99 || bounds.Height < 43.99) report.Under44Dip.Add(name);
            if (thumbBounds.Width < 43.99 || thumbBounds.Height < 43.99) report.Under44Dip.Add($"{name} thumb");
            if (bounds.Left < -.5 || bounds.Top < -.5 || bounds.Right > report.Width + .5 || bounds.Bottom > report.Height + .5)
                report.Clipped.Add(name);
            if (thumbBounds.Left < bounds.Left - .5 || thumbBounds.Top < bounds.Top - .5 || thumbBounds.Right > bounds.Right + .5 || thumbBounds.Bottom > bounds.Bottom + .5)
                report.Clipped.Add($"{name} thumb outside slider");
            foreach (var button in report.Buttons)
            {
                var intersection = Rect.Intersect(bounds, button.Rect);
                if (!intersection.IsEmpty && intersection.Width > .5 && intersection.Height > .5)
                    report.Overlaps.Add($"{name} / {button.Name}: {Round(intersection.Width)} × {Round(intersection.Height)} DIP");
            }
        }
        return report with { Sliders = sliders };
    }

    private static string ControlName(FrameworkElement control)
    {
        // An attached label on a detached object, not a UI Automation query.
        var name = AutomationProperties.GetName(control);
        if (!string.IsNullOrEmpty(name)) return name;
        return (control as ContentControl)?.Content as string ?? control.ToolTip as string ??
            (string.IsNullOrEmpty(control.Name) ? control.GetType().Name : control.Name);
    }

    private static bool InsideScrollViewports(FrameworkElement element, FrameworkElement root)
    {
        var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        for (DependencyObject? ancestor = VisualTreeHelper.GetParent(element); ancestor is not null && ancestor != root;
             ancestor = VisualTreeHelper.GetParent(ancestor))
        {
            if (ancestor is not ScrollContentPresenter presenter) continue;
            var viewport = presenter.TransformToAncestor(root).TransformBounds(new Rect(presenter.RenderSize));
            // Intentional scrolling is measured in separate top/bottom scenes;
            // hidden or partially revealed controls are not root-layout overlaps.
            if (bounds.Left < viewport.Left - .5 || bounds.Top < viewport.Top - .5 ||
                bounds.Right > viewport.Right + .5 || bounds.Bottom > viewport.Bottom + .5) return false;
        }
        return true;
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
        var text = new StringBuilder("# Moye WPF offscreen layout preview\n\nUses the actual MainWindow.Content, application resources, and PageEditor with an in-memory test fixture. No user database is accessed. No windows are displayed, no input is sent, and no UI Automation client or desktop capture is used.\n\nThis Measure / Arrange / RenderTargetBitmap preview checks layout only. It does not validate live UI interaction, touch, pen input, window DPI, popup menus, or the system title bar. Image dimensions describe the content area in DIP, rendered at 96 DPI. Controls outside an intentional scroll viewport are listed separately; dialog top/bottom scenes check the revealed controls and fixed action buttons. Slider geometry is checked for the new color and thickness controls.\n\n");
        foreach (var report in reports)
        {
            text.AppendLine($"## {report.Scene}: {report.Width} × {report.Height}\n\nImage: {report.Image}\n");
            if (report.Scene.StartsWith("editor", StringComparison.Ordinal)) text.AppendLine($"Viewport: {report.ViewportWidth} × {report.ViewportHeight} DIP. Zoom: {report.Zoom:P0}.\n");
            if (report.Scene == "editor-fit-width") text.AppendLine($"Page width: {report.PageDisplayWidth} DIP. Left gutter: {report.PageLeftGutter} DIP; right gutter: {report.PageRightGutter} DIP. Both page edges and the realized editor align within the viewport.\n");
            text.AppendLine($"Buttons: {report.Buttons.Count}; sliders: {report.Sliders.Count}; below 44 × 44 DIP: {report.Under44Dip.Count}; overlapping: {report.Overlaps.Count}; outside the content area: {report.Clipped.Count}.\n");
            if (report.ScrollHiddenControls.Count > 0) text.AppendLine($"Outside the current scroll viewport: {string.Join(", ", report.ScrollHiddenControls)}.\n");
            foreach (var issue in report.Overlaps) text.AppendLine($"- Overlap: {issue}");
            foreach (var issue in report.Under44Dip) text.AppendLine($"- Below 44 DIP: {issue}");
            foreach (var issue in report.Clipped) text.AppendLine($"- Outside the content area: {issue}");
            text.AppendLine("\n| Button | X | Y | Width | Height | Enabled |\n|---|---:|---:|---:|---:|---|");
            foreach (var button in report.Buttons)
                text.AppendLine($"| {button.Name.Replace("|", "/")} | {button.X} | {button.Y} | {button.Width} | {button.Height} | {button.Enabled} |");
            if (report.Sliders.Count > 0)
            {
                text.AppendLine("\n| Slider | Width | Height | Thumb width | Thumb height | Value | Range |\n|---|---:|---:|---:|---:|---:|---|");
                foreach (var slider in report.Sliders)
                    text.AppendLine($"| {slider.Name} | {slider.Width} | {slider.Height} | {slider.ThumbWidth} | {slider.ThumbHeight} | {slider.Value} | {slider.Minimum}–{slider.Maximum} |");
            }
            text.AppendLine();
        }
        return text.ToString();
    }

    private sealed record ButtonBounds(string Name, double X, double Y, double Width, double Height, bool Enabled)
    {
        [System.Text.Json.Serialization.JsonIgnore] public Rect Rect => new(X, Y, Width, Height);
    }
    private sealed record SliderBounds(string Name, double X, double Y, double Width, double Height,
        double ThumbWidth, double ThumbHeight, double Value, double Minimum, double Maximum);
    private sealed record PreviewReport(string Image, int Width, int Height, List<ButtonBounds> Buttons,
        List<string> Under44Dip, List<string> Overlaps, List<string> Clipped)
    {
        public string Scene { get; init; } = "";
        public List<SliderBounds> Sliders { get; init; } = [];
        public List<string> ScrollHiddenControls { get; init; } = [];
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
            Id = "offline-ui-preview-fixture", Title = "Mathematics", Folder = "Semester 1",
            Sections = [new() { Id = "algebra", Title = "Linear Algebra" }, new() { Id = "calculus", Title = "Calculus" }, new() { Id = "empty-topic", Title = "Next Lecture" },
                new() { Id = "revision", Title = "Revision — Important formulas and worked tutorial examples" }],
            Pages =
            [
                new()
                {
                    SectionId = "algebra", Template = PaperTemplate.Ruled,
                    Texts =
                    [
                        new() { X = 58, Y = 28, Width = 670, Height = 66, FontSize = 32, Text = "Sunday Ideas" },
                        new() { X = 58, Y = 108, Width = 670, Height = 240, FontSize = 22,
                            Text = "Put your ideas on paper. Make sense of them later.\n\nA few things to remember today\n• Keep a notebook ready for the next idea\n• Use a little color to organize your thoughts\n• Leave some space for your next project" },
                        new() { X = 58, Y = 410, Width = 670, Height = 70, FontSize = 18, Color = "#FF326AE8",
                            Text = "This local Moye test note is used to check offscreen layout." }
                    ]
                },
                new() { SectionId = "algebra", Template = PaperTemplate.Grid },
                new() { SectionId = "algebra" },
                new() { SectionId = "calculus", Template = PaperTemplate.Graph },
                new() { SectionId = "revision", Template = PaperTemplate.Cornell,
                    Texts = [new() { Text = "Midterm revision\n\n• Eigenvalues\n• Matrix operations", X = 200, Y = 80, Width = 500 }] }
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
