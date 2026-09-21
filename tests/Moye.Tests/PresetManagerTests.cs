using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Moye.Controls;
using Moye.Models;

namespace Moye.Tests;

/// <summary>Detached dialog state tests; no modal window or native input is synthesized.</summary>
public sealed class PresetManagerTests
{
    [Fact]
    public void LoadingAndSavingKeepsExactDipWidthAndExistingAlphaColor()
    {
        Sta(() =>
        {
            var preferences = WritingPreferences.CreateDefault();
            preferences.Presets[0].Width = 1.7008123456789;
            preferences.Presets[0].Color = "#AA1234AB";
            var dialog = new PresetManagerDialog(null!, preferences);
            Assert.Equal(IntPtr.Zero, new WindowInteropHelper(dialog).Handle);
            Assert.IsType<Button>(Field<object>(dialog, "_color"));
            Assert.Equal(preferences.Presets[0].Width, Field<StrokeWidthPicker>(dialog, "_width").StrokeWidth);
            Assert.Equal(Color.FromArgb(0xAA, 0x12, 0x34, 0xAB),
                Assert.IsType<SolidColorBrush>(Field<Border>(dialog, "_colorSwatch").Background).Color);

            Assert.True((bool)Invoke(dialog, "CommitSelection")!);
            Assert.Equal(preferences.Presets[0].Width, dialog.Preferences.Presets[0].Width);
            Assert.Equal("#AA1234AB", dialog.Preferences.Presets[0].Color);
            Assert.NotSame(preferences, dialog.Preferences);
            Assert.NotSame(preferences.Presets[0], dialog.Preferences.Presets[0]);
            dialog.Close();
        });
    }

    [Fact]
    public void WidthAndColorEditsRemainPrivateWhenSwitchingPresetsAndCanceling()
    {
        Sta(() =>
        {
            var preferences = WritingPreferences.CreateDefault();
            var original = preferences.Presets[0].Snapshot();
            var dialog = new PresetManagerDialog(null!, preferences);
            Field<StrokeWidthPicker>(dialog, "_width").StrokeWidth = 7.123456789;
            Field<TextBox>(dialog, "_name").Text = "Class notes 課堂筆記";
            // The nested color picker returns a Color only after its Apply action.
            Invoke(dialog, "SetSelectedColor", Color.FromArgb(170, 21, 90, 170));
            Field<ListBox>(dialog, "_list").SelectedItem = dialog.Preferences.Presets[1];

            Assert.Equal(7.123456789, dialog.Preferences.Presets[0].Width);
            Assert.Equal("#AA155AAA", dialog.Preferences.Presets[0].Color);
            Assert.Equal("Class notes 課堂筆記", dialog.Preferences.Presets[0].Name);
            Assert.Equal(preferences.Presets[1].Width, Field<StrokeWidthPicker>(dialog, "_width").StrokeWidth);
            dialog.Close(); // The caller does not adopt Preferences after Cancel/close.

            Assert.Equal(original.Width, preferences.Presets[0].Width);
            Assert.Equal(original.Color, preferences.Presets[0].Color);
            Assert.Equal(original.Name, preferences.Presets[0].Name);
            Assert.Equal(original.Id, preferences.LastPresetId);
        });
    }

    [Fact]
    public void DuplicatingAndReorderingRetainsTheChosenColorWidthAndTool()
    {
        Sta(() =>
        {
            var preferences = WritingPreferences.CreateDefault();
            var dialog = new PresetManagerDialog(null!, preferences);
            Field<StrokeWidthPicker>(dialog, "_width").StrokeWidth = 12.375;
            Invoke(dialog, "SetSelectedColor", Colors.Coral);
            Field<ComboBox>(dialog, "_tool").SelectedIndex = 1;
            Invoke(dialog, "Duplicate");
            var copy = dialog.Preferences.Presets[1];
            Assert.NotEqual(preferences.Presets[0].Id, copy.Id);
            Assert.Equal(12.375, copy.Width);
            Assert.Equal(Colors.Coral.ToString(), copy.Color);
            Assert.Equal(InkTool.Highlighter, copy.Tool);
            Assert.Equal(.5, copy.Opacity);
            Assert.False(Field<TextBox>(dialog, "_opacity").IsEnabled);
            Invoke(dialog, "Move", -1);
            Assert.Same(copy, dialog.Preferences.Presets[0]);
            Assert.True((bool)Invoke(dialog, "CommitSelection")!);
            Assert.Equal(copy.Id, dialog.Preferences.LastPresetId);
            Assert.Equal(4, preferences.Presets.Count);
            Assert.Equal("default-black-pen", preferences.Presets[0].Id);
            dialog.Close();
        });
    }

    [Fact]
    public void LivePreviewTracksColorOpacityToolPressureAndSmoothingBeforeSaving()
    {
        Sta(() =>
        {
            var preferences = WritingPreferences.CreateDefault();
            var dialog = new PresetManagerDialog(null!, preferences);
            var picker = Field<StrokeWidthPicker>(dialog, "_width");
            var sample = typeof(StrokeWidthPicker).GetField("_sample", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(picker)!;
            T Preview<T>(string property) => (T)sample.GetType().GetProperty(property)!.GetValue(sample)!;

            var color = Color.FromArgb(204, 15, 110, 150);
            Invoke(dialog, "SetSelectedColor", color);
            Field<TextBox>(dialog, "_opacity").Text = "37";
            Field<CheckBox>(dialog, "_pressure").IsChecked = false;
            Field<CheckBox>(dialog, "_smoothing").IsChecked = false;
            picker.StrokeWidth = 4.75;
            Assert.Equal(color, Preview<Color>("Color"));
            Assert.Equal(.37, Preview<double>("InkOpacity"));
            Assert.False(Preview<bool>("Highlighter"));
            Assert.False(Preview<bool>("Pressure"));
            Assert.False(Preview<bool>("Smoothing"));
            Assert.Equal(4.75, Preview<double>("StrokeWidth"));

            Field<ComboBox>(dialog, "_tool").SelectedIndex = 1;
            Assert.True(Preview<bool>("Highlighter"));
            Assert.Equal(.5, Preview<double>("InkOpacity"));
            Field<ComboBox>(dialog, "_tool").SelectedIndex = 0;
            Field<CheckBox>(dialog, "_pressure").IsChecked = true;
            Field<CheckBox>(dialog, "_smoothing").IsChecked = true;
            Assert.False(Preview<bool>("Highlighter"));
            Assert.Equal(.37, Preview<double>("InkOpacity"));
            Assert.True(Preview<bool>("Pressure"));
            Assert.True(Preview<bool>("Smoothing"));
            // Merely refreshing the preview must not apply the working preset.
            Assert.Equal(preferences.Presets[0].Width, dialog.Preferences.Presets[0].Width);
            Assert.Equal(preferences.Presets[0].Color, dialog.Preferences.Presets[0].Color);
            dialog.Close();
        });
    }

    private static T Field<T>(PresetManagerDialog dialog, string name) =>
        (T)typeof(PresetManagerDialog).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;

    private static object? Invoke(PresetManagerDialog dialog, string name, params object[] arguments) =>
        typeof(PresetManagerDialog).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, arguments);

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Preset manager test exceeded 30 seconds.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
