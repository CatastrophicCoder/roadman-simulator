using System.Reflection;
using System.Text.RegularExpressions;
using Roadman.Core.Maps;
using Roadman.Core.Minimap;
using Roadman.Core.Tests.Items;
using Roadman.Core.Tools;

namespace Roadman.Core.Tests.Minimap;

/// <summary>
/// No other games' names or trademarks in the minimap's names and texts (<c>docs/specs/minimap.md</c>, acceptance
/// criterion 20): the word list of ADR 0019 (<see cref="ItemCatalogTests.BrandOrGamePattern"/>) against every type,
/// member and enum value name of <c>Roadman.Core.Minimap</c>, its string constants, the style tables' keys, and the
/// minimap's reader and argument names.
/// </summary>
public partial class MinimapWordListTests
{
    [Fact]
    public void Ac20_MinimapNamesAndTexts_HaveNoOtherGamesNamesOrTrademarks()
    {
        var texts = MinimapTexts().ToList();
        Assert.NotEmpty(texts);

        var hits = texts
            .SelectMany(text => new[] { text, SplitWords(text) })
            .Select(text => (Text: text, Match: ItemCatalogTests.BrandOrGamePattern.Match(text)))
            .Where(hit => hit.Match.Success)
            .Select(hit => $"'{hit.Match.Value}' in '{hit.Text}'")
            .Distinct()
            .ToList();

        Assert.True(hits.Count == 0, $"other games' names or trademarks: {string.Join("; ", hits)}");
    }

    [Fact]
    public void Ac20_TheCheckSeesTheMinimapTypes()
    {
        var texts = MinimapTexts().ToHashSet(StringComparer.Ordinal);

        Assert.Contains(nameof(MinimapProjection), texts);
        Assert.Contains(nameof(PoiGroup.FoodAndDrink), texts);
        Assert.Contains("parking_aisle", texts);
        Assert.Contains(MinimapStyle.ParkingSymbolText, texts);
        Assert.Contains(ScreenshotArgs.MinimapTopRightValue, texts);

        // And it would catch a planted name.
        Assert.Matches(ItemCatalogTests.BrandOrGamePattern, SplitWords("MinimapGTAStyle"));
    }

    private static IEnumerable<string> MinimapTexts()
    {
        var types = typeof(MinimapProjection).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(MinimapProjection).Namespace)
            .Concat([typeof(MinimapImage), typeof(MinimapImageReader), typeof(MinimapBounds)]);
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (var type in types)
        {
            if (type.Name.Contains('<'))
            {
                continue; // compiler-generated
            }

            yield return type.Name;
            foreach (var member in type.GetMembers(all).Where(member => !member.Name.Contains('<')))
            {
                yield return member.Name;
            }

            foreach (var field in type.GetFields(all).Where(field => field.IsLiteral && field.FieldType == typeof(string)))
            {
                yield return (string)field.GetRawConstantValue()!;
            }

            if (type.IsEnum)
            {
                foreach (var name in Enum.GetNames(type))
                {
                    yield return name;
                }
            }
        }

        foreach (var key in MinimapStyle.KnownRoadClasses.Keys.Concat(MinimapStyle.KnownAmenities.Keys))
        {
            yield return key;
        }

        yield return MapPack.MinimapFile;
        yield return MapPack.MinimapImageFile;
        foreach (var value in new[]
        {
            ScreenshotArgs.MinimapCornerFlag, ScreenshotArgs.MinimapTopLeftValue, ScreenshotArgs.MinimapTopRightValue,
            ScreenshotArgs.MinimapBottomLeftValue, ScreenshotArgs.MinimapBottomRightValue, PerfArgs.NoMinimapFlag,
        })
        {
            yield return value;
        }
    }

    /// <summary>A PascalCase or snake_case name as separate words, so a name hidden inside an identifier is a whole word.</summary>
    private static string SplitWords(string text) => WordBreak().Replace(text, " ").Replace('_', ' ').Replace('-', ' ');

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex WordBreak();
}
