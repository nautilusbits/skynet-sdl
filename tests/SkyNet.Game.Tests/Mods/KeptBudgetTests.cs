using SkyNet.Game.Mods;

namespace SkyNet.Game.Tests.Mods;

/// <summary>Surfaces kept across maps: the current map's always, the rest least recently used first within the budget.</summary>
public class KeptBudgetTests
{
    [Fact]
    public void The_current_map_keeps_everything_it_uses_whatever_the_budget()
    {
        var budget = new KeptBudget<string>(10);
        budget.Use("a", 100);
        budget.Use("b", 100);

        Assert.Empty(budget.Over());
        Assert.Equal(200, budget.Bytes);
    }

    [Fact]
    public void Unused_items_go_oldest_first_until_the_rest_fits()
    {
        var budget = new KeptBudget<string>(150);
        budget.Use("oldest", 100);
        budget.NextMap();
        budget.Use("old", 100);
        budget.NextMap();
        budget.Use("newer", 100);
        budget.NextMap();
        budget.Use("now", 500);

        Assert.Equal(["oldest"], budget.Over());
        Assert.False(budget.Contains("oldest"));
        Assert.True(budget.Contains("old"));
        Assert.Equal(700, budget.Bytes);
    }

    [Fact]
    public void The_map_just_left_keeps_everything_whatever_the_budget()
    {
        // Out of a big map into an interior and back: the big map's surfaces wait for the return.
        var budget = new KeptBudget<string>(0);
        budget.Use("outdoor", 1000);
        budget.NextMap();
        budget.Use("interior", 10);

        Assert.Empty(budget.Over());
        budget.NextMap();
        budget.Use("outdoor", 1000);
        Assert.Empty(budget.Over());
        budget.NextMap();
        budget.Use("outdoor", 1000);
        Assert.Equal(["interior"], budget.Over());
    }

    [Fact]
    public void A_revisited_item_counts_once_and_is_current_again()
    {
        var budget = new KeptBudget<string>(0);
        budget.Use("a", 100);
        budget.NextMap();
        budget.Use("a", 100);

        Assert.Empty(budget.Over());
        Assert.Equal(100, budget.Bytes);
        Assert.Equal(1, budget.Count);
    }
}
