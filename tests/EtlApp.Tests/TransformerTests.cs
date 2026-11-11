using EtlApp.Etl;
using Xunit;

namespace EtlApp.Tests;

public class TransformerTests
{
    static readonly List<string> Headers = new()
    {
        "_uuid", "today", "Date Of Birth", "Caste", "Enter Other Caste", "Ethnicity", "Enter Other Ethnicity",
        "Other disease or symptoms of any?"
    };

    static Dictionary<string, object?> Row(string id, object? today = null, object? dob = null, string? caste = null, string? otherCaste = null,
                                           string? eth = null, string? otherEth = null, string? disease = null) => new()
    {
        ["_uuid"] = id, ["today"] = today, ["Date Of Birth"] = dob, ["Caste"] = caste, ["Enter Other Caste"] = otherCaste,
        ["Ethnicity"] = eth, ["Enter Other Ethnicity"] = otherEth, ["Other disease or symptoms of any?"] = disease
    };

    static readonly string[] AllRules = CleanupRules.All.Select(r => r.Key).ToArray();

    // ------------------------------------------------------------------ clean-up rules are opt-in

    [Fact]
    public void Default_run_keeps_every_value_as_uploaded()
    {
        var rows = new[] { Row("a", caste: "OBC", otherCaste: "NT", eth: "Other", otherEth: "Jainism", disease: "high bp") };
        var r = Transformer.Run(Headers, rows);

        var row = r.Rows[0];
        Assert.Equal("OBC", row["Caste"]);
        Assert.Equal("NT", row["Other Caste"]);
        Assert.Equal("Other", row["Ethnicity"]);
        Assert.Equal("Jainism", row["Other Ethnicity"]);
        Assert.Equal("high bp", row["Other disease or symptoms of any?"]);
        Assert.Empty(r.Changes!);
        Assert.Contains("Other Caste", r.Columns);
        Assert.Contains("Other Ethnicity", r.Columns);
    }

    [Fact]
    public void Default_run_keeps_empty_other_columns()
    {
        var r = Transformer.Run(Headers, new[] { Row("a", caste: "OBC") });
        Assert.Contains("Other Caste", r.Columns);
        Assert.Contains("Other Ethnicity", r.Columns);
    }

    [Fact]
    public void Default_run_still_renames_and_adds_calculated_columns()
    {
        var r = Transformer.Run(Headers, new[] { Row("a") });
        Assert.Equal("Id", r.Columns[0]);
        Assert.Contains("Date", r.Columns);
        Assert.Contains("Age", r.Columns);
    }

    [Fact]
    public void All_rules_apply_when_switched_on()
    {
        var rows = new[] { Row("a", caste: "OBC", otherCaste: "NT", eth: "Other", otherEth: "Jainism", disease: "hypertension") };
        var r = Transformer.Run(Headers, rows, AllRules);

        var row = r.Rows[0];
        Assert.Equal("NT", row["Caste"]);
        Assert.Equal("Jain", row["Ethnicity"]);
        Assert.Equal("Blood Pressure", row["Other disease or symptoms of any?"]);
        Assert.DoesNotContain("Other Caste", r.Columns);        // empty after the clean-up
        Assert.DoesNotContain("Other Ethnicity", r.Columns);
        Assert.All(r.Changes!, c => Assert.NotNull(c.RuleKey));
    }

    [Fact]
    public void Rules_are_independent()
    {
        var rows = new[] { Row("a", caste: "OBC", otherCaste: "NT", eth: "Other", otherEth: "Jainism", disease: "hypertension") };
        var r = Transformer.Run(Headers, rows, new[] { CleanupRules.DiseaseClassify });

        var row = r.Rows[0];
        Assert.Equal("OBC", row["Caste"]);
        Assert.Equal("Other", row["Ethnicity"]);
        Assert.Equal("Blood Pressure", row["Other disease or symptoms of any?"]);
        Assert.All(r.Changes!, c => Assert.Equal(CleanupRules.DiseaseClassify, c.RuleKey));
    }

    [Fact]
    public void Running_again_without_rules_restores_original_values()
    {
        var rows = new List<Dictionary<string, object?>> { Row("a", otherCaste: "NT", caste: "OBC") };
        Transformer.Run(Headers, rows, AllRules);
        var again = Transformer.Run(Headers, rows);
        Assert.Equal("OBC", again.Rows[0]["Caste"]);        // the input rows are never modified
        Assert.Equal("NT", again.Rows[0]["Other Caste"]);
    }

    // ------------------------------------------------------------------ dates and Age

