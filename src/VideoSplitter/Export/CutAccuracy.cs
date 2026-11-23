using VideoSplitter.Editing;
using VideoSplitter.Models;

namespace VideoSplitter.Export;

public static class CutAccuracy
{
    public static bool NeedsPreciseCut(Segment segment, IReadOnlyList<double> keyframes, double fps)
    {
        double safeFps = fps > 0 ? fps : 30.0;
        double toleranceSeconds = 0.5 / safeFps;

        if (segment.Start.TotalSeconds <= toleranceSeconds)
        {
            return false;
        }

        if (keyframes.Count == 0)
        {
            return false;
        }

        return !KeyframeSnapper.IsOnKeyframe(segment.Start.TotalSeconds, keyframes, toleranceSeconds);
    }
}
