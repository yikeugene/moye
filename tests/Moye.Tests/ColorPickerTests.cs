using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Moye.Controls;

namespace Moye.Tests;

/// <summary>Color math and detached WPF controls; physical pen/touch delivery is a separate device check.</summary>
public sealed class ColorPickerTests
{
    [Theory]
    [InlineData(0, 255, 0, 0)]
    [InlineData(60, 255, 255, 0)]
    [InlineData(120, 0, 255, 0)]
    [InlineData(180, 0, 255, 255)]
    [InlineData(240, 0, 0, 255)]
    [InlineData(300, 255, 0, 255)]
    [InlineData(360, 255, 0, 0)]
    [InlineData(-60, 255, 0, 255)]
    [InlineData(420, 255, 255, 0)]
    public void HueSectorsAndWrapProduceExpectedSrgb(double hue, byte red, byte green, byte blue)
    {
        Assert.Equal(Color.FromArgb(83, red, green, blue), new HsvColor(hue, 1, 1).ToColor(83));
    }

    [Fact]
    public void ColorConversionRoundTripsRgbIncludingGrayBlackAndTransparentChannels()
    {
        byte[] channels = [0, 1, 37, 73, 128, 201, 254, 255];
        foreach (var r in channels)
        foreach (var g in channels)
        foreach (var b in channels)
        {
            var source = Color.FromArgb(0, r, g, b);
            Assert.Equal(source, HsvColor.FromColor(source).ToColor(source.A));
        }
    }

    [Fact]
    public void NonfiniteAndOutOfBoundsValuesCannotProduceInvalidColors()
    {
        Assert.Equal(Colors.White, new HsvColor(double.NaN, double.PositiveInfinity, 1).ToColor());
        Assert.Equal(Colors.Black, new HsvColor(double.NegativeInfinity, 1, double.NaN).ToColor());
        Assert.Equal(Colors.White, new HsvColor(30, -2, 10).ToColor());
        Assert.Equal(Colors.Red, new HsvColor(0, 2, 1).ToColor());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(73)]
    [InlineData(255)]
    public void PaletteHueAndColorFieldAllPreserveTheOriginalAlpha(byte alpha)
    {
        Sta(() =>
        {
            var picker = new ColorPickerSurface(Color.FromArgb(alpha, 40, 80, 120));
            var changes = 0;
            picker.ColorChanged += (_, _) => changes++;
            picker.SelectColor(Color.FromArgb(255, 229, 72, 77));
            Assert.Equal(Color.FromArgb(alpha, 229, 72, 77), picker.SelectedColor);
            picker.SetSaturationValue(1, 1);
            picker.SetHue(120);
            Assert.Equal(Color.FromArgb(alpha, 0, 255, 0), picker.SelectedColor);
            Assert.Equal(3, changes);
            picker.SetHue(120);
            picker.SetSaturationValue(1, 1);
            Assert.Equal(3, changes);
        });
    }

    [Fact]
    public void AchromaticSelectionRetainsHueForLaterSaturationChanges()
    {
        Sta(() =>
        {
            var picker = new ColorPickerSurface(Colors.Blue);
            picker.SetSaturationValue(0, .5);
            Assert.Equal(Color.FromRgb(128, 128, 128), picker.SelectedColor);
            var changes = 0;
            picker.ColorChanged += (_, _) => changes++;
            picker.SetHue(120);
            Assert.Equal(0, changes);
            Assert.Equal(120, picker.Hsv.Hue);
            Assert.Equal(120, picker.HueSlider.Value);
            picker.SetSaturationValue(1, .5);
            Assert.Equal(Color.FromRgb(0, 128, 0), picker.SelectedColor);
            picker.SelectColor(Colors.White);
            Assert.Equal(120, picker.Hsv.Hue);
        });
    }

    [Fact]
    public void HueSliderAtRedEndDoesNotJumpItsThumbBackToTheStart()
    {
        Sta(() =>
        {
            var picker = new ColorPickerSurface(Colors.Red);
            picker.HueSlider.Value = 360;
            Assert.Equal(360, picker.HueSlider.Value);
            Assert.Equal(0, picker.Hsv.Hue);
            Assert.Equal(Colors.Red, picker.SelectedColor);
        });
    }

