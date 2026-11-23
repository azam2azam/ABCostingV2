import { TileInfo } from './ui';

/** Which page the package tiles are on: the rows they add up differ, the formulas do not. */
export type TileScope = 'package' | 'doctor' | 'department' | 'encounter';

const ROWS: Record<TileScope, string> = {
  package: "the package's billed rows in the run (one per doctor per episode)",
  doctor: "the doctor's package rows in the run (one per package per episode)",
  department: "the package rows on the department's episodes in the run",
  encounter: "the episode's package rows in the run",
};
const PROC_ROWS: Record<TileScope, string> = {
  package: '',
  doctor: "the doctor's procedure lines not billed as or under a package",
  department: "the procedure lines on the department's episodes not billed as or under a package",
  encounter: "the episode's procedure lines not billed as or under a package",
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
          : [['Performed', 'Times the package was performed on an episode: the package line quantity, or its billings through child lines.'],
             ['Package rows', rows]],
        note: scope === 'package' ? null : 'One admission can bill several packages, so this can be more than the number of episodes.',
      },
      resources: {
        formula: `Resources Consumed = ${scope === 'package' ? '(Contracted price × Cases)' : 'Σ (Contracted price × Performed)'} + Σ (Consumables covered) + Σ (Medicine covered) + Σ (Consumables billed) + Σ (Medicine billed)`,
        terms: [
          ['Contracted price', "The package's price in the catalogue, multiplied by its cases per package."],
          ['Consumables covered', 'Consumables HIS records consumed on the episode but not billed separately (inside the package price), at selling price.'],
          ['Medicine covered', 'Medicine consumed and not billed separately, at MedicinePrice × quantity.'],
          ['Consumables billed', 'Consumable lines billed separately on the episode, as billed incl. VAT.'],
          ['Medicine billed', 'Medicine lines billed separately on the episode, as billed incl. VAT.'],
        ],
        note: `Summed over ${rows}. Consumables covered + billed is the lists' Consumables column; the same for Medicine.`,
      },
      billed: {
        formula: 'Amount billed = Σ (Invoice Billed) + Σ (Consumables billed) + Σ (Medicine billed)',
        terms: [
          ['Invoice Billed', "The package's own invoice lines (and child lines HIS bills under it), incl. VAT: the lists' Invoice Billed column."],
          ['Consumables billed', 'Consumable lines billed separately on the episode, incl. VAT.'],
          ['Medicine billed', 'Medicine lines billed separately on the episode, incl. VAT.'],
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
        ['Performed package rows', 'Times each package was performed on an episode.'],
        ['Performed procedure rows', 'Quantity of each procedure billed outside a package.'],
        ['Package rows', rows],
        ['Procedure rows', procs],
      ],
    },
    resources: {
      formula: 'Resources Consumed = Σ (Contracted price × Performed) + Σ (Consumables covered) + Σ (Medicine covered) + Σ (Invoice Billed procedure lines) + Σ (Consumables procedure lines) + Σ (Medicine procedure lines)',
      terms: [
        ['Contracted price', "The package's catalogue price, multiplied by its cases per package."],
        ['Consumables / Medicine covered', 'Consumed on the episode inside the package price, at selling price.'],
        ['Invoice Billed procedure lines', 'The procedure lines as billed, incl. VAT.'],
        ['Consumables / Medicine procedure lines', 'Consumption HIS ties to those procedure lines, at selling price.'],
      ],
      note: shared,
    },
    billed: {
      formula: 'Amount billed = Σ (Invoice Billed package lines) + Σ (Invoice Billed procedure lines)',
      terms: [
        ['Invoice Billed package lines', "The package's own invoice lines, incl. VAT."],
        ['Invoice Billed procedure lines', 'Every other line on the episodes, including consumable and medicine lines, incl. VAT.'],
      ],
      note: `Each line once. ${shared}`,
    },
    claims: {
      formula: 'Claimed = Σ (Claimed package own lines) + Σ (Claimed procedure lines)',
      terms: [
        ['Claimed package own lines', "The package's own lines only, without the episode's shared consumable and medicine lines."],
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
  const rows = "the procedure's lines in the run not billed as or under a package (one row per doctor per episode)";
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

const CARE_AREAS: [string, string][] = [
  ['IPD', 'Inpatient admissions with no theatre booking.'],
  ['OR', 'Inpatient admissions with a theatre booking in HIS that was not cancelled (INP_OTReservation, Status not 4 or 5).'],
  ['OPD', 'Outpatient appointments (and day cases).'],
  ['ER', 'Emergency assessments.'],
];
const AREA_NAMES: Record<string, string> = { IPD: 'IPD', OR: 'OR', OPD: 'OPD', ER: 'ER' };

/**
 * How the Summary and IPD / OR / OPD / ER dashboard tiles are worked out, keyed by the headline tile key.
 * `area` is the dashboard's care area, or null on Summary (the whole run, split by care area under each tile).
 */
export function dashboardTileInfo(area: string | null): Record<string, TileInfo> {
  const scope = area ? `the ${AREA_NAMES[area] ?? area} episodes in the run` : 'every episode in the run';
  const split = area ? null : 'The figures under the tile split it into IPD, OR, OPD and ER; each episode is in exactly one, so they add up to the total.';
  return {
    encounters: {
      formula: 'Episodes = number of episodes in the run',
      terms: [
        ['Episode', 'An inpatient admission, outpatient appointment or emergency assessment with at least one invoice line HIS Invoicing counts, dated in the period.'],
        ...(area ? CARE_AREAS.filter(([k]) => k === area) : CARE_AREAS),
      ],
      note: split,
    },
    admissionRequests: {
      formula: 'Admission requests = number of HIS admission requests raised in the period, not cancelled',
      terms: [
        ['Admission request', 'A request to admit a patient (HIS INP_AdmissionRequest), raised from an outpatient clinic or the ER, counted by its request date.'],
        ['Not cancelled', 'Requests HIS cancelled (Status 4) or marked inactive are left out.'],
        ...(area ? [] : [
          ['IPD', 'Requests that became an admission without an operation.'],
          ['OR', 'Requests that became an admission with a theatre booking that went ahead.'],
          ['OPD', 'Requests raised from an outpatient clinic and not (yet) admitted.'],
          ['ER', 'Requests raised from the ER and not (yet) admitted.'],
        ] as [string, string][]),
      ],
      note: area ? null : 'Each request is in one care area, so the figures under the tile add up to the total. A request admitted later moves from OPD or ER to IPD or OR once HIS Sync reads its admission.',
    },
    direct: {
      formula: 'Direct cost = Σ (Contracted price × Quantity × Margin Cost %)',
      terms: [
        ['Contracted price', "The procedure's price in the catalogue, effective in the period."],
        ['Quantity', 'The quantity on each counted invoice line.'],
        ['Margin Cost %', "Set in the costing model by procedure (or by its procedure type); a package uses its Package Costing % when it has one."],
      ],
      note: [`Summed over the invoice lines of ${scope}. A line invoiced at no price takes its fixed Zero Price amount instead.`, split].filter(Boolean).join(' '),
    },
    indirect: {
      formula: 'Indirect cost = Σ (Contracted price × Quantity × Overhead %)',
      terms: [
        ['Contracted price', "The procedure's price in the catalogue, effective in the period."],
        ['Quantity', 'The quantity on each counted invoice line.'],
        ['Overhead %', 'Set in the costing model by procedure (or by its procedure type).'],
      ],
      note: [`Summed over the invoice lines of ${scope}. Package lines and lines invoiced at no price carry no indirect cost.`, split].filter(Boolean).join(' '),
    },
    overheads: {
      formula: 'Overheads = Σ over episodes of Σ over overhead lines (Episode value × Overhead %, capped at Overhead SAR)',
      terms: [
        ['Episode value', "Σ (Contracted price × Quantity) over the episode's counted lines."],
        ['Overhead %', 'Each episode level overhead line in the costing model (Indirect Costing). With no %, the line adds its fixed Overhead SAR to every episode.'],
        ['Overhead SAR', 'The cap on the line for one episode, when it has a %.'],
      ],
      note: [`Summed over ${scope}.`, split].filter(Boolean).join(' '),
    },
    revenue: {
      formula: 'Revenue = Σ (Net billed − Refunded)',
      terms: [
        ['Net billed', 'The net amount of each invoice line HIS Invoicing counts (active lines on outpatient and inpatient invoices), dated in the period.'],
        ['Refunded', 'What HIS refunded on the line.'],
      ],
      note: [`Summed over ${scope}.`, split].filter(Boolean).join(' '),
    },
    total: {
      formula: 'Total cost = Direct cost + Indirect cost + Overheads',
      terms: [
        ['Direct cost', 'Contracted price × Quantity × Margin Cost %.'],
        ['Indirect cost', 'Contracted price × Quantity × Overhead %.'],
        ['Overheads', 'Episode level overheads.'],
      ],
      note: split,
    },
    margin: {
      formula: 'Margin = Revenue − Total cost;  Margin % = Margin ÷ Revenue × 100',
      terms: [
        ['Revenue', 'Net billed less refunds on the counted invoice lines.'],
        ['Total cost', 'Direct cost + Indirect cost + Overheads.'],
      ],
      note: split,
    },
  };
}
