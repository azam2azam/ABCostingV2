namespace Verita.Infrastructure.Data;

/// <summary>
/// Gives every hospital its own copy of a costing version not yet owned by one (HospitalId empty),
/// and repoints each hospital's definitions, costing, runs and approvals at its copy. The hospital
/// with the most costing against a version keeps the original row. Nothing is deleted.
/// </summary>
public static class CostingVersionSplit
{
    public const string Sql = """
        CREATE TABLE #VersionMap (OldId uniqueidentifier NOT NULL, HospitalId uniqueidentifier NOT NULL, NewId uniqueidentifier NOT NULL, PRIMARY KEY (OldId, HospitalId));

        WITH Ranked AS (
            SELECT v.Id AS OldId, h.Id AS HospitalId,
                   ROW_NUMBER() OVER (PARTITION BY v.Id ORDER BY s.Setup DESC, d.Defs DESC, h.Code) AS Rn
            FROM CostingVersion v
            CROSS JOIN Hospital h
            CROSS APPLY (SELECT (SELECT COUNT(*) FROM ProcedureTypeCosting x WHERE x.CostingVersionId = v.Id AND x.HospitalId = h.Id)
                              + (SELECT COUNT(*) FROM ProcedureMargin x WHERE x.CostingVersionId = v.Id AND x.HospitalId = h.Id)
                              + (SELECT COUNT(*) FROM EncounterOverhead x WHERE x.CostingVersionId = v.Id AND x.HospitalId = h.Id) AS Setup) s
            CROSS APPLY (SELECT COUNT(*) AS Defs FROM CostingDefinition x JOIN [Procedure] p ON p.Id = x.ProcedureId
                         WHERE x.CostingVersionId = v.Id AND p.HospitalId = h.Id) d
            WHERE v.HospitalId = '00000000-0000-0000-0000-000000000000'
        )
        INSERT #VersionMap (OldId, HospitalId, NewId)
        SELECT OldId, HospitalId, CASE WHEN Rn = 1 THEN OldId ELSE NEWID() END FROM Ranked;

        INSERT CostingVersion (Id, HospitalId, Code, Name, Description, EffectiveFrom, EffectiveTo, Status, PublishedAt, PublishedById, CreatedAt)
        SELECT m.NewId, m.HospitalId, v.Code, v.Name, v.Description, v.EffectiveFrom, v.EffectiveTo, v.Status, v.PublishedAt, v.PublishedById, v.CreatedAt
        FROM #VersionMap m JOIN CostingVersion v ON v.Id = m.OldId WHERE m.NewId <> m.OldId;

        UPDATE v SET HospitalId = m.HospitalId
        FROM CostingVersion v JOIN #VersionMap m ON m.OldId = v.Id AND m.NewId = v.Id;

        UPDATE d SET CostingVersionId = m.NewId
        FROM CostingDefinition d JOIN [Procedure] p ON p.Id = d.ProcedureId
        JOIN #VersionMap m ON m.OldId = d.CostingVersionId AND m.HospitalId = p.HospitalId WHERE m.NewId <> m.OldId;

        UPDATE x SET CostingVersionId = m.NewId
        FROM ProcedureTypeCosting x JOIN #VersionMap m ON m.OldId = x.CostingVersionId AND m.HospitalId = x.HospitalId WHERE m.NewId <> m.OldId;

        UPDATE x SET CostingVersionId = m.NewId
        FROM ProcedureMargin x JOIN #VersionMap m ON m.OldId = x.CostingVersionId AND m.HospitalId = x.HospitalId WHERE m.NewId <> m.OldId;

        UPDATE x SET CostingVersionId = m.NewId
        FROM EncounterOverhead x JOIN #VersionMap m ON m.OldId = x.CostingVersionId AND m.HospitalId = x.HospitalId WHERE m.NewId <> m.OldId;

        UPDATE a SET CostingVersionId = m.NewId
        FROM ApprovalRequest a JOIN CalculationRun r ON r.Id = a.EntityId
        JOIN #VersionMap m ON m.OldId = a.CostingVersionId AND m.HospitalId = r.HospitalId
        WHERE a.EntityType = 'CALCULATION_RUN' AND m.NewId <> m.OldId;

        UPDATE x SET CostingVersionId = m.NewId
        FROM CalculationRun x JOIN #VersionMap m ON m.OldId = x.CostingVersionId AND m.HospitalId = x.HospitalId WHERE m.NewId <> m.OldId;

        INSERT ApprovalRequest (Id, EntityType, EntityId, EntityLabel, CostingVersionId, Stage, RequestedById, ReviewedById, RequestNote, ReviewNote, RequestedAt, ReviewedAt)
        SELECT NEWID(), a.EntityType, m.NewId, a.EntityLabel, m.NewId, a.Stage, a.RequestedById, a.ReviewedById, a.RequestNote, a.ReviewNote, a.RequestedAt, a.ReviewedAt
        FROM ApprovalRequest a JOIN #VersionMap m ON m.OldId = a.EntityId
        WHERE a.EntityType = 'COSTING_VERSION' AND m.NewId <> m.OldId;

        DROP TABLE #VersionMap;
        """;
}
