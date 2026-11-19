import { PackageRow } from '../core/api';
import { Col, hisProcedureId } from './ui';

/** The Packages list's columns, shared by the Packages screen and a department's packages. */
export function packageColumns(): Col<PackageRow>[] {
  return [
    { key: 'code', label: 'Id', value: r => hisProcedureId(r.code), link: r => `/packages/${r.id}` },
    { key: 'name', label: 'Name', minWidth: '22rem' },
    { key: 'cases', label: 'Cases', type: 'num', dp: 0, blankZero: true },
    { key: 'materialsBilled', label: 'Consumables', type: 'money', dp: 0, blankZero: true },
    { key: 'avgLos', label: 'Avg. LOS', type: 'num', dp: 1, blankZero: true,
      value: r => Number(r.encounters) ? Number(r.wardDays ?? 0) / Number(r.encounters) : 0 },
    { key: 'feeBilled', label: 'Invoice Billed', type: 'money', dp: 0, blankZero: true },
    { key: 'medicineBilled', label: 'Medicine', type: 'money', dp: 0, blankZero: true },
    // Claimed: what HIS claimed on invoices it put on a claim (HIS_InvoiceMain.IsClaimed = 1).
    { key: 'claimsReceived', label: 'Claimed', type: 'money', dp: 0, blankZero: true, bad: r => Number(r.claimRejected) > 0 },
  ];
}
