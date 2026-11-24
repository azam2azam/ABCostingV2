namespace Verita.Domain.Entities;

// ═══ HIS Sync ═══════════════════════════════════════════════════════════════════════
// What the HIS Sync screen asks for and what the sync worker reports back. VERITA only writes
// settings, catalogue requests and fact date ranges; the worker (a separate service) picks them up,
// updates its progress row and appends to the log.

/// <summary>One hospital's HIS sync settings: one row per hospital.</summary>
public class HisSyncSetting
{
    public Guid Id { get; set; }
    public Guid HospitalId { get; set; }
    /// <summary>The worker checks the HIS catalogue for changes once a day at <see cref="CatalogueTime"/>.</summary>
    public bool CatalogueEnabled { get; set; }
    /// <summary>Local time of the daily catalogue check. Default 01:00.</summary>
    public TimeOnly CatalogueTime { get; set; } = new(1, 0);
    /// <summary>Set by "Sync now"; the worker clears it when it starts the check.</summary>
    public DateTime? CatalogueRequestedAt { get; set; }
    public string? CatalogueRequestedBy { get; set; }
    /// <summary>When the worker last finished a catalogue check.</summary>
    public DateTime? CatalogueLastRunAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";
}

/// <summary>A date range of facts queued for the worker. Dates are inclusive; today is never synced.</summary>
public class HisSyncRange
{
    public Guid Id { get; set; }
    public Guid HospitalId { get; set; }
    public DateOnly FromDate { get; set; }
    public DateOnly ToDate { get; set; }
    /// <summary>QUEUED, RUNNING, DONE, DONE_WITH_ERRORS, FAILED, CANCELLED.</summary>
    public string Status { get; set; } = "QUEUED";
    /// <summary>SYNC (first sync of the dates) or RESYNC ("Sync again": each day reads only the parts it lacks).</summary>
    public string Mode { get; set; } = "SYNC";
    public int DaysTotal { get; set; }
    public int DaysDone { get; set; }
    public DateTime RequestedAt { get; set; }
    public string RequestedBy { get; set; } = "";
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? Error { get; set; }
}

/// <summary>A worker's live progress for one hospital, one row per kind. Written by the worker only.</summary>
public class HisSyncWorker
{
    public Guid Id { get; set; }
    public Guid HospitalId { get; set; }
    /// <summary>CATALOGUE or FACTS.</summary>
    public string Kind { get; set; } = "";
    /// <summary>IDLE, RUNNING, PAUSED, FAILED.</summary>
    public string Status { get; set; } = "IDLE";
    public string? Step { get; set; }
    public Guid? RangeId { get; set; }
    public DateOnly? CurrentDate { get; set; }
    public int BatchesDone { get; set; }
    public int? BatchesTotal { get; set; }
    public int RecordsDone { get; set; }
    public int? RecordsTotal { get; set; }
    public DateTime? StartedAt { get; set; }
    /// <summary>The worker touches this while it is alive; a stale heartbeat means it is not running.</summary>
    public DateTime? HeartbeatAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? LastError { get; set; }
}

/// <summary>An entry in the sync log: settings changes, requests, and what the worker did.</summary>
public class HisSyncLog
{
    public Guid Id { get; set; }
    public Guid HospitalId { get; set; }
    public DateTime At { get; set; }
    /// <summary>CONFIG, CATALOGUE or FACTS.</summary>
    public string Kind { get; set; } = "";
    /// <summary>INFO, WARN or ERROR.</summary>
    public string Level { get; set; } = "INFO";
    public string Message { get; set; } = "";
    public string? Detail { get; set; }
    public DateOnly? FactDate { get; set; }
    /// <summary>The person, or "his-sync" for the worker.</summary>
    public string Actor { get; set; } = "";
}

/// <summary>
/// One day of a fact range as the facts worker syncs it. The day's encounters are listed once
/// (<see cref="HisSyncEncounter"/>) and synced in batches; the day is done when every batch is.
/// </summary>
public class HisSyncDay
{
    public Guid Id { get; set; }
    public Guid HospitalId { get; set; }
    public Guid RangeId { get; set; }
    public DateOnly Date { get; set; }
    /// <summary>PENDING (no list yet), RUNNING, DONE, DONE_WITH_ERRORS, FAILED.</summary>
    public string Status { get; set; } = "PENDING";
    public int Encounters { get; set; }
    public int Batches { get; set; }
    public int Synced { get; set; }
    public int Failed { get; set; }
    /// <summary>Failed attempts to list the day's encounters or to write its own refunds.</summary>
    public int Attempts { get; set; }
    public bool RefundsDone { get; set; }
    /// <summary>The fact parts and versions the day holds, e.g. "encounters:2,billing:1" (HisExtract.FactSections); null before versions were kept.</summary>
    public string? Sections { get; set; }
    /// <summary>The parts this run of the day reads; null reads them all (a first sync).</summary>
    public string? Needs { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? Error { get; set; }
}

/// <summary>An encounter of a sync day, by HIS key, and the batch it is synced in.</summary>
public class HisSyncEncounter
{
    public Guid Id { get; set; }
    public Guid DayId { get; set; }
    /// <summary>IP (AdmissionNo), OP (AppointmentNo) or ER (AssessmentNo).</summary>
    public string Type { get; set; } = "";
    public int HisNo { get; set; }
    /// <summary>Encounters that are one VERITA encounter (an ER visit and its appointment) share a group and a batch.</summary>
    public int GroupNo { get; set; }
    public int BatchNo { get; set; }
    /// <summary>PENDING, SYNCED, FAILED.</summary>
    public string Status { get; set; } = "PENDING";
    public int Attempts { get; set; }
    public string? Error { get; set; }
    public DateTime? SyncedAt { get; set; }
    /// <summary>ER only: the appointment linked by AppointmentID, and by the assessment's invoice (with its company).</summary>
    public int? LinkAppointmentId { get; set; }
    public int? LinkInvoiceAppointment { get; set; }
    public int? LinkInvoiceCompany { get; set; }
}

/// <summary>
/// Catalogue changes found in the HIS, parked until someone applies them on the HIS Sync screen. The extracted
/// pack is kept as an import file; applying it queues that file's import.
/// </summary>
public class HisSyncCatalogueChange
{
    public Guid Id { get; set; }
    public Guid HospitalId { get; set; }
    public Guid? ImportFileId { get; set; }
    public DateTime DetectedAt { get; set; }
    /// <summary>PENDING, APPLIED, DISCARDED, SUPERSEDED (a newer check replaced it).</summary>
    public string Status { get; set; } = "PENDING";
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }
    public int Rejected { get; set; }
    /// <summary>Per table: entity, rows, created, updated, unchanged, rejected (JSON).</summary>
    public string TablesJson { get; set; } = "[]";
    public DateTime? DecidedAt { get; set; }
    public string? DecidedBy { get; set; }
}