    [Fact]
    public void FieldCoordinatesAndKeyboardStepsMapToClampedColorValues()
    {
        Sta(() =>
        {
            var picker = new ColorPickerSurface(Color.FromArgb(96, 255, 0, 0));
            Layout(picker, 420, 560);
            var field = picker.ColorField;
            field.SelectPoint(new Point(field.ActualWidth / 4, field.ActualHeight / 4));
            Assert.Equal(.25, field.Saturation, 5);
            Assert.Equal(.75, field.Value, 5);
            Assert.Equal(Color.FromArgb(96, 191, 143, 143), picker.SelectedColor);
            field.MoveSelection(.01, -.01);
            Assert.Equal(.26, field.Saturation, 5);
            Assert.Equal(.74, field.Value, 5);
            field.MoveSelection(.1, .1);
            Assert.Equal(.36, field.Saturation, 5);
            Assert.Equal(.84, field.Value, 5);
            field.SelectPoint(new Point(-100, field.ActualHeight + 100));
            Assert.Equal(Color.FromArgb(96, 0, 0, 0), picker.SelectedColor);
            field.SelectPoint(new Point(field.ActualWidth + 100, -100));
            Assert.Equal(Color.FromArgb(96, 255, 0, 0), picker.SelectedColor);
        });
    }

    [Fact]
    public void UnarrangedColorFieldIgnoresCoordinatesUntilItsSizeIsKnown()
    {
        Sta(() =>
        {
            var picker = new ColorPickerSurface(Colors.Blue);
            picker.ColorField.SelectPoint(new Point(50, 50));
            Assert.Equal(Colors.Blue, picker.SelectedColor);
        });
    }

    [Fact]
    public void PlaneRenderingMatchesTheSrgbColorAtItsCenter()
    {
        Sta(() =>
        {
            var field = new SaturationValuePlane();
            field.SetColor(new HsvColor(120, 0, 1));
            Layout(field, 200, 100);
            var bitmap = new RenderTargetBitmap(200, 100, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(field);
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect(100, 50, 1, 1), pixel, 4, 0);
            Assert.InRange(pixel[0], 60, 67);
            Assert.InRange(pixel[1], 124, 131);
            Assert.InRange(pixel[2], 60, 67);
            Assert.Equal(255, pixel[3]);
        });
    }

    [Fact]
    public void DialogEditsRemainPrivateUntilApplyAndCurrentSwatchRestoresOriginal()
    {
        Sta(() =>
        {
            var initial = Color.FromArgb(55, 51, 102, 153);
            var dialog = new ColorPickerDialog(null!, initial);
            Assert.Equal(IntPtr.Zero, new WindowInteropHelper(dialog).Handle);
            dialog.Picker.SelectColor(Colors.Orange);
            Assert.NotEqual(initial, dialog.Picker.SelectedColor);
            Assert.Equal(initial, dialog.SelectedColor);
            Assert.Null(dialog.DialogResult);
            var current = Descendants<Button>(dialog.Picker).Single(button => AutomationProperties.GetName(button) == "Restore current color");
            current.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(initial, dialog.Picker.SelectedColor);
            Assert.Equal(initial, dialog.SelectedColor);
            Assert.Empty(Descendants<TextBox>(dialog.Picker));
            Assert.Equal(IntPtr.Zero, new WindowInteropHelper(dialog).Handle);
        });
    }

    [Theory]
    [InlineData(420, 560)]
    [InlineData(343, 560)]
    public void PaletteAndHueHaveAccessibleNamesAndFortyFourDipTargets(double width, double height)
    {
        Sta(() =>
        {
            var picker = new ColorPickerSurface(Colors.DarkBlue);
            Layout(picker, width, height);
            var buttons = Descendants<Button>(picker).ToArray();
            Assert.Equal(17, buttons.Length);
            Assert.All(buttons, button =>
            {
                Assert.True(button.ActualHeight >= 44);
                Assert.True(button.ActualWidth >= 44);
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
            });
            Assert.Equal("Saturation and brightness", AutomationProperties.GetName(picker.ColorField));
            Assert.Equal("Hue", AutomationProperties.GetName(picker.HueSlider));
            Assert.True(picker.ColorField.Focusable);
            Assert.True(picker.HueSlider.ActualHeight >= 44);
            var thumb = Assert.Single(Descendants<Thumb>(picker.HueSlider));
            Assert.Equal(44, thumb.ActualWidth);
            Assert.Equal(44, thumb.ActualHeight);
            Assert.True(picker.ColorField.ActualWidth <= width);
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void Layout(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
    }
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Color picker test exceeded 30 seconds.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
