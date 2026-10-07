import { describe, expect, it } from 'vitest';
import { compareDec, directionClass, formatMoney, roundDec } from './core/decimal';

// NG-5: money is formatted from its digits; nothing converts it to a JavaScript number.
describe('decimal formatting', () => {
  it('rounds half away from zero on the digits', () => {
    expect(roundDec('1180.9175', 2)).toBe('1180.92');
    expect(roundDec('-0.005', 2)).toBe('-0.01');
    expect(roundDec('999.995', 2)).toBe('1000.00');
  });
  it('formats money and compact money', () => {
    expect(formatMoney('1180.9175')).toBe('SAR 1,180.92');
    expect(formatMoney('1180917.5', { compact: true })).toBe('SAR 1.18M');
  });
  it('compares decimal strings without floats', () => {
    expect(compareDec('10.5', '9.99')).toBeGreaterThan(0);
    expect(compareDec('-10', '-9')).toBeLessThan(0);
  });
  it('colours by money direction', () => {
    expect(directionClass('120', 'higher')).toBe('good');
    expect(directionClass('120', 'lower')).toBe('bad');
  });
});
