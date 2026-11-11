using EtlApp.Etl;
using Xunit;

namespace EtlApp.Tests;

public class FilteringAndSortingTests
{
    static TransformResult Data() => new(
        new List<string> { "Id", "Visit", "Score", "Name" },
        new List<Dictionary<string, object?>>
        {
            new() { ["Id"] = "1", ["Visit"] = new DateTime(2024, 1, 10), ["Score"] = 5d,  ["Name"] = "Asha" },
            new() { ["Id"] = "2", ["Visit"] = new DateTime(2024, 3, 2),  ["Score"] = 12d, ["Name"] = "Ravi" },
            new() { ["Id"] = "3", ["Visit"] = null,                      ["Score"] = 7d,  ["Name"] = "meera" },
        },
        new Dictionary<string, string>(), new List<string>());

    [Fact]
    public void Date_columns_are_described_as_dates()
    {
        var info = Filtering.Describe(Data()).Single(c => c.Name == "Visit");
        Assert.Equal("date", info.Type);
        Assert.Equal("2024-01-10", info.MinText);
        Assert.Equal("2024-03-02", info.MaxText);
    }

    [Theory]
    [InlineData("gt", "2024-01-10", new[] { "2" })]
    [InlineData("gte", "2024-01-10", new[] { "1", "2" })]
    [InlineData("lt", "2024-03-02", new[] { "1" })]
    [InlineData("eq", "2024-03-02", new[] { "2" })]
    [InlineData("notEquals", "2024-03-02", new[] { "1" })]   // blank dates never match
    public void Date_filters(string op, string value, string[] ids)
    {
        var rows = Filtering.Apply(Data(), new[] { new FilterSpec("Visit", op, value) });
        Assert.Equal(ids, rows.Select(r => (string)r["Id"]!).ToArray());
    }

    [Fact]
    public void Bad_date_value_is_rejected() =>
        Assert.Throws<ArgumentException>(() => Filtering.Apply(Data(), new[] { new FilterSpec("Visit", "gt", "10/01/2024") }));

    [Fact]
    public void Text_operator_on_date_column_is_rejected() =>
        Assert.Throws<ArgumentException>(() => Filtering.Apply(Data(), new[] { new FilterSpec("Visit", "contains", "2024") }));

    [Fact]
    public void Number_filters_still_work()
    {
        var rows = Filtering.Apply(Data(), new[] { new FilterSpec("Score", "gte", "7") });
        Assert.Equal(new[] { "2", "3" }, rows.Select(r => (string)r["Id"]!).ToArray());
    }

    [Fact]
    public void Nested_groups_combine()
    {
        var filters = new[]
        {
            new FilterSpec(null, null, null, "or", new List<FilterSpec>
            {
                new("Name", "equals", "asha"),
                new("Score", "gt", "10"),
            })
        };
        var rows = Filtering.Apply(Data(), filters);
        Assert.Equal(new[] { "1", "2" }, rows.Select(r => (string)r["Id"]!).ToArray());
    }

    [Fact]
    public void Number_like_text_sorts_numerically()
    {
        var values = new object?[] { "10", "9", null, "100", "2" };
        var sorted = values.OrderBy(v => v, new CellComparer(false)).ToArray();
        Assert.Equal(new object?[] { "2", "9", "10", "100", null }, sorted);
    }

    [Fact]
    public void Blanks_go_last_in_both_directions()
    {
        var values = new object?[] { "b", "", "a", null };
        var desc = values.OrderBy(v => v, new CellComparer(true)).ToArray();
        Assert.Equal("b", desc[0]);
        Assert.Equal("a", desc[1]);
        Assert.True(desc.Skip(2).All(v => v == null || (string)v == ""));
    }

    [Fact]
    public void Mixed_numbers_and_text_numbers_sort_together()
    {
        var values = new object?[] { 10d, "9", 1d };
        Assert.Equal(new object?[] { 1d, "9", 10d }, values.OrderBy(v => v, new CellComparer(false)).ToArray());
    }
}

public class SessionStoreTests
{
    static (List<string>, List<Dictionary<string, object?>>, TransformResult) Upload(int rows, int cols)
    {
        var headers = Enumerable.Range(0, cols).Select(i => "c" + i).ToList();
        var raw = Enumerable.Range(0, rows).Select(_ => headers.ToDictionary(h => h, h => (object?)"x")).ToList();
        return (headers, raw, Transformer.Run(headers, raw));
    }

    [Fact]
    public void Oldest_uploads_are_dropped_when_over_budget()
    {
        var store = new SessionStore();
        var (h, raw, data) = Upload(100_000, 6);                 // 600k cells each; budget is 1.5M in total
        var a = store.Add("a.xlsx", "s", h, raw, data);
        var b = store.Add("b.xlsx", "s", h, raw, data);
        var c = store.Add("c.xlsx", "s", h, raw, data);
        Assert.Null(store.Get(a));
        Assert.NotNull(store.Get(b));
        Assert.NotNull(store.Get(c));
    }

    [Fact]
    public void Cleanup_bumps_the_version()
    {
        var store = new SessionStore();
        var (h, raw, data) = Upload(2, 2);
        var id = store.Add("a.xlsx", "s", h, raw, data);
        var e = store.GetEntry(id)!;
        var v = e.Version;
        e.SetCleanup(new[] { CleanupRules.CasteNt }, data);
        Assert.Equal(v + 1, e.Version);
        Assert.Equal(new[] { CleanupRules.CasteNt }, e.Cleanup);
    }
}
