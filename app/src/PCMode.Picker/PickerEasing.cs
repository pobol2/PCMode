namespace PCMode.Picker;

/// <summary>The picker's animation arithmetic.</summary>
public static class PickerEasing
{
    public static double Lerp(double from, double to, double amount) => from + ((to - from) * amount);

    /// <summary>
    /// The classic 3t^2 - 2t^3 curve, clamped.
    /// </summary>
    /// <remarks>
    /// Monotonic, and it reaches exactly 0 and exactly 1 only where its input
    /// does. That second property is what lets callers use "the eased value is
    /// 1" as the end of an animation without tracking elapsed time separately,
    /// which is how the original decided when to stop its timers.
    /// </remarks>
    public static double SmoothStep(double amount)
    {
        double t = Math.Clamp(amount, 0.0, 1.0);

        return t * t * (3.0 - (2.0 * t));
    }

    /// <summary>
    /// Eased progress from 0 at the start to 1 once the duration has elapsed.
    /// </summary>
    /// <remarks>
    /// A duration of zero completes immediately rather than dividing by it, so
    /// a tuning that switches an animation off cannot produce a NaN that then
    /// spreads through every interpolated value.
    /// </remarks>
    public static double Ease(TimeSpan elapsed, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return 1.0;
        }

        return SmoothStep(Math.Min(1.0, elapsed.TotalMilliseconds / duration.TotalMilliseconds));
    }
}
