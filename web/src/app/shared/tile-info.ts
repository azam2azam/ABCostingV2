import { TileInfo } from './ui';

/** Which page the package tiles are on: the rows they add up differ, the formulas do not. */
export type TileScope = 'package' | 'doctor' | 'department' | 'encounter';

const ROWS: Record<TileScope, string> = {
  package: "the package's billed rows in the run (one per doctor per encounter)",
  doctor: "the doctor's package rows in the run (one per package per encounter)",
  department: "the package rows on the department's encounters in the run",
  encounter: "the encounter's package rows in the run",
};
const PROC_ROWS: Record<TileScope, string> = {
  package: '',
  doctor: "the doctor's procedure lines not billed as or under a package",
  department: "the procedure lines on the department's encounters not billed as or under a package",
  encounter: "the encounter's procedure lines not billed as or under a package",
};

export interface PackageTileInfo { performed: TileInfo; resources: TileInfo; billed: TileInfo; claims: TileInfo }

/**
 * How the Performed (cases), Resources Consumed, Amount billed and Claimed tiles are worked out on the package, doctor and
 * department pages. Field names are the lists' column names (Invoice Billed, Consumables, Medicine, Claimed). With
 * non-package items (doctor and department only), procedure lines are added and the package's shared consumable and
 * medicine lines are counted once, as procedure lines.
 */
export function packageTileInfo(scope: TileScope, includeAll = false): PackageTileInfo {
  const rows = ROWS[scope];
  const claimed = 'Lines on invoices HIS put on a claim (HIS_InvoiceMain.IsClaimed = 1), as the lists\' Claimed column.';
  if (!includeAll) {
    return {
      performed: {
        formula: scope === 'package' ? 'Performed (cases) = Cases' : 'Performed (cases) = Σ (Performed package rows)',
        terms: scope === 'package'
          ? [['Cases', "Times HIS billed the package in the period, from the run's package billing summary (the Packages list's Cases)."]]
          : [['Performed', 'Times the package was performed on an encounter: the package line quantity, or its billings through child lines.'],
             ['Package rows', rows]],
        note: scope === 'package' ? null : 'One admission can bill several packages, so this can be more than the number of encounters.',
      },
      resources: {
        formula: `Resources Consumed = ${scope === 'package' ? '(Contracted price × Cases)' : 'Σ (Contracted price × Performed)'} + Σ (Consumables covered) + Σ (Medicine covered) + Σ (Consumables billed) + Σ (Medicine billed)`,
        terms: [
          ['Contracted price', "The package's price in the catalogue, multiplied by its cases per package."],
          ['Consumables covered', 'Consumables HIS records consumed on the encounter but not billed separately (inside the package price), at selling price.'],
          ['Medicine covered', 'Medicine consumed and not billed separately, at MedicinePrice × quantity.'],
          ['Consumables billed', 'Consumable lines billed separately on the encounter, as billed incl. VAT.'],
          ['Medicine billed', 'Medicine lines billed separately on the encounter, as billed incl. VAT.'],
        ],
        note: `Summed over ${rows}. Consumables covered + billed is the lists' Consumables column; the same for Medicine.`,
      },
      billed: {
        formula: 'Amount billed = Σ (Invoice Billed) + Σ (Consumables billed) + Σ (Medicine billed)',
        terms: [
          ['Invoice Billed', "The package's own invoice lines (and child lines HIS bills under it), incl. VAT: the lists' Invoice Billed column."],
          ['Consumables billed', 'Consumable lines billed separately on the encounter, incl. VAT.'],
          ['Medicine billed', 'Medicine lines billed separately on the encounter, incl. VAT.'],
        ],
        note: `Summed over ${rows}. Only what was billed counts: the lists' Consumables and Medicine columns also include what the package covered (consumed inside the package price, not billed), so they are higher than the billed parts here.`,
      },
      claims: {
        formula: 'Claimed = Σ (Claimed)',
        terms: [['Claimed', `Package, consumable and medicine lines on invoices HIS put on a claim (HIS_InvoiceMain.IsClaimed = 1): the lists' Claimed column.`]],
        note: `Summed over ${rows}. Blank until the facts carry the IsClaimed flag.`,
      },
    };
  }
  const procs = PROC_ROWS[scope];
  const shared = "The package's separately billed consumables and medicine are the admission's general lines, which are procedure lines already, so they are counted once, on the procedure side.";
  return {
    performed: {
      formula: 'Performed (cases) = Σ (Performed package rows) + Σ (Performed procedure rows)',
      terms: [
        ['Performed package rows', 'Times each package was performed on an encounter.'],
        ['Performed procedure rows', 'Quantity of each procedure billed outside a package.'],
        ['Package rows', rows],
        ['Procedure rows', procs],
      ],
    },
    resources: {
      formula: 'Resources Consumed = Σ (Contracted price × Performed) + Σ (Consumables covered) + Σ (Medicine covered) + Σ (Invoice Billed procedure lines) + Σ (Consumables procedure lines) + Σ (Medicine procedure lines)',
      terms: [
        ['Contracted price', "The package's catalogue price, multiplied by its cases per package."],
        ['Consumables / Medicine covered', 'Consumed on the encounter inside the package price, at selling price.'],
        ['Invoice Billed procedure lines', 'The procedure lines as billed, incl. VAT.'],
        ['Consumables / Medicine procedure lines', 'Consumption HIS ties to those procedure lines, at selling price.'],
      ],
      note: shared,
    },
    billed: {
      formula: 'Amount billed = Σ (Invoice Billed package lines) + Σ (Invoice Billed procedure lines)',
      terms: [
        ['Invoice Billed package lines', "The package's own invoice lines, incl. VAT."],
        ['Invoice Billed procedure lines', 'Every other line on the encounters, including consumable and medicine lines, incl. VAT.'],
      ],
      note: `Each line once. ${shared}`,
    },
    claims: {
      formula: 'Claimed = Σ (Claimed package own lines) + Σ (Claimed procedure lines)',
      terms: [
        ['Claimed package own lines', "The package's own lines only, without the encounter's shared consumable and medicine lines."],
        ['Claimed procedure lines', 'The procedure lines, including the shared consumable and medicine lines.'],
        ['Claimed', claimed],
      ],
      note: 'Blank until the facts carry the IsClaimed flag.',
    },
  };
}