    [Theory]
    [InlineData("05/12/1990", 1990, 12, 5)]   // day first: 5 December, not 12 May
    [InlineData("5/12/1990", 1990, 12, 5)]
    [InlineData("25/12/1990", 1990, 12, 25)]
    [InlineData("05-12-1990", 1990, 12, 5)]
    [InlineData("05.12.1990", 1990, 12, 5)]
    [InlineData("1990-12-05", 1990, 12, 5)]
    [InlineData("5 Dec 1990", 1990, 12, 5)]
    [InlineData(" 05/12/1990 ", 1990, 12, 5)]
    public void Text_dates_are_read_day_first(string text, int y, int m, int d)
    {
        Assert.Equal(new DateTime(y, m, d), Transformer.AsDate(text)!.Value.Date);
    }

    [Theory]
    [InlineData("12/25/1990")]   // month-first is not a valid day-first date: not guessed
    [InlineData("not a date")]
    [InlineData("")]
    public void Unreadable_text_dates_are_null(string text) => Assert.Null(Transformer.AsDate(text));

    [Fact]
    public void Serial_numbers_out_of_range_are_null() => Assert.Null(Transformer.AsDate(9_999_999_999d));

    [Fact]
    public void Age_uses_the_form_date()
    {
        var r = Transformer.Run(Headers, new[] { Row("a", today: new DateTime(2020, 6, 1), dob: new DateTime(2000, 6, 2)) },
                                today: new DateTime(2030, 1, 1));
        Assert.Equal(19, r.Rows[0]["Age"]);   // one day before the 20th birthday on the form date
    }

    [Fact]
    public void Age_falls_back_to_current_date_without_form_date()
    {
        var r = Transformer.Run(Headers, new[] { Row("a", today: null, dob: new DateTime(2000, 1, 1)) }, today: new DateTime(2030, 1, 1));
        Assert.Equal(30, r.Rows[0]["Age"]);
        Assert.Contains(r.Notes, n => n.Contains("no form date"));
    }

    [Fact]
    public void Age_with_text_dob_is_day_first()
    {
        var r = Transformer.Run(Headers, new[] { Row("a", today: new DateTime(1991, 6, 1), dob: "05/12/1990") });
        Assert.Equal(0, r.Rows[0]["Age"]);   // born 5 Dec 1990 -> 0 on 1 Jun 1991 (would be 1 if read as 12 May)
    }

    [Fact]
    public void Age_is_blank_when_birth_is_after_form_date()
    {
        var r = Transformer.Run(Headers, new[] { Row("a", today: new DateTime(2000, 1, 1), dob: new DateTime(2001, 1, 1)) });
        Assert.Null(r.Rows[0]["Age"]);
    }

    [Theory]
    [InlineData(2000, 2, 29, 2001, 2, 28, 1)]   // leap-day birthdays count on 28 Feb in other years
    [InlineData(2000, 2, 29, 2001, 2, 27, 0)]
    [InlineData(2000, 2, 29, 2001, 3, 1, 1)]
    [InlineData(1990, 5, 10, 2020, 5, 10, 30)]
    [InlineData(1990, 5, 10, 2020, 5, 9, 29)]
    public void YearsBetween_counts_whole_years(int y1, int m1, int d1, int y2, int m2, int d2, int expected) =>
        Assert.Equal(expected, Transformer.YearsBetween(new DateTime(y1, m1, d1), new DateTime(y2, m2, d2)));

    // ------------------------------------------------------------------ disease classification

    [Theory]
    [InlineData("heart problem", "Heart Problem")]
    [InlineData("Cardiac arrest", "Heart Problem")]
    [InlineData("Breast cancer", "Cancer")]
    [InlineData("high bp", null)]                        // "high bp" is not just "bp": kept as typed
    [InlineData("BP", "Blood Pressure")]
    [InlineData("hypertension", "Blood Pressure")]
    [InlineData("Type 2", "Diabetes")]
    [InlineData("hypothyroidism", "Thyroid")]
    [InlineData("osteoarthritis", "Arthiritis")]
    [InlineData("TB", "Lung Problem")]
    [InlineData("lungs infection", "Lung Problem")]
    [InlineData("fatty liver", "Liver Problem")]
    [InlineData("uterus fibroid", "PCOD / PCOS / Uterus related issues")]
    [InlineData("Uric acid, pcos", "Uric acid, PCOD / PCOS / Uterus related issues")]
    [InlineData("schizophrenia", null)]
    [InlineData("PNES", null)]
    public void Diseases_are_classified(string text, string? expected) => Assert.Equal(expected, Transformer.ClassifyDisease(text));

    [Theory]
    [InlineData("heartburn")]
    [InlineData("deliver")]          // not "liver"
    [InlineData("computer vision syndrome")]  // not "uter"
    [InlineData("acidity")]
    public void Look_alike_words_are_not_classified(string text) => Assert.Null(Transformer.ClassifyDisease(text));
}
