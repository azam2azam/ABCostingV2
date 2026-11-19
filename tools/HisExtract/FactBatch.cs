namespace HisExtract;

/// <summary>What one facts run writes.</summary>
public enum FactsPart
{
    /// <summary>The whole day, as the CLI does: every encounter of the day and the day's refunds.</summary>
    Day,
    /// <summary>One batch of the day's encounters (<see cref="Settings.Batch"/>) with all their rows; no date-only refunds.</summary>
    Batch,
    /// <summary>Only refunds dated in the day, once the day's batches are done.</summary>
    DayRefunds,
}

/// <summary>An ER assessment of the day and the appointment it is billed through, if any.</summary>
/// <param name="AppointmentId">Linked by INP_ERAssessment.AppointmentID (same patient).</param>
/// <param name="InvoiceAppointment">Linked by the assessment's invoice (HIS_InvoiceMain.AppointmentNo).</param>
/// <param name="InvoiceCompany">The company on that invoice: the ER encounter's payer when the appointment has none.</param>
public sealed record ErLink(int Assessment, int? AppointmentId, int? InvoiceAppointment, int? InvoiceCompany);

/// <summary>
/// The encounters of one batch, by HIS key. A batch keeps an ER assessment and the appointment it is billed
/// through together, because they are one encounter in VERITA.
/// </summary>
public sealed record FactBatch(int[] Ip, int[] Op, int[] Er, ErLink[] ErLinks)
{
    public int Count => Ip.Length + Op.Length + Er.Length;
}
