using System.Diagnostics;
using VideoSplitter.Models;

namespace VideoSplitter.Editing;

public readonly record struct SegmentEditResult(
    bool Success,
    IReadOnlyList<Segment> Segments,
    string? ErrorMessage = null,
    Guid? CreatedSegmentId = null,
    int AllocatedSegmentNames = 0);

public static class SegmentEditor
{
    public static readonly TimeSpan MinDuration = TimeSpan.FromSeconds(0.1);

    public static IReadOnlyList<Segment> CreateInitial(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return Array.Empty<Segment>();
        }

        Segment[] result =
        [
            new Segment(Guid.NewGuid(), TimeSpan.Zero, duration, true, "Segment 1")
        ];
        AssertInvariants(result);
        return result;
    }

    public static int ContainingIndex(IReadOnlyList<Segment> list, TimeSpan t)
    {
        if (list.Count == 0)
        {
            return -1;
        }

        if (t <= list[0].Start)
        {
            return 0;
        }

        if (t >= list[^1].End)
        {
            return list.Count - 1;
        }

        int lo = 0;
        int hi = list.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) / 2);
            Segment seg = list[mid];
            if (t < seg.Start)
            {
                hi = mid - 1;
            }
            else if (t >= seg.End)
            {
                lo = mid + 1;
            }
            else
            {
                return mid;
            }
        }

        return list.Count - 1;
    }

    public static SegmentEditResult Split(IReadOnlyList<Segment> list, TimeSpan t, int nextSegmentNumber)
    {
        int idx = ContainingIndex(list, t);
        if (idx < 0)
        {
            return Fail(list, "Aucun segment disponible.");
        }

        Segment target = list[idx];
        if (t - target.Start < MinDuration || target.End - t < MinDuration)
        {
            return Fail(list, "Impossible de couper ici : segment trop court (< 0,1 s).");
        }

        Segment left = target with { End = t };
        var right = new Segment(Guid.NewGuid(), t, target.End, target.Enabled, $"Segment {nextSegmentNumber}");

        var next = new Segment[list.Count + 1];
        for (int i = 0; i < idx; i++)
        {
            next[i] = list[i];
        }

        next[idx] = left;
        next[idx + 1] = right;

        for (int i = idx + 1; i < list.Count; i++)
        {
            next[i + 1] = list[i];
        }

        AssertInvariants(next);
        return new SegmentEditResult(true, next, CreatedSegmentId: right.Id);
    }

    public static TimeSpan ClampBoundary(IReadOnlyList<Segment> list, int boundaryIndex, TimeSpan t)
    {
        if (boundaryIndex < 0 || boundaryIndex >= list.Count - 1)
        {
            return t;
        }

        TimeSpan min = list[boundaryIndex].Start + MinDuration;
        TimeSpan max = list[boundaryIndex + 1].End - MinDuration;
        if (min > max)
        {
            return min;
        }

        if (t < min)
        {
            return min;
        }

        return t > max ? max : t;
    }

    public static SegmentEditResult MoveBoundary(IReadOnlyList<Segment> list, int boundaryIndex, TimeSpan t)
    {
        if (boundaryIndex < 0 || boundaryIndex >= list.Count - 1)
        {
            return Fail(list, "Frontiere invalide.");
        }

        TimeSpan clamped = ClampBoundary(list, boundaryIndex, t);
        var next = new Segment[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            next[i] = list[i];
        }

        next[boundaryIndex] = list[boundaryIndex] with { End = clamped };
        next[boundaryIndex + 1] = list[boundaryIndex + 1] with { Start = clamped };

        AssertInvariants(next);
        return new SegmentEditResult(true, next);
    }

    public static SegmentEditResult SetIn(IReadOnlyList<Segment> list, TimeSpan t, int nextSegmentNumber)
    {
        int idx = ContainingIndex(list, t);
        if (idx < 0)
        {
            return Fail(list, "Aucun segment disponible.");
        }

        if (idx == 0)
        {
            Segment first = list[0];
            if (t - first.Start < MinDuration || first.End - t < MinDuration)
            {
                return Fail(list, "Impossible de rogner le debut ici : segment trop court (< 0,1 s).");
            }

            Segment leftDisabled = first with { End = t, Enabled = false };
            var rightKept = new Segment(Guid.NewGuid(), t, first.End, first.Enabled, $"Segment {nextSegmentNumber}");

            var next = new Segment[list.Count + 1];
            next[0] = leftDisabled;
            next[1] = rightKept;
            for (int i = 1; i < list.Count; i++)
            {
                next[i + 1] = list[i];
            }

            AssertInvariants(next);
            return new SegmentEditResult(true, next, CreatedSegmentId: rightKept.Id);
        }

        Segment prev = list[idx - 1];
        Segment current = list[idx];
        if (t - prev.Start < MinDuration || current.End - t < MinDuration)
        {
            return Fail(list, "Impossible de definir le point d'entree ici : segment trop court (< 0,1 s).");
        }

        return MoveBoundary(list, idx - 1, t);
    }

    public static SegmentEditResult SetOut(IReadOnlyList<Segment> list, TimeSpan t, int nextSegmentNumber)
    {
        int idx = ContainingIndex(list, t);
        if (idx < 0)
        {
            return Fail(list, "Aucun segment disponible.");
        }

        if (idx == list.Count - 1)
        {
            Segment last = list[^1];
            if (t - last.Start < MinDuration || last.End - t < MinDuration)
            {
                return Fail(list, "Impossible de rogner la fin ici : segment trop court (< 0,1 s).");
            }

            Segment leftKept = last with { End = t };
            var rightDisabled = new Segment(Guid.NewGuid(), t, last.End, false, $"Segment {nextSegmentNumber}");

            var next = new Segment[list.Count + 1];
            for (int i = 0; i < list.Count - 1; i++)
            {
                next[i] = list[i];
            }

            next[^2] = leftKept;
            next[^1] = rightDisabled;

            AssertInvariants(next);
            return new SegmentEditResult(true, next, CreatedSegmentId: leftKept.Id);
        }

        Segment current = list[idx];
        Segment following = list[idx + 1];
        if (t - current.Start < MinDuration || following.End - t < MinDuration)
        {
            return Fail(list, "Impossible de definir le point de sortie ici : segment trop court (< 0,1 s).");
        }

        return MoveBoundary(list, idx, t);
    }

    public static SegmentEditResult DefineSegment(
        IReadOnlyList<Segment> list,
        TimeSpan start,
        TimeSpan end,
        int nextSegmentNumber)
    {
        if (list.Count == 0)
        {
            return Fail(list, "Aucun segment disponible.");
        }

        TimeSpan totalEnd = list[^1].End;
        TimeSpan clampedStart = start < TimeSpan.Zero ? TimeSpan.Zero : (start > totalEnd ? totalEnd : start);
        TimeSpan clampedEnd = end < TimeSpan.Zero ? TimeSpan.Zero : (end > totalEnd ? totalEnd : end);

        if (clampedEnd <= clampedStart || clampedEnd - clampedStart < MinDuration)
        {
            return Fail(list, "Impossible de definir le segment : duree trop courte (< 0,1 s).");
        }

        int startIdx = ContainingIndex(list, clampedStart);
        Segment startSeg = list[startIdx];
        TimeSpan effectiveStart = clampedStart;
        if (effectiveStart - startSeg.Start < MinDuration)
        {
            effectiveStart = startSeg.Start;
        }
        else if (startSeg.End - effectiveStart < MinDuration)
        {
            if (startSeg.End < clampedEnd && clampedEnd - startSeg.End >= MinDuration)
            {
                effectiveStart = startSeg.End;
            }
            else if (startSeg.End - startSeg.Start >= MinDuration + MinDuration)
            {
                effectiveStart = startSeg.End - MinDuration;
            }
        }

        int endIdx = ContainingIndex(list, clampedEnd);
        Segment endSeg = list[endIdx];
        TimeSpan effectiveEnd = clampedEnd;
        if (endSeg.End - effectiveEnd < MinDuration)
        {
            effectiveEnd = endSeg.End;
        }
        else if (effectiveEnd - endSeg.Start < MinDuration)
        {
            if (endSeg.Start > effectiveStart && endSeg.Start - effectiveStart >= MinDuration)
            {
                effectiveEnd = endSeg.Start;
            }
            else if (endSeg.End - endSeg.Start >= MinDuration + MinDuration)
            {
                effectiveEnd = endSeg.Start + MinDuration;
            }
        }

        if (effectiveEnd <= effectiveStart || effectiveEnd - effectiveStart < MinDuration)
        {
            return Fail(list, "Impossible de definir le segment : duree trop courte (< 0,1 s).");
        }

        bool isInitialFullSegment = list.Count == 1
            && list[0].Enabled
            && list[0].Start == TimeSpan.Zero
            && list[0].End == totalEnd;

        if (isInitialFullSegment && effectiveStart == TimeSpan.Zero && effectiveEnd == totalEnd)
        {
            return new SegmentEditResult(true, list, CreatedSegmentId: list[0].Id);
        }

        var nextList = new List<Segment>(list.Count + 2);
        int nameCounter = nextSegmentNumber;
        bool inserted = false;
        Guid createdId = Guid.Empty;

        for (int i = 0; i < list.Count; i++)
        {
            Segment seg = list[i];

            if (seg.End <= effectiveStart)
            {
                nextList.Add(seg);
                continue;
            }

            if (seg.Start >= effectiveEnd)
            {
                if (!inserted)
                {
                    var created = new Segment(Guid.NewGuid(), effectiveStart, effectiveEnd, true, $"Segment {nameCounter++}");
                    createdId = created.Id;
                    nextList.Add(created);
                    inserted = true;
                }

                nextList.Add(seg);
                continue;
            }

            bool hasLeftRemainder = seg.Start < effectiveStart;
            bool hasRightRemainder = seg.End > effectiveEnd;

            if (hasLeftRemainder)
            {
                bool leftEnabled = isInitialFullSegment ? false : seg.Enabled;
                nextList.Add(seg with { End = effectiveStart, Enabled = leftEnabled });
            }

            if (!inserted)
            {
                string segName = isInitialFullSegment && !hasLeftRemainder
                    ? seg.Name
                    : $"Segment {nameCounter++}";
                var created = new Segment(Guid.NewGuid(), effectiveStart, effectiveEnd, true, segName);
                createdId = created.Id;
                nextList.Add(created);
                inserted = true;
            }

            if (hasRightRemainder)
            {
                bool rightEnabled = isInitialFullSegment ? false : seg.Enabled;
                if (hasLeftRemainder || (isInitialFullSegment && !hasLeftRemainder))
                {
                    nextList.Add(new Segment(Guid.NewGuid(), effectiveEnd, seg.End, rightEnabled, $"Segment {nameCounter++}"));
                }
                else
                {
                    nextList.Add(seg with { Start = effectiveEnd, Enabled = rightEnabled });
                }
            }
        }

        if (!inserted)
        {
            var created = new Segment(Guid.NewGuid(), effectiveStart, effectiveEnd, true, $"Segment {nameCounter++}");
            createdId = created.Id;
            nextList.Add(created);
        }

        Segment[] resultArray = nextList.ToArray();
        AssertInvariants(resultArray);
        return new SegmentEditResult(
            true,
            resultArray,
            CreatedSegmentId: createdId,
            AllocatedSegmentNames: nameCounter - nextSegmentNumber);
    }

    public static SegmentEditResult MergeBoundary(IReadOnlyList<Segment> list, int boundaryIndex)
    {
        if (boundaryIndex < 0 || boundaryIndex >= list.Count - 1)
        {
            return Fail(list, "Aucun marqueur valide a supprimer.");
        }

        Segment merged = list[boundaryIndex] with { End = list[boundaryIndex + 1].End };
        var next = new Segment[list.Count - 1];

        for (int i = 0; i < boundaryIndex; i++)
        {
            next[i] = list[i];
        }

        next[boundaryIndex] = merged;

        for (int i = boundaryIndex + 2; i < list.Count; i++)
        {
            next[i - 1] = list[i];
        }

        AssertInvariants(next);
        return new SegmentEditResult(true, next, CreatedSegmentId: merged.Id);
    }

    public static SegmentEditResult SetEnabled(IReadOnlyList<Segment> list, Guid id, bool enabled)
    {
        int idx = FindIndexById(list, id);
        if (idx < 0)
        {
            return Fail(list, "Segment introuvable.");
        }

        if (list[idx].Enabled == enabled)
        {
            return new SegmentEditResult(true, list);
        }

        var next = new Segment[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            next[i] = i == idx ? list[i] with { Enabled = enabled } : list[i];
        }

        AssertInvariants(next);
        return new SegmentEditResult(true, next);
    }

    public static SegmentEditResult Rename(IReadOnlyList<Segment> list, Guid id, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Fail(list, "Le nom du segment ne peut pas etre vide.");
        }

        int idx = FindIndexById(list, id);
        if (idx < 0)
        {
            return Fail(list, "Segment introuvable.");
        }

        string trimmed = name.Trim();
        if (string.Equals(list[idx].Name, trimmed, StringComparison.Ordinal))
        {
            return new SegmentEditResult(true, list);
        }

        var next = new Segment[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            next[i] = i == idx ? list[i] with { Name = trimmed } : list[i];
        }

        AssertInvariants(next);
        return new SegmentEditResult(true, next);
    }

    private static int FindIndexById(IReadOnlyList<Segment> list, Guid id)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private static SegmentEditResult Fail(IReadOnlyList<Segment> list, string message)
        => new(false, list, message);

    [Conditional("DEBUG")]
    private static void AssertInvariants(IReadOnlyList<Segment> list)
    {
        if (list.Count == 0)
        {
            return;
        }

        Debug.Assert(list[0].Start == TimeSpan.Zero, "Le premier segment doit commencer a 0.");
        bool enforceMin = list[^1].End >= MinDuration;

        for (int i = 0; i < list.Count; i++)
        {
            Segment s = list[i];
            Debug.Assert(s.Start < s.End, "Segment invalide : Start >= End.");
            if (enforceMin)
            {
                Debug.Assert(s.End - s.Start >= MinDuration, "Segment plus court que MinDuration (0,1 s).");
            }

            if (i < list.Count - 1)
            {
                Debug.Assert(s.End == list[i + 1].Start, "Segments non contigus.");
            }
        }
    }
}
