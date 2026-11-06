using System.Text.RegularExpressions;
using Roadman.Core.Items;
using Roadman.Core.Maps;
using static Roadman.Core.Tests.Items.TestCatalogs;

namespace Roadman.Core.Tests.Items;

/// <summary>
/// <see cref="ItemCatalog"/>, <see cref="ItemType"/>, <see cref="ItemCategory"/> and <see cref="EquipSlot"/>:
/// <c>docs/specs/inventory.md</c>, acceptance criteria 1 and 2.
/// </summary>
public class ItemCatalogTests
{
    /// <summary>
    /// This list exists solely to check that these names don't appear in game content: it is the one place where
    /// other games' names and trademarks may be written (the exception recorded in the root <c>CLAUDE.md</c> and
    /// ADR 0019). It holds distinctive brand names (sportswear, fashion, phones, bike locks, tools, sports gear) and
    /// other games' titles, studios, places and characters only. A name that is also an ordinary word or phrase
    /// (a common noun, an adjective, an idiom, a plain first name or place name, in English or Finnish) is left out,
    /// so ordinary descriptions never trip it. Matched as whole words or whole phrases, case-insensitive, never as
    /// substrings; an id's '_' and '-' count as word breaks.
    /// </summary>
    private static readonly string[] BrandAndGameWords =
    [
        // Sportswear and fashion
        "Adidas", "Nike", "Reebok", "Fila", "Umbro", "Lacoste", "Ellesse", "Sergio Tacchini", "Le Coq Sportif",
        "Lonsdale", "Everlast", "Asics", "Moncler", "Burberry", "Gucci", "Louis Vuitton", "Prada", "Versace",
        "Balenciaga", "Stussy", "Carhartt", "Levi's", "Levis", "Hugo Boss", "Ralph Lauren", "Tommy Hilfiger",
        "Calvin Klein", "Fred Perry", "Ben Sherman", "Kangol", "Trapstar", "Corteiz", "Marimekko", "Halti", "Icepeak",
        "Reima",
        // Phones and electronics
        "iPhone", "Samsung", "Nokia", "Motorola", "Huawei", "Xiaomi", "OnePlus", "Google", "Sony", "Ericsson", "Doro",
        // Bats, sports gear
        "Louisville Slugger", "Rawlings", "Mizuno", "Marucci",
        // Locks, bikes and tools
        "Abus", "Masterlock", "OnGuard", "Hiplok", "Bahco", "Fiskars", "Estwing", "Halder", "Hultafors", "Makita",
        "Bosch", "DeWalt", "Jopo", "Helkama", "Brompton", "Cannondale",
        // Lighters, bags, other gear
        "Zippo", "Bic", "Eastpak", "Fjallraven", "Fjällräven", "Kanken", "Rolex", "Casio", "G-Shock",
        // Other games, studios, their places and characters
        "Grand Theft Auto", "GTA", "Vice City", "San Andreas", "Liberty City", "Los Santos", "Saints Row", "Watch Dogs",
        "Crazy Taxi", "Red Dead", "Scarface", "Gordon Freeman", "Black Mesa", "Fortnite", "Minecraft", "Skyrim",
        "Resident Evil", "Silent Hill", "Max Payne", "Alan Wake", "Rovio", "Zelda", "Tetris", "Pokemon", "Pokémon",
    ];

