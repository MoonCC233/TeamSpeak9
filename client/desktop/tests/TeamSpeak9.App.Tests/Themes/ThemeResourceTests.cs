// TeamSpeak9 - PC client
// Licensed under the terms in the repository root.

using System.Windows;
using System.Windows.Media;
using TeamSpeak9.App.Converters;
using TeamSpeak9.App.Tests.Infrastructure;

namespace TeamSpeak9.App.Tests.Themes;

/// <summary>
/// Loads the shipped theme dictionaries and checks the contract the XAML relies on.
/// </summary>
/// <remarks>
/// XAML is resolved at run time, so a wrong resource type or a typo in a key only shows up as a
/// crash when the user opens the affected view. Loading the dictionaries here turns that into a
/// build-time failure.
/// </remarks>
public class ThemeResourceTests
{
    private const string Prefix = "/TeamSpeak9;component/Themes/";

    static ThemeResourceTests()
    {
        // Outside a running Application nothing has told WPF which assembly backs
        // "pack://application:,,,". Setting ResourceAssembly is what Application does on startup and
        // is enough to make the ";component" resource URIs below resolve from a plain test host.
        System.Windows.Application.ResourceAssembly ??= typeof(NickColorConverter).Assembly;
    }

    /// <summary>The dictionaries in the exact order <c>App.xaml</c> merges them.</summary>
    private static readonly string[] AppDictionaries =
    [
        "Palette.xaml",
        "Typography.xaml",
        "Icons.xaml",
        "Controls/Icons.xaml",
        "Controls/Buttons.xaml",
        "Controls/Inputs.xaml",
        "Controls/Lists.xaml",
        "Controls/Surfaces.xaml",
        "Controls/Markdown.xaml",
        "Window.xaml",
    ];

    /// <summary>
    /// Keys whose values only resolve inside a running <see cref="System.Windows.Application" />.
    /// </summary>
    /// <remarks>
    /// A <c>StaticResource</c> nested in an object element inside a <c>Setter.Value</c> gets a
    /// narrower lookup scope than a plain setter value: it sees the declaring dictionary but not the
    /// parent that merged it, so it can only fall back to <c>Application.Current.Resources</c>. Both
    /// window styles build a <c>WindowChrome</c> that way, and <c>Window.Shell</c> reads
    /// <c>Size.CaptionHeight</c> from Palette.xaml. That works in the app because App.xaml merges
    /// everything into the application scope; constructing an <c>Application</c> here would leak into
    /// every other test in the assembly, so these two keys are only checked for presence.
    /// </remarks>
    private static readonly string[] NeedsApplicationScope = ["Window.Shell", "Window.Dialog"];

    private static ResourceDictionary Load(string relativePath)
    {
        var dictionary = new ResourceDictionary();
        dictionary.Source = new Uri(Prefix + relativePath, UriKind.Relative);
        return dictionary;
    }

    private static ResourceDictionary LoadApplicationResources()
    {
        // Merging has to go through XAML rather than MergedDictionaries.Add: a dictionary parses as
        // soon as its Source is set, and a StaticResource can only reach a sibling that was merged
        // earlier if the parent chain already exists at that moment. Window.xaml relies on it for
        // Size.CaptionHeight, which lives in Palette.xaml.
        var xaml = new System.Text.StringBuilder()
            .AppendLine("""<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">""")
            .AppendLine("  <ResourceDictionary.MergedDictionaries>");

        foreach (string path in AppDictionaries)
            xaml.AppendLine($"""    <ResourceDictionary Source="{Prefix}{path}" />""");

        xaml.AppendLine("  </ResourceDictionary.MergedDictionaries>")
            .AppendLine("</ResourceDictionary>");

        return (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml.ToString());
    }

    [Fact]
    public void ThePaletteStandsOnItsOwn()
    {
        // Palette is merged first, so it must not depend on anything the later dictionaries add.
        Sta.Run(() =>
        {
            var palette = Load("Palette.xaml");

            Assert.NotEmpty(palette.Keys);
            foreach (object key in palette.Keys)
                Assert.NotNull(palette[key]);
        });
    }

