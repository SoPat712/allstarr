namespace allstarr.Controllers.ClientApi;

internal sealed partial class RankedSearchSnapshots
{
    private sealed class ScoreNode(RankedSearchRow row, uint priority)
    {
        internal readonly RankedSearchRow Row = row;
        internal readonly uint Priority = priority;
        internal ScoreNode? Left, Right;
        internal int Count = 1;
        internal double Native = row.Native != null ? row.Score : double.NegativeInfinity;
        internal double External = row.Native == null ? row.Score : double.NegativeInfinity;
    }

    private uint priority = 0x9e3779b9;
    private static int Count(ScoreNode? node) => node?.Count ?? 0;
    private static ScoreNode RefreshNode(ScoreNode node)
    {
        node.Count = Count(node.Left) + Count(node.Right) + 1;
        node.Native = Math.Max(node.Row.Native != null ? node.Row.Score : double.NegativeInfinity,
            Math.Max(node.Left?.Native ?? double.NegativeInfinity, node.Right?.Native ?? double.NegativeInfinity));
        node.External = Math.Max(node.Row.Native == null ? node.Row.Score : double.NegativeInfinity,
            Math.Max(node.Left?.External ?? double.NegativeInfinity, node.Right?.External ?? double.NegativeInfinity));
        return node;
    }

    private static bool HasBarrier(ScoreNode? node, RankedSearchRow row, bool initial) => node != null && (initial
        ? row.Native != null ? node.Native >= row.Score || node.External > row.Score : Math.Max(node.Native, node.External) >= row.Score
        : row.Native != null ? node.Native > row.Score - 5 || node.External > row.Score : Math.Max(node.Native, node.External) > row.Score - 5);

    // A row stops at the rightmost item it cannot pass under the five-point rule.
    // Subtree maxima find that barrier without comparing every earlier item.
    private static int Position(ScoreNode? node, RankedSearchRow row, bool initial)
    {
        if (node == null || !HasBarrier(node, row, initial)) return 0;
        var left = Count(node.Left);
        if (HasBarrier(node.Right, row, initial)) return left + 1 + Position(node.Right, row, initial);
        var passes = initial ? row.Score > node.Row.Score || row.Score == node.Row.Score && row.Native != null && node.Row.Native == null
            : ShouldMoveUp(row, node.Row);
        return passes ? Position(node.Left, row, initial) : left + 1;
    }

    private ScoreNode InsertScore(ScoreNode? root, RankedSearchRow row, bool initial)
    {
        priority ^= priority << 13;
        priority ^= priority >> 17;
        priority ^= priority << 5;
        return Insert(root, Position(root, row, initial), new(row, priority));
    }

    private static ScoreNode Insert(ScoreNode? root, int position, ScoreNode node)
    {
        if (root == null) return node;
        if (node.Priority < root.Priority)
        {
            Split(root, position, out node.Left, out node.Right);
            return RefreshNode(node);
        }
        if (position <= Count(root.Left)) root.Left = Insert(root.Left, position, node);
        else root.Right = Insert(root.Right, position - Count(root.Left) - 1, node);
        return RefreshNode(root);
    }

    private static void Split(ScoreNode? root, int position, out ScoreNode? left, out ScoreNode? right)
    {
        if (root == null) { left = right = null; return; }
        if (position <= Count(root.Left))
        {
            Split(root.Left, position, out left, out root.Left);
            right = RefreshNode(root);
        }
        else
        {
            Split(root.Right, position - Count(root.Left) - 1, out root.Right, out right);
            left = RefreshNode(root);
        }
    }

    private static IEnumerable<RankedSearchRow> Ordered(ScoreNode? node)
    {
        if (node == null) yield break;
        foreach (var row in Ordered(node.Left)) yield return row;
        yield return node.Row;
        foreach (var row in Ordered(node.Right)) yield return row;
    }
}
