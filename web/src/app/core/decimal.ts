/**
 * Money arrives as a decimal string (NG-5) and is formatted as a string. Nothing here converts a
 * money value to a JavaScript number: rounding and scaling are done on the digits, so the fourth
 * decimal place that crossed the wire is the one that gets rounded.
 */

interface Parts { neg: boolean; int: string; frac: string; }

function parse(v: string | null | undefined): Parts | null {
  if (v === null || v === undefined || v === '') return null;
  const m = /^(-)?(\d*)(?:\.(\d*))?$/.exec(String(v).trim());
  if (!m) return null;
  return { neg: !!m[1], int: (m[2] || '0').replace(/^0+(?=\d)/, ''), frac: m[3] ?? '' };
}

/** Round half away from zero to `dp` places, on the digits. */
export function roundDec(v: string, dp: number): string {
  const p = parse(v);
  if (!p) return v;
  const frac = (p.frac + '0'.repeat(dp + 1)).slice(0, dp + 1);
  let digits = (p.int + frac.slice(0, dp)).split('').map(Number);
  if (Number(frac[dp]) >= 5) {
    let i = digits.length - 1;
    while (i >= 0) {
      if (digits[i] === 9) { digits[i] = 0; i--; } else { digits[i]++; break; }
    }
    if (i < 0) digits = [1, ...digits];
  }
  const s = digits.join('');
  const intPart = s.slice(0, s.length - dp) || '0';
  const fracPart = dp > 0 ? s.slice(s.length - dp) : '';
  const zero = /^0*$/.test(intPart + fracPart);
  return `${p.neg && !zero ? '-' : ''}${intPart}${dp > 0 ? '.' + fracPart : ''}`;
}

/** Move the decimal point (10^-n scaling) without arithmetic. */
export function shiftDec(v: string, n: number): string {
  const p = parse(v);
  if (!p) return v;
  let int = p.int, frac = p.frac;
  if (n < 0) {
    const k = -n;
    int = int.padStart(k + 1, '0');
    frac = int.slice(int.length - k) + frac;
    int = int.slice(0, int.length - k);
  }
  return `${p.neg ? '-' : ''}${int.replace(/^0+(?=\d)/, '')}.${frac || '0'}`;
}

function group(int: string): string {
  return int.replace(/\B(?=(\d{3})+(?!\d))/g, ',');
}

export function formatDec(v: string | null | undefined, dp = 2): string {
  if (v === null || v === undefined || v === '') return '—';
  const r = roundDec(String(v), dp);
  const neg = r.startsWith('-');
  const [i, f] = (neg ? r.slice(1) : r).split('.');
  return `${neg ? '−' : ''}${group(i)}${f ? '.' + f : ''}`;
}

/** "SAR 1,180.92", or compact "SAR 1.18M" for tiles. */
export function formatMoney(v: string | null | undefined, opts: { dp?: number; compact?: boolean; currency?: string } = {}): string {
  if (v === null || v === undefined || v === '') return '—';
  const cur = opts.currency ?? 'SAR';
  const p = parse(String(v));
  if (!p) return String(v);
  if (opts.compact) {
    const len = p.int.length;
    if (len > 9) return `${cur} ${formatDec(shiftDec(String(v), -9), 2)}B`;
    if (len > 6) return `${cur} ${formatDec(shiftDec(String(v), -6), 2)}M`;
    if (len > 3) return `${cur} ${formatDec(shiftDec(String(v), -3), 1)}K`;
    return `${cur} ${formatDec(String(v), 0)}`;
  }
  return `${cur} ${formatDec(String(v), opts.dp ?? 2)}`;
}

export function sign(v: string | null | undefined): -1 | 0 | 1 {
  const p = parse(v ?? '');
  if (!p) return 0;
  if (/^0*$/.test(p.int + p.frac)) return 0;
  return p.neg ? -1 : 1;
}

/** Compare two decimal strings for sorting, without converting to number. */
export function compareDec(a: string | null | undefined, b: string | null | undefined): number {
  const pa = parse(a ?? ''), pb = parse(b ?? '');
  if (!pa && !pb) return 0;
  if (!pa) return -1;
  if (!pb) return 1;
  const sa = sign(a), sb = sign(b);
  if (sa !== sb) return sa - sb;
  const mag = (x: Parts, y: Parts) => {
    if (x.int.length !== y.int.length) return x.int.length - y.int.length;
    if (x.int !== y.int) return x.int < y.int ? -1 : 1;
    const len = Math.max(x.frac.length, y.frac.length);
    const fx = x.frac.padEnd(len, '0'), fy = y.frac.padEnd(len, '0');
    return fx === fy ? 0 : fx < fy ? -1 : 1;
  };
  const m = mag(pa, pb);
  return sa < 0 ? -m : m;
}

/**
 * NG-6: the direction decides the colour. For a higher-is-better figure (margin) a positive
 * value is good; for a lower-is-better figure (cost, variance) a positive value is bad.
 */
export type Direction = 'higher' | 'lower' | 'neutral';
export function directionClass(v: string | null | undefined, dir: Direction): string {
  if (dir === 'neutral') return '';
  const s = sign(v);
  if (s === 0) return '';
  return (s > 0) === (dir === 'higher') ? 'good' : 'bad';
}