    [Fact]
    public void EveryColourKeyHoldsAColour()
    {
        Sta.Run(() =>
        {
            var palette = Load("Palette.xaml");

            foreach (string key in Keys(palette).Where(k => k.StartsWith("Color.", StringComparison.Ordinal)))
                Assert.IsType<Color>(palette[key]);
        });
    }

    [Fact]
    public void EveryBrushKeyHoldsABrush()
    {
        Sta.Run(() =>
        {
            var palette = Load("Palette.xaml");

            foreach (string key in Keys(palette).Where(k => k.StartsWith("Brush.", StringComparison.Ordinal)))
                Assert.IsAssignableFrom<Brush>(palette[key]);
        });
    }

    [Fact]
    public void EveryCornerRadiusKeyHoldsACornerRadius()
    {
        Sta.Run(() =>
        {
            var palette = Load("Palette.xaml");

            foreach (string key in Keys(palette).Where(k => k.StartsWith("Radius.", StringComparison.Ordinal)))
                Assert.IsType<CornerRadius>(palette[key]);
        });
    }

    [Fact]
    public void ASizeIsADoubleUnlessItNamesAColumn()
    {
        // Binding a double to ColumnDefinition.Width throws at load time, so the grid metrics are
        // published twice: Size.X as a double for Width/Height and Size.XColumn as a GridLength.
        Sta.Run(() =>
        {
            var palette = Load("Palette.xaml");

            foreach (string key in Keys(palette).Where(k => k.StartsWith("Size.", StringComparison.Ordinal)))
            {
                if (key.EndsWith("Column", StringComparison.Ordinal))
                    Assert.IsType<GridLength>(palette[key]);
                else
                    Assert.IsType<double>(palette[key]);
            }
        });
    }

    [Fact]
    public void TheAvatarColumnWidthIsAGridLength()
    {
        Sta.Run(() =>
        {
            var palette = Load("Palette.xaml");

            var width = Assert.IsType<GridLength>(palette["Size.AvatarSmallColumn"]);
            Assert.Equal(24d, width.Value);
            Assert.Equal(GridUnitType.Pixel, width.GridUnitType);
            Assert.Equal(24d, Assert.IsType<double>(palette["Size.AvatarSmall"]));
        });
    }

    [Theory]
    [InlineData("Size.CaptionHeight", 48d)]
    [InlineData("Size.IconSmall", 14d)]
    [InlineData("Size.IconMedium", 18d)]
    [InlineData("Size.IconLarge", 22d)]
    [InlineData("Size.RowHeight", 28d)]
    [InlineData("Size.AvatarSmall", 24d)]
    [InlineData("Size.AvatarMedium", 32d)]
    public void TheLayoutMetricsAreDoubles(string key, double expected)
    {
        Sta.Run(() => Assert.Equal(expected, Assert.IsType<double>(Load("Palette.xaml")[key])));
    }

    [Theory]
    [InlineData("Radius.Small", 4d)]
    [InlineData("Radius.Medium", 8d)]
    [InlineData("Radius.Large", 10d)]
    [InlineData("Radius.Pill", 999d)]
    public void TheCornerRadiiAreUniform(string key, double expected)
    {
        Sta.Run(() =>
        {
            var radius = Assert.IsType<CornerRadius>(Load("Palette.xaml")[key]);

            Assert.Equal(expected, radius.TopLeft);
            Assert.Equal(expected, radius.TopRight);
            Assert.Equal(expected, radius.BottomRight);
            Assert.Equal(expected, radius.BottomLeft);
        });
    }

    [Fact]
    public void TheNickPaletteMatchesWhatTheConverterAsksFor()
    {
        // NickColorConverter hashes into Brush.Nick0..Brush.Nick{PaletteSize - 1}; a mismatch would
        // silently fall back to grey for part of the roster.
        Sta.Run(() =>
        {
            var palette = Load("Palette.xaml");

            for (int i = 0; i < NickColorConverter.PaletteSize; i++)
                Assert.IsType<SolidColorBrush>(palette[$"Brush.Nick{i}"]);

            Assert.False(palette.Contains($"Brush.Nick{NickColorConverter.PaletteSize}"));
        });
    }