    private static readonly Regex BrandOrGamePattern = new(
        @"(?<![\p{L}\p{N}])(" + string.Join("|", BrandAndGameWords.Select(Regex.Escape)) + @")(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static ItemType[] ValidList() =>
    [
        Item("tool_2", ItemCategory.Weapon, "Zulu tool"),
        Item("misc", ItemCategory.Other, "apple crate"),
        Item("tool_1", ItemCategory.Weapon, "alpha tool"),
        Item("coat", ItemCategory.Clothing, "Coat"),
        Item("badge", ItemCategory.Other, "Badge"),
        Item("tool_3", ItemCategory.Weapon, "Beta tool"),
    ];

    // Criterion 1: construction

    [Fact]
    public void Ac1_ValidList_IsAcceptedWithEveryItem()
    {
        var catalog = new ItemCatalog(ValidList());

        Assert.Equal(6, catalog.All.Count);
        Assert.All(ValidList(), item => Assert.Equal(item, catalog.Get(item.Id)));
    }

    [Fact]
    public void Ac1_EmptyList_IsAccepted()
    {
        Assert.Empty(new ItemCatalog([]).All);
    }

    public static TheoryData<string> NonSlugIds() =>
    [
        "Crowbar", "crow bar", "crow--bar", "crow__bar", "_crowbar", "crowbar_", "-crowbar", "crow.bar", "crowbär",
        "crowbar\n", " crowbar", "CROWBAR",
    ];

    [Theory]
    [MemberData(nameof(NonSlugIds))]
    public void Ac1_NonSlugId_IsRejectedNamingTheId(string id)
    {
        var ex = Assert.ThrowsAny<ArgumentException>(() => new ItemCatalog([Item("ok"), Item(id)]));

        Assert.IsNotType<ArgumentNullException>(ex);
        Assert.Contains(id, ex.Message);
    }

    [Fact]
    public void Ac1_EmptyId_IsRejected()
    {
        var ex = Assert.ThrowsAny<ArgumentException>(() => new ItemCatalog([Item("ok"), Item("")]));

        Assert.IsNotType<ArgumentNullException>(ex);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("a1")]
    [InlineData("crow_bar")]
    [InlineData("crow-bar")]
    [InlineData("item_2-b")]
    public void Ac1_SlugIds_AreAccepted(string id)
    {
        Assert.True(MapPack.IsSlug(id));
        Assert.True(new ItemCatalog([Item(id)]).Contains(id));
    }

    [Fact]
    public void Ac1_RepeatedId_IsRejectedNamingTheId()
    {
        var ex = Assert.ThrowsAny<ArgumentException>(() =>
            new ItemCatalog([Item("crowbar", name: "Crowbar"), Item("phone"), Item("crowbar", name: "Other crowbar")]));

        Assert.Contains("crowbar", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData(null)]
    public void Ac1_BlankDisplayName_IsRejectedNamingTheId(string? name)
    {
        var ex = Assert.ThrowsAny<ArgumentException>(() =>
            new ItemCatalog([Item("ok"), new ItemType("nameless", name!, ItemCategory.Other, "A description.")]));

        Assert.Contains("nameless", ex.Message);
    }

    [Theory]
    [InlineData("Crowbar", "Crowbar")]
    [InlineData("Crowbar", "crowbar")]
    [InlineData("Crowbar", "CROWBAR")]
    public void Ac1_DisplayNameUsedTwice_IgnoringCase_IsRejectedNamingTheSecondId(string first, string second)
    {
        var ex = Assert.ThrowsAny<ArgumentException>(() =>
            new ItemCatalog([Item("first_bar", name: first), Item("second_bar", name: second)]));

        Assert.Contains("second_bar", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void Ac1_BlankDescription_IsRejectedNamingTheId(string? description)
    {
        var ex = Assert.ThrowsAny<ArgumentException>(() =>
            new ItemCatalog([Item("ok"), new ItemType("plain", "Plain", ItemCategory.Other, description!)]));

        Assert.Contains("plain", ex.Message);
    }

    [Theory]
    [InlineData("Two\nlines.")]
    [InlineData("Two\rlines.")]
    [InlineData("Two\r\nlines.")]
    [InlineData("Trailing break.\n")]
    public void Ac1_DescriptionWithALineBreak_IsRejectedNamingTheId(string description)
    {
        var ex = Assert.ThrowsAny<ArgumentException>(() =>
            new ItemCatalog([Item("ok"), Item("wordy", description: description)]));

        Assert.Contains("wordy", ex.Message);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    [InlineData(99)]
    public void Ac1_UndefinedCategory_IsRejectedNamingTheId(int category)
    {
        var ex = Assert.ThrowsAny<ArgumentException>(() =>
            new ItemCatalog([Item("ok"), Item("odd", (ItemCategory)category)]));

        Assert.Contains("odd", ex.Message);
    }

    [Fact]
    public void Constructor_NullListOrNullItem_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => new ItemCatalog(null!));
        Assert.Throws<ArgumentNullException>(() => new ItemCatalog([Item("ok"), null!]));
    }

    // Criterion 1: lookups

    [Fact]
    public void Ac1_TryGet_KnownIdGivesTheItem_UnknownGivesFalse()
    {
        var catalog = new ItemCatalog(ValidList());

        Assert.True(catalog.TryGet("coat", out var coat));
        Assert.Equal(Item("coat", ItemCategory.Clothing, "Coat"), coat);
        Assert.False(catalog.TryGet("hat", out var hat));
        Assert.Null(hat);
        Assert.False(catalog.TryGet("COAT", out _)); // ids are compared ordinal
    }

    [Fact]
    public void Ac1_Get_UnknownId_ThrowsKeyNotFoundNamingIt()
    {
        var catalog = new ItemCatalog(ValidList());

        var ex = Assert.Throws<KeyNotFoundException>(() => catalog.Get("laser_saw"));

        Assert.Contains("laser_saw", ex.Message);
    }

    [Fact]
    public void Ac1_Contains_IsTrueForKnownIdsOnly()
    {
        var catalog = new ItemCatalog(ValidList());

        Assert.True(catalog.Contains("tool_1"));
        Assert.True(catalog.Contains("badge"));
        Assert.False(catalog.Contains("tool_4"));
        Assert.False(catalog.Contains("Tool_1"));
        Assert.False(catalog.Contains(""));
    }

    [Fact]
    public void Ac1_All_IsSortedByCategoryThenDisplayNameIgnoringCase()
    {
        var catalog = new ItemCatalog(ValidList());

        // Weapons: "alpha tool" < "Beta tool" < "Zulu tool" ignoring case (ordinal would put "Beta", "Zulu" first);
        // then clothing; then other: "apple crate" < "Badge".
        Assert.Equal(["tool_1", "tool_3", "tool_2", "coat", "misc", "badge"], catalog.All.Select(i => i.Id));
    }

    [Fact]
    public void Ac1_All_DoesNotDependOnTheInputOrder()
    {
        var forward = new ItemCatalog(ValidList()).All.Select(i => i.Id).ToList();
        var backward = new ItemCatalog(ValidList().Reverse()).All.Select(i => i.Id).ToList();

        Assert.Equal(forward, backward);
    }

    [Fact]
    public void Ac1_SlotFor_WeaponIsWeapon_ClothingIsOutfit_OtherIsNone()
    {
        Assert.Equal(EquipSlot.Weapon, ItemCatalog.SlotFor(Item("w", ItemCategory.Weapon)));
        Assert.Equal(EquipSlot.Outfit, ItemCatalog.SlotFor(Item("c", ItemCategory.Clothing)));
        Assert.Null(ItemCatalog.SlotFor(Item("o", ItemCategory.Other)));
    }

    [Fact]
    public void Ac1_SlotForId_LooksTheItemUp()
    {
        var catalog = Mixed;

        Assert.Equal(EquipSlot.Weapon, catalog.SlotFor("bat"));
        Assert.Equal(EquipSlot.Outfit, catalog.SlotFor("hoodie"));
        Assert.Null(catalog.SlotFor("phone"));
        Assert.Throws<KeyNotFoundException>(() => catalog.SlotFor("sword"));
    }

    [Fact]
    public void Slots_AreWeaponThenOutfit_TheOnlyTwo()
    {
        Assert.Equal([EquipSlot.Weapon, EquipSlot.Outfit], ItemCatalog.Slots);
        Assert.Equal([EquipSlot.Weapon, EquipSlot.Outfit], Enum.GetValues<EquipSlot>());
    }

    [Fact]
    public void Categories_AreWeaponClothingOther_InThatOrder()
    {
        Assert.Equal([ItemCategory.Weapon, ItemCategory.Clothing, ItemCategory.Other], Enum.GetValues<ItemCategory>());
    }

    // Criterion 2: the default catalogue

    public static TheoryData<string, string, ItemCategory, string> DefaultTable() => new()
    {
        { "baseball_bat", "Baseball bat", ItemCategory.Weapon, "A wooden bat. Not for baseball." },
        { "crowbar", "Crowbar", ItemCategory.Weapon, "Opens things that were meant to stay shut." },
        { "tracksuit", "Tracksuit", ItemCategory.Clothing, "Matching top and bottoms, slightly too shiny." },
        { "hoodie", "Hoodie", ItemCategory.Clothing, "Hood up, head down." },
        { "phone", "Phone", ItemCategory.Other, "Cracked screen, one bar of signal." },
        { "bike_lock", "Bike lock", ItemCategory.Other, "A heavy chain lock. The bike is long gone." },
    };

    [Theory]
    [MemberData(nameof(DefaultTable))]
    public void Ac2_Default_HoldsTheSpecTableItem(string id, string name, ItemCategory category, string description)
    {
        Assert.Equal(new ItemType(id, name, category, description), ItemCatalog.Default.Get(id));
    }

    [Fact]
    public void Ac2_Default_HoldsExactlyTheSixItems_InCatalogueOrder()
    {
        Assert.Equal(
            ["baseball_bat", "crowbar", "hoodie", "tracksuit", "bike_lock", "phone"],
            ItemCatalog.Default.All.Select(i => i.Id));
    }

    [Fact]
    public void Ac2_Default_PassesTheConstructionChecks()
    {
        var rebuilt = new ItemCatalog(ItemCatalog.Default.All);

        Assert.Equal(ItemCatalog.Default.All, rebuilt.All);
        Assert.All(ItemCatalog.Default.All, item =>
        {
            Assert.True(MapPack.IsSlug(item.Id), item.Id);
            Assert.False(string.IsNullOrWhiteSpace(item.DisplayName), item.Id);
            Assert.False(string.IsNullOrWhiteSpace(item.Description), item.Id);
            Assert.DoesNotContain('\n', item.Description);
            Assert.DoesNotContain('\r', item.Description);
            Assert.True(Enum.IsDefined(item.Category), item.Id);
        });
    }

    [Fact]
    public void Ac2_Default_IsTheSameInstanceEachTime()
    {
        Assert.Same(ItemCatalog.Default, ItemCatalog.Default);
    }

    [Fact]
    public void Ac2_Default_NamesNoBrandOrOtherGame()
    {
        var offending = ItemCatalog.Default.All
            .SelectMany(item => new[] { item.Id.Replace('_', ' '), item.DisplayName, item.Description }
                .Select(text => (item.Id, Text: text, Match: BrandOrGamePattern.Match(text))))
            .Where(x => x.Match.Success)
            .Select(x => $"{x.Id}: '{x.Text}' ({x.Match.Value})")
            .ToList();

        Assert.True(offending.Count == 0, $"brand or game words in the default catalogue: {string.Join("; ", offending)}");
    }

    [Theory]
    [InlineData("Adidas tracksuit")]
    [InlineData("a black mesa crowbar")]
    [InlineData("GTA")]
    [InlineData("abus lock")]
    [InlineData("louisville slugger")]
    [InlineData("iphone")]
    [InlineData("ZIPPO")]
    [InlineData("le coq sportif hoodie")]
    [InlineData("gta_crowbar")] // an id: '_' is a word break
    [InlineData("g-shock")]
    public void BrandWordList_CatchesBrandsAndGames(string text)
    {
        Assert.Matches(BrandOrGamePattern, text);
    }

    [Theory]
    [InlineData("Baseball bat")]
    [InlineData("A heavy chain lock. The bike is long gone.")]
    [InlineData("bike lock")]
    [InlineData("Matching top and bottoms, slightly too shiny.")]
    // Ordinary words that are also brand or game names are not on the list.
    [InlineData("The driver, a champion of the palace, ate an apple.")]
    [InlineData("A giant galaxy; a clash on the trek; a crescent moon.")]
    [InlineData("Mafia, postal, remedy, supreme, diesel, specialized, valve, bully, clipper.")]
    [InlineData("Half-life of a fallout; a far cry; a just cause; the call of duty; a new balance.")]
    // Whole words only: a name inside a longer word doesn't count.
    [InlineData("abuse")] // Abus
    [InlineData("bicycle")] // Bic
    [InlineData("Dorothy")] // Doro
    [InlineData("Nikes")] // Nike
    [InlineData("sonyx")] // Sony
    [InlineData("gtaway")] // GTA
    public void BrandWordList_LetsGenericTextThrough(string text)
    {
        Assert.DoesNotMatch(BrandOrGamePattern, text);
    }

    [Fact]
    public void BrandWordList_HasNoRepeatedEntries_IgnoringCase()
    {
        var repeated = BrandAndGameWords
            .GroupBy(word => word, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        Assert.Empty(repeated);
    }

    [Fact]
    public void BrandWordList_EveryEntryMatchesItself_AsAWholeWord()
    {
        Assert.All(BrandAndGameWords, word =>
        {
            Assert.Matches(BrandOrGamePattern, word);
            Assert.Matches(BrandOrGamePattern, $"a {word.ToLowerInvariant()} item");
            Assert.DoesNotMatch(BrandOrGamePattern, $"x{word}x");
        });
    }
}