/**
 * The procedure page's tiles: the procedure's lines billed outside a package in the run (lines HIS bills as or under a
 * package are on the package page), with the consumption HIS ties to them.
 */
export function procedureTileInfo(): PackageTileInfo {
  const rows = "the procedure's lines in the run not billed as or under a package (one row per doctor per encounter)";
  return {
    performed: {
      formula: 'Performed (cases) = Σ (Performed)',
      terms: [['Performed', 'Quantity of the procedure billed on each line.'], ['Procedure lines', rows]],
    },
    resources: {
      formula: 'Resources Consumed = Σ (Invoice Billed) + Σ (Consumables) + Σ (Medicine)',
      terms: [
        ['Invoice Billed', 'The procedure lines as billed, incl. VAT.'],
        ['Consumables', 'Consumables HIS consumption records tie to those lines, at selling price (UnitPrice).'],
        ['Medicine', 'Medicine HIS consumption records tie to those lines, at MedicinePrice × quantity.'],
      ],
      note: `Summed over ${rows}.`,
    },
    billed: {
      formula: 'Amount billed = Σ (Invoice Billed)',
      terms: [['Invoice Billed', 'The procedure lines as billed, incl. VAT.']],
      note: `Summed over ${rows}.`,
    },
    claims: {
      formula: 'Claimed = Σ (Claimed)',
      terms: [['Claimed', 'The procedure lines on invoices HIS put on a claim (HIS_InvoiceMain.IsClaimed = 1).']],
      note: `Summed over ${rows}. Blank until the facts carry the IsClaimed flag.`,
    },
  };
}
