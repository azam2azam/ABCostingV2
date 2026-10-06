// Exports the verita-abc reference implementation's seeded data as a flat-file fixture
// (BRD 23.4). Source tables go to fixture/source, the reference engine's own results to
// fixture/reference for parity comparison. Uses Node's built-in SQLite driver, so it needs
// nothing from the reference project except its database file.
//
//   node tools/fixture/export-reference.mjs "D:/Claude Projects/verita-abc/prisma/dev.db"
import { DatabaseSync } from 'node:sqlite'
import { mkdirSync, writeFileSync } from 'node:fs'
import { gzipSync } from 'node:zlib'
import { join } from 'node:path'

const dbPath = process.argv[2] ?? 'D:/Claude Projects/verita-abc/prisma/dev.db'
const out = join(import.meta.dirname, '..', '..', 'fixture')
const db = new DatabaseSync(dbPath, { readOnly: true })

const SOURCE = [
  'Hospital', 'Facility', 'ServiceLine', 'Department', 'CostCenter',
  'GLAccount', 'Period', 'GLPosting', 'CostPool', 'GLMapping',
  'CostDriver', 'DriverVolume',
  'Resource', 'ResourceRate', 'ResourceCapacity', 'Provider',
  'Activity', 'ActivityResource', 'ActivityDriver', 'Procedure', 'ProcedureRate',
  'CostingVersion', 'CostingDefinition', 'ProcedureActivityLine', 'ProcedureResourceLine',
  'Package', 'PackageComponent', 'DRG',
  'Payer', 'Patient', 'Encounter', 'EncounterService', 'ActivityTransaction', 'RevenueTransaction',
  'AllocationRule', 'AllocationRuleTarget',
  'User', 'ApprovalRequest', 'AuditLog', 'Scenario', 'ScenarioLever',
]
// The reference engine's outputs, used only to prove parity (BRD 23.4).
const REFERENCE = [
  'CalculationRun', 'ResidualBucket', 'Reconciliation', 'CostCenterBalance',
  'ResourceUnitCost', 'ProcedureCost', 'EncounterCost',
]

// Prisma stores DateTime as epoch milliseconds in SQLite.
const DATE_COLS = /^(effectiveFrom|effectiveTo|startDate|endDate|postedAt|admitAt|dischargeAt|performedAt|occurredAt|createdAt|publishedAt|lastLoginAt|requestedAt|reviewedAt|at|executedAt|startedAt|finishedAt|detectedAt|calculatedAt)$/
const BOOL_COLS = /^(isActive|isCapacityDriven|isSurgical|isClosed)$/

function dump(table, dir) {
  const rows = db.prepare(`SELECT * FROM "${table}"`).all()
  const lines = rows.map((r) => {
    const o = {}
    for (const [k, v] of Object.entries(r)) {
      if (v !== null && DATE_COLS.test(k)) o[k] = new Date(Number(v)).toISOString()
      else if (v !== null && BOOL_COLS.test(k)) o[k] = v === 1 || v === true
      else o[k] = v
    }
    return JSON.stringify(o)
  })
  mkdirSync(join(out, dir), { recursive: true })
  writeFileSync(join(out, dir, `${table}.ndjson.gz`), gzipSync(lines.join('\n')))
  return rows.length
}

const manifest = { exportedAt: new Date().toISOString(), source: {}, reference: {} }
for (const t of SOURCE) manifest.source[t] = dump(t, 'source')
for (const t of REFERENCE) manifest.reference[t] = dump(t, 'reference')
writeFileSync(join(out, 'manifest.json'), JSON.stringify(manifest, null, 2))
console.log(JSON.stringify(manifest, null, 2))
