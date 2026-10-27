using Roadman.Core;

namespace Roadman.Core.Tests;

public class PlayerStateTests
{
    [Fact]
    public void Die_ClearsMoneyAndGear()
    {
        var state = new PlayerState();
        state.AddMoney(250);
        state.AddGear("pistol");
        state.AddGear("crowbar");

        state.Die();

        Assert.Equal(0, state.Money);
        Assert.Empty(state.Gear);
    }

    [Fact]
    public void Die_ResetsWantedLevel()
    {
        var state = new PlayerState();
        state.SetWantedLevel(3);

        state.Die();

        Assert.Equal(0, state.WantedLevel);
    }

    [Fact]
    public void Die_AlwaysRaisesDied()
    {
        var state = new PlayerState();
        var died = 0;
        state.Died += () => died++;

        state.Die();
        state.Die();

        Assert.Equal(2, died);
    }

    [Fact]
    public void AddMoney_IncreasesMoney()
    {
        var state = new PlayerState();

        state.AddMoney(40);
        state.AddMoney(2);

        Assert.Equal(42, state.Money);
    }

    [Fact]
    public void TrySpend_WithEnoughMoney_DeductsAndReturnsTrue()
    {
        var state = new PlayerState();
        state.AddMoney(100);

        Assert.True(state.TrySpend(100));
        Assert.Equal(0, state.Money);
    }

    [Fact]
    public void TrySpend_WithoutFunds_ReturnsFalseAndChangesNothing()
    {
        var state = new PlayerState();
        state.AddMoney(10);
        var moneyEvents = 0;
        state.MoneyChanged += _ => moneyEvents++;

        Assert.False(state.TrySpend(11));
        Assert.Equal(10, state.Money);
        Assert.Equal(0, moneyEvents);
    }

    [Fact]
    public void NegativeAmounts_AreRejected()
    {
        var state = new PlayerState();
        state.AddMoney(10);

        Assert.Throws<ArgumentOutOfRangeException>(() => state.AddMoney(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.TrySpend(-1));
        Assert.Equal(10, state.Money);
    }

    [Fact]
    public void AddMoney_Overflow_ThrowsAndLeavesMoneyUnchanged()
    {
        var state = new PlayerState();
        state.AddMoney(int.MaxValue);
        var moneyEvents = 0;
        state.MoneyChanged += _ => moneyEvents++;

        Assert.Throws<OverflowException>(() => state.AddMoney(1));
        Assert.Equal(int.MaxValue, state.Money);
        Assert.Equal(0, moneyEvents);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void GearIds_MustNotBeBlank(string? itemId)
    {
        var state = new PlayerState();

        Assert.ThrowsAny<ArgumentException>(() => state.AddGear(itemId!));
        Assert.ThrowsAny<ArgumentException>(() => state.RemoveGear(itemId!));
    }

    [Theory]
    [InlineData(-3, 0)]
    [InlineData(0, 0)]
    [InlineData(2, 2)]
    [InlineData(PlayerState.MaxWantedLevel, PlayerState.MaxWantedLevel)]
    [InlineData(99, PlayerState.MaxWantedLevel)]
    public void SetWantedLevel_ClampsToRange(int requested, int expected)
    {
        var state = new PlayerState();

        state.SetWantedLevel(requested);

        Assert.Equal(expected, state.WantedLevel);
    }

    [Fact]
    public void AddGear_And_RemoveGear_ReportWhetherTheSetChanged()
    {
        var state = new PlayerState();

        Assert.True(state.AddGear("pistol"));
        Assert.False(state.AddGear("pistol"));
        Assert.Contains("pistol", state.Gear);
        Assert.True(state.RemoveGear("pistol"));
        Assert.False(state.RemoveGear("pistol"));
        Assert.Empty(state.Gear);
    }

    [Fact]
    public void Events_FireOncePerChange()
    {
        var state = new PlayerState();
        var money = new List<int>();
        var gear = 0;
        var wanted = new List<int>();
        state.MoneyChanged += money.Add;
        state.GearChanged += () => gear++;
        state.WantedLevelChanged += wanted.Add;

        state.AddMoney(50);
        state.TrySpend(20);
        state.AddGear("crowbar");
        state.RemoveGear("crowbar");
        state.SetWantedLevel(2);

        Assert.Equal([50, 30], money);
        Assert.Equal(2, gear);
        Assert.Equal([2], wanted);
    }

    [Fact]
    public void Events_DoNotFireOnNoOp()
    {
        var state = new PlayerState();
        state.AddMoney(5);
        state.AddGear("crowbar");
        state.SetWantedLevel(PlayerState.MaxWantedLevel);
        var events = 0;
        state.MoneyChanged += _ => events++;
        state.GearChanged += () => events++;
        state.WantedLevelChanged += _ => events++;

        state.AddMoney(0);
        state.TrySpend(0);
        state.TrySpend(6);
        state.AddGear("crowbar");
        state.RemoveGear("pistol");
        state.SetWantedLevel(PlayerState.MaxWantedLevel);
        state.SetWantedLevel(PlayerState.MaxWantedLevel + 1);

        Assert.Equal(0, events);
    }

    [Fact]
    public void Die_WhenAlreadyEmpty_RaisesOnlyDied()
    {
        var state = new PlayerState();
        var changes = 0;
        var died = 0;
        state.MoneyChanged += _ => changes++;
        state.GearChanged += () => changes++;
        state.WantedLevelChanged += _ => changes++;
        state.Died += () => died++;

        state.Die();

        Assert.Equal(0, changes);
        Assert.Equal(1, died);
    }

    [Fact]
    public void Die_RaisesEachChangeOnce()
    {
        var state = new PlayerState();
        state.AddMoney(10);
        state.AddGear("pistol");
        state.AddGear("crowbar");
        state.SetWantedLevel(4);
        var money = new List<int>();
        var gear = 0;
        var wanted = new List<int>();
        state.MoneyChanged += money.Add;
        state.GearChanged += () => gear++;
        state.WantedLevelChanged += wanted.Add;

        state.Die();

        Assert.Equal([0], money);
        Assert.Equal(1, gear);
        Assert.Equal([0], wanted);
    }
}
