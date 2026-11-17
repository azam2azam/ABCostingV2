namespace HisExtract;

/// <summary>
/// Legacy stub. <see cref="FactsExtract"/> no longer calls this — packs carry
/// <c>40_StaffTime</c>, <c>41_EncounterTransfer</c>, <c>39_ConsumptionItem</c> only.
/// Kept so a one-off empty <c>35_ActivityTransaction.csv</c> can still be written if needed.
/// Import drops ActivityTransaction on HIS facts packs via <c>HisImportPack.WithoutHisInventedRows</c>.
/// </summary>
public static class ActivityTxnExtract
{
    public static Task RunAsync(HisConnection db, Settings s, Codes c, CsvZipWriter pack)
    {
        using (pack.Create("35_ActivityTransaction.csv",
            "encounterNo", "serviceRef", "activityCode", "resourceCode", "costCentreCode",
            "quantity", "uom", "occurredAt", "sourceSystem", "sourceRef"))
        { }

        pack.Note("ActivityTransaction: empty stub only — FactsExtract does not emit this file. "
            + "Utilization is StaffTime / EncounterTransfer / ConsumptionItem.");
        return Task.CompletedTask;
    }
}
