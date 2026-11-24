using HisExtract;
using Verita.Domain;
using Verita.Domain.Entities;
using Verita.Infrastructure.DataHub;

namespace Verita.Engine.Tests;

/// <summary>What "Sync again" reads, the care area of an episode, and which periods count as custom.</summary>
public class SyncAgainTests
{
    [Fact]
    public void A_day_synced_before_parts_were_kept_reads_only_the_newer_parts()
    {
        var missing = FactSections.Missing(FactSections.Baseline);
        Assert.Equal([FactSections.Encounters, FactSections.AdmissionRequests], missing.Keys.OrderBy(k => k == FactSections.Encounters ? 0 : 1));
        Assert.Equal(2, missing[FactSections.Encounters]);
    }

    [Fact]
    public void A_day_holding_every_current_part_is_up_to_date()
    {
        var held = FactSections.Parse(FactSections.Format(FactSections.Current));
        Assert.Empty(FactSections.Missing(held));
    }

    [Fact]
    public void A_day_that_held_nothing_reads_everything()
    {
        Assert.Equal(FactSections.Current.Count, FactSections.Missing(FactSections.Parse(null)).Count);
    }

    [Fact]
    public void Reading_the_missing_parts_brings_a_day_up_to_date()
    {
        var after = FactSections.Merge(FactSections.Baseline, FactSections.Missing(FactSections.Baseline));
        Assert.Empty(FactSections.Missing(after));
        Assert.Equal("encounters:2,billing:1,consumption:1,refunds:1,admissionRequests:1", FactSections.Format(after));
    }

    [Fact]
    public void Day_wide_parts_are_not_read_per_episode()
    {
        Assert.Contains(FactSections.Encounters, FactSections.PerEpisode);
        Assert.DoesNotContain(FactSections.AdmissionRequests, FactSections.PerEpisode);
        Assert.DoesNotContain(FactSections.Refunds, FactSections.PerEpisode);
    }

    [Fact]
    public void Admission_request_files_import_as_admission_requests()
    {
        Assert.Equal("AdmissionRequest", HisImportPack.SpecFromFileName("46_AdmissionRequest.csv"));
    }

    [Theory]
    [InlineData("IP", true, CareArea.Or)]
    [InlineData("IP", false, CareArea.Ipd)]
    [InlineData("OP", false, CareArea.Opd)]
    [InlineData("DAYCASE", false, CareArea.Opd)]
    [InlineData("ER", false, CareArea.Er)]
    [InlineData("ER", true, CareArea.Er)]
    public void Each_episode_is_in_one_care_area(string encType, bool surgical, string area) =>
        Assert.Equal(area, CareArea.Of(encType, surgical));

    [Fact]
    public void A_period_over_part_of_months_is_custom_and_a_month_is_not()
    {
        Period P(string code, int m1, int d1, int m2, int d2) => new()
        {
            Id = Guid.NewGuid(), Code = code, StartDate = new DateOnly(2026, m1, d1), EndDate = new DateOnly(2026, m2, d2),
        };
        var feb = P("2026 Feb", 2, 1, 2, 28);
        var mar = P("2026 Mar", 3, 1, 3, 31);
        var ramadan = P("Ramadan", 2, 18, 3, 19);
        var all = new[] { feb, mar, ramadan };
        Assert.True(PeriodCalendar.IsCustom(ramadan, all));
        Assert.False(PeriodCalendar.IsCustom(feb, all));
        var alone = P("Alone", 5, 3, 5, 9);
        Assert.False(PeriodCalendar.IsCustom(alone, [alone]));
    }
}
