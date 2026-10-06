namespace Verita.Domain;

/// <summary>
/// Money and the conservation primitive (BRD ARCH-7, CR-1, CR-2).
///
/// Money is <see cref="decimal"/> end to end: DECIMAL(19,4) in SQL Server, decimal in C#, a
/// string on the wire. Nothing in the platform holds money in a floating-point type.
///
/// Scale rules (NFR-7): money is stored at four decimal places and presented at two; rates and
/// quantities are stored at six, because a per-minute rate multiplied by six-figure minute
/// counts amplifies rounding badly at two.
/// </summary>
public readonly record struct Money(decimal Value)
{
    public const int StorageScale = 4;
    public const int RateScale = 6;
    public const int PresentationScale = 2;

    public static readonly Money Zero = new(0m);

    public static Money operator +(Money a, Money b) => new(a.Value + b.Value);
    public static Money operator -(Money a, Money b) => new(a.Value - b.Value);
    public static Money operator -(Money a) => new(-a.Value);
    public static Money operator *(Money a, decimal factor) => new(a.Value * factor);
    public static implicit operator decimal(Money m) => m.Value;

    public Money Round2() => new(R2(Value));
    public Money Round4() => new(R4(Value));
    public Money Round6() => new(R6(Value));

    public override string ToString() => Value.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);

    // ── Rounding ──────────────────────────────────────────────────────────────────
    // Away-from-zero matches how finance rounds by hand and how the reference implementation
    // rounded; banker's rounding would make hand-calculated validation disagree on halves.

    public static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
    public static decimal R4(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);
    public static decimal R6(decimal v) => Math.Round(v, 6, MidpointRounding.AwayFromZero);

    /// <summary>Equality to the halala. Money is never compared with ==.</summary>
    public static bool Eq(decimal a, decimal b, decimal tolerance = 0.01m) => Math.Abs(a - b) <= tolerance;

    /// <summary>
    /// Divide, returning null rather than a substitute when the denominator is zero (CR-13).
    /// A zero driver volume is a real condition that routes money to a residual bucket; the
    /// caller has to decide what to do with it.
    /// </summary>
    public static decimal? SafeDiv(decimal numerator, decimal denominator) =>
        denominator == 0m ? null : numerator / denominator;

    /// <summary>Percentage with a zero-safe base, stored at four decimal places.</summary>
    public static decimal Pct(decimal part, decimal whole) => whole == 0m ? 0m : R4(part / whole * 100m);

    /// <summary>
    /// Split an amount across weights so the parts sum back to the amount exactly (CR-2).
    ///
    /// Proportional shares rounded individually lose or create minor units on every
    /// distribution. The part with the largest rounding remainder absorbs the drift, so the
    /// parts always sum back to the amount exactly at the given scale.
    /// </summary>
    public static decimal[] Distribute(decimal amount, IReadOnlyList<decimal> weights, int scale = StorageScale)
    {
        var parts = new decimal[weights.Count];
        decimal total = 0m;
        foreach (var w in weights) total += w;
        if (total == 0m || weights.Count == 0) return parts;

        var target = Math.Round(amount, scale, MidpointRounding.AwayFromZero);
        var raw = new decimal[weights.Count];
        decimal sum = 0m;
        for (var i = 0; i < weights.Count; i++)
        {
            raw[i] = amount * weights[i] / total;
            parts[i] = Math.Round(raw[i], scale, MidpointRounding.AwayFromZero);
            sum += parts[i];
        }

        var drift = target - sum;
        if (drift != 0m)
        {
            var idx = 0;
            var best = -1m;
            for (var i = 0; i < raw.Length; i++)
            {
                var remainder = Math.Abs(raw[i] - parts[i]);
                if (remainder > best) { best = remainder; idx = i; }
            }
            parts[idx] += drift;
        }
        return parts;
    }

    // ── Statistics, in decimal so no float ever touches a cost figure ─────────────

    public static decimal Mean(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0) return 0m;
        decimal s = 0m;
        foreach (var v in values) s += v;
        return s / values.Count;
    }

    /// <summary>Sample standard deviation.</summary>
    public static decimal StdDev(IReadOnlyList<decimal> values)
    {
        if (values.Count < 2) return 0m;
        var m = Mean(values);
        decimal acc = 0m;
        foreach (var v in values) acc += (v - m) * (v - m);
        return Sqrt(acc / (values.Count - 1));
    }

    /// <summary>Percentile by linear interpolation between ranks, from an unsorted sample.</summary>
    public static decimal Percentile(IReadOnlyList<decimal> values, decimal p)
    {
        if (values.Count == 0) return 0m;
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 1) return sorted[0];
        var rank = p / 100m * (sorted.Length - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        if (lo == hi) return sorted[lo];
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    /// <summary>Newton's method square root in decimal.</summary>
    public static decimal Sqrt(decimal x)
    {
        if (x < 0m) throw new ArgumentOutOfRangeException(nameof(x));
        if (x == 0m) return 0m;
        var guess = x > 1m ? x / 2m : 1m;
        for (var i = 0; i < 200; i++)
        {
            var next = (guess + x / guess) / 2m;
            if (next == guess) break;
            guess = next;
        }
        return guess;
    }
}
