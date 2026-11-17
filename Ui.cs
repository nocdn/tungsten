using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Tungsten;

static class Ui
{
    public static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static SolidColorBrush B(uint argb)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        b.Freeze();
        return b;
    }

    public static readonly SolidColorBrush Fg = B(0xFFFFFFFF), Fg2 = B(0xB8FFFFFF), Fg3 = B(0x80FFFFFF), Black = B(0xFF101010),
        Ink2 = B(0xFF4A4A4A), ItemBg = B(0x1CFFFFFF), Stroke = B(0x2EFFFFFF), Green = B(0xFF7CD86E), DarkGreen = B(0xFF107C10),
        XboxGreen = B(0xFF107C10), Red = B(0xFFFF7A7A);

    public static TextBlock Text(string s, double size, Brush fg, FontWeight? w = null) => new()
    {
        Text = s, FontSize = size, Foreground = fg, FontWeight = w ?? FontWeights.Normal, FontFamily = Font,
        TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
    };

    public static TextBlock Glyph(string g, double size, Brush fg) => new()
    {
        Text = g, FontSize = size, Foreground = fg, FontFamily = Icons,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    public static void Anim(IAnimatable target, DependencyProperty dp, double to, int ms, double? from = null)
    {
        var a = new DoubleAnimation { To = to, Duration = TimeSpan.FromMilliseconds(ms), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        if (from.HasValue) a.From = from;
        target.BeginAnimation(dp, a);
    }

    /// <summary>Controller button badge in Xbox colours.</summary>
    public static FrameworkElement Badge(string btn)
    {
        var (bg, fg, text, glyph) = btn switch
        {
            "A" => (B(0xFF3FA535), Fg, "A", false),
            "B" => (B(0xFFD8393B), Fg, "B", false),
            "X" => (B(0xFF2C7FE0), Fg, "X", false),
            "Y" => (B(0xFFEAB717), Black, "Y", false),
            "Menu" => (B(0xFF454545), Fg, "", true),
            "View" => (B(0xFF454545), Fg, "", true),
            _ => (B(0xFF454545), Fg, btn, false),
        };
        FrameworkElement child = glyph
            ? Glyph(text, 10, fg)
            : new TextBlock
            {
                Text = text, FontSize = text.Length > 1 ? 10 : 12, FontWeight = FontWeights.Bold, Foreground = fg, FontFamily = Font,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
        return new Border
        {
            Width = text.Length > 1 && !glyph ? 30 : 22, Height = 22, CornerRadius = new CornerRadius(11), Background = bg, Child = child,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    public static FrameworkElement Hint(string btn, string text)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 0, 0) };
        sp.Children.Add(Badge(btn));
        var t = Text(text, 13, Fg2);
        t.Margin = new Thickness(6, 0, 0, 0);
        sp.Children.Add(t);
        return sp;
    }
}
