using System.Linq.Expressions;
using Verita.Domain.Entities;

namespace Verita.Infrastructure.Runs;

/// <summary>Which HIS invoice lines count as billed.</summary>
public static class HisInvoiceRules
{
    /// <summary>
    /// Line status 2 is active; 1 is not charged and 4 is cancelled. Invoice type 1 is outpatient and 75
    /// inpatient; type 2 is a child invoice, whose parent already carries the money.
    /// </summary>
    public static readonly Expression<Func<RevenueTransaction, bool>> Counts =
        r => r.HisLineStatus == 2 && (r.HisInvoiceType == 1 || r.HisInvoiceType == 75);
}