    [Fact]
    public void TheNickBrushesAreAllDistinct()
    {
        Sta.Run(() =>
        {
            var palette = Load("Palette.xaml");

            var colors = Enumerable.Range(0, NickColorConverter.PaletteSize)
                .Select(i => ((SolidColorBrush)palette[$"Brush.Nick{i}"]).Color)
                .ToList();

            Assert.Equal(NickColorConverter.PaletteSize, colors.Distinct().Count());
        });
    }

    [Fact]
    public void EveryResourceInTheMergedThemeCanBeRealised()
    {
        // Resource values are lazy: an unresolved StaticResource only throws when something asks
        // for it. Touching every key forces the whole theme to be built.
        Sta.Run(() =>
        {
            var resources = LoadApplicationResources();

            foreach (var dictionary in Flatten(resources))
            {
                foreach (object key in dictionary.Keys.Cast<object>().ToList())
                {
                    if (key is string name && NeedsApplicationScope.Contains(name, StringComparer.Ordinal))
                        continue;

                    Assert.NotNull(resources[key]);
                }
            }
        });
    }

    [Theory]
    [InlineData("Button.Primary")]
    [InlineData("TextBox.Base")]
    [InlineData("ListItem.Message")]
    [InlineData("Card")]
    [InlineData("Text.Body")]
    [InlineData("Text.SectionHeader")]
    [InlineData("Text.Caption")]
    [InlineData("Markdown.Message")]
    [InlineData("MenuItem.AudioDevice")]
    public void TheStylesTheShellBindsToExist(string key)
    {
        Sta.Run(() => Assert.IsAssignableFrom<Style>(LoadApplicationResources()[key]));
    }

    [Fact]
    public void TheAudioDeviceRowDrawsItsCheckMarkFromAHeaderTemplate()
    {
        // The shared MenuItem template has no check glyph, so the marker has to come from the
        // header template. A Setter.Value element would be shared across rows and throw on the
        // second one, which is why this asserts a DataTemplate rather than any visual.
        Sta.Run(() =>
        {
            var style = (Style)LoadApplicationResources()["MenuItem.AudioDevice"];

            Assert.Equal(typeof(System.Windows.Controls.MenuItem), style.TargetType);

            var header = style.Setters
                .OfType<Setter>()
                .Single(s => s.Property == System.Windows.Controls.HeaderedItemsControl.HeaderTemplateProperty);

            Assert.IsType<DataTemplate>(header.Value);
        });
    }

    [Fact]
    public void TheWindowStylesAreDeclared()
    {
        Sta.Run(() =>
        {
            var resources = LoadApplicationResources();

            foreach (string key in NeedsApplicationScope)
                Assert.True(resources.Contains(key));
        });
    }

    [Fact]
    public void TheUiFontIsAFontFamily()
    {
        Sta.Run(() => Assert.IsType<FontFamily>(LoadApplicationResources()["Font.Ui"]));
    }

    [Fact]
    public void TheIconPathsAreGeometries()
    {
        Sta.Run(() =>
        {
            var resources = LoadApplicationResources();

            Assert.IsAssignableFrom<Geometry>(resources["Icon.Close"]);
            Assert.IsAssignableFrom<Style>(resources["Icon.Small"]);
        });
    }

    [Fact]
    public void TheTwoIconDictionariesDoNotShadowEachOther()
    {
        // Themes/Icons.xaml holds the path geometries and Themes/Controls/Icons.xaml the Path
        // styles. They share a name but must not share keys, or whichever merges last wins.
        Sta.Run(() =>
        {
            var geometries = Keys(Load("Icons.xaml")).ToHashSet(StringComparer.Ordinal);

            foreach (string key in Keys(Load("Controls/Icons.xaml")))
                Assert.DoesNotContain(key, geometries);
        });
    }

    [Fact]
    public void EveryDictionaryAppXamlListsIsLoadable()
    {
        Sta.Run(() =>
        {
            foreach (string path in AppDictionaries)
                Assert.NotEmpty(Load(path).Keys);
        });
    }

    private static IEnumerable<string> Keys(ResourceDictionary dictionary) =>
        dictionary.Keys.Cast<object>().OfType<string>();

    private static IEnumerable<ResourceDictionary> Flatten(ResourceDictionary dictionary)
    {
        yield return dictionary;

        foreach (var merged in dictionary.MergedDictionaries)
        {
            foreach (var nested in Flatten(merged))
                yield return nested;
        }
    }
}
