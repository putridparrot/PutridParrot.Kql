namespace Kql.Net.Runtime;

public enum JoinKind
{
    InnerUnique,
    Inner,
    LeftOuter,
    RightOuter,
    FullOuter,
    LeftAnti,
    RightAnti,
    LeftSemi,
    RightSemi
}

/// <summary>In-memory implementation of the KQL join flavours.</summary>
public static class JoinHelper
{
    private static bool HasNull<TKey>(TKey key) => key is KqlRow row && row.HasNullValue();

    public static IEnumerable<TOut> Join<TL, TR, TKey, TOut>(
        IEnumerable<TL> left, IEnumerable<TR> right,
        Func<TL, TKey> leftKey, Func<TR, TKey> rightKey,
        Func<TL, bool, TR, bool, TOut> result, JoinKind kind)
        where TKey : notnull
    {
        var leftRows = left.ToList();
        var rightRows = right.ToList();

        if (kind == JoinKind.InnerUnique)
        {
            // keep the first left row per key, then inner join
            var seen = new HashSet<TKey>();
            leftRows = leftRows.Where(row => seen.Add(leftKey(row))).ToList();
            kind = JoinKind.Inner;
        }

        // KQL join keys compare with ==, so a key with a null part never matches (the row can still appear unmatched)
        var rightLookup = rightRows.Where(x => !HasNull(rightKey(x))).ToLookup(rightKey);
        var leftLookup = leftRows.Where(x => !HasNull(leftKey(x))).ToLookup(leftKey);

        switch (kind)
        {
            case JoinKind.Inner:
                foreach (var l in leftRows)
                foreach (var r in rightLookup[leftKey(l)])
                    yield return result(l, true, r, true);
                break;

            case JoinKind.LeftOuter:
                foreach (var l in leftRows)
                {
                    var matched = false;
                    foreach (var r in rightLookup[leftKey(l)])
                    {
                        matched = true;
                        yield return result(l, true, r, true);
                    }
                    if (!matched) yield return result(l, true, default!, false);
                }
                break;

            case JoinKind.RightOuter:
                foreach (var r in rightRows)
                {
                    var matched = false;
                    foreach (var l in leftLookup[rightKey(r)])
                    {
                        matched = true;
                        yield return result(l, true, r, true);
                    }
                    if (!matched) yield return result(default!, false, r, true);
                }
                break;

            case JoinKind.FullOuter:
                foreach (var l in leftRows)
                {
                    var matched = false;
                    foreach (var r in rightLookup[leftKey(l)])
                    {
                        matched = true;
                        yield return result(l, true, r, true);
                    }
                    if (!matched) yield return result(l, true, default!, false);
                }
                foreach (var r in rightRows)
                    if (!leftLookup.Contains(rightKey(r)))
                        yield return result(default!, false, r, true);
                break;

            case JoinKind.LeftSemi:
                foreach (var l in leftRows)
                    if (rightLookup.Contains(leftKey(l)))
                        yield return result(l, true, default!, false);
                break;

            case JoinKind.LeftAnti:
                foreach (var l in leftRows)
                    if (!rightLookup.Contains(leftKey(l)))
                        yield return result(l, true, default!, false);
                break;

            case JoinKind.RightSemi:
                foreach (var r in rightRows)
                    if (leftLookup.Contains(rightKey(r)))
                        yield return result(default!, false, r, true);
                break;

            case JoinKind.RightAnti:
                foreach (var r in rightRows)
                    if (!leftLookup.Contains(rightKey(r)))
                        yield return result(default!, false, r, true);
                break;
        }
    }
}
