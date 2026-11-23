namespace VideoSplitter.Editing;

public static class KeyframeSnapper
{
    private const double NavigationEpsilonSeconds = 0.0005;

    public static double Snap(double time, IReadOnlyList<double> sortedKeyframes, double toleranceSeconds)
    {
        if (sortedKeyframes.Count == 0 || toleranceSeconds < 0)
        {
            return time;
        }

        double nearest = FindNearest(time, sortedKeyframes);
        return Math.Abs(nearest - time) <= toleranceSeconds ? nearest : time;
    }

    public static bool IsOnKeyframe(double time, IReadOnlyList<double> sortedKeyframes, double toleranceSeconds)
    {
        if (sortedKeyframes.Count == 0 || toleranceSeconds < 0)
        {
            return false;
        }

        double nearest = FindNearest(time, sortedKeyframes);
        return Math.Abs(nearest - time) <= toleranceSeconds;
    }

    public static double? Previous(double time, IReadOnlyList<double> sortedKeyframes)
    {
        if (sortedKeyframes.Count == 0)
        {
            return null;
        }

        double target = time - NavigationEpsilonSeconds;
        int idx = LowerBound(sortedKeyframes, target);
        int prevIdx = idx - 1;
        return prevIdx >= 0 ? sortedKeyframes[prevIdx] : null;
    }

    public static double? Next(double time, IReadOnlyList<double> sortedKeyframes)
    {
        if (sortedKeyframes.Count == 0)
        {
            return null;
        }

        double target = time + NavigationEpsilonSeconds;
        int idx = UpperBound(sortedKeyframes, target);
        return idx < sortedKeyframes.Count ? sortedKeyframes[idx] : null;
    }

    private static double FindNearest(double time, IReadOnlyList<double> sortedKeyframes)
    {
        int idx = LowerBound(sortedKeyframes, time);
        if (idx == 0)
        {
            return sortedKeyframes[0];
        }

        if (idx >= sortedKeyframes.Count)
        {
            return sortedKeyframes[^1];
        }

        double after = sortedKeyframes[idx];
        double before = sortedKeyframes[idx - 1];
        return Math.Abs(time - before) <= Math.Abs(after - time) ? before : after;
    }

    private static int LowerBound(IReadOnlyList<double> sorted, double value)
    {
        int lo = 0;
        int hi = sorted.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (sorted[mid] < value)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private static int UpperBound(IReadOnlyList<double> sorted, double value)
    {
        int lo = 0;
        int hi = sorted.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (sorted[mid] <= value)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}
