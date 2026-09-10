namespace Spirectl.Sts2;

public static class Sts2TreeSearch
{
    public static IReadOnlyList<TNode> FindDescendants<TNode>(
        TNode root,
        Func<TNode, IEnumerable<TNode>> getChildren,
        Func<TNode, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(getChildren);
        ArgumentNullException.ThrowIfNull(predicate);

        var matches = new List<TNode>();
        Traverse(root, getChildren, predicate, matches);
        return matches;
    }

    private static void Traverse<TNode>(
        TNode node,
        Func<TNode, IEnumerable<TNode>> getChildren,
        Func<TNode, bool> predicate,
        ICollection<TNode> matches)
    {
        foreach (var child in getChildren(node))
        {
            if (predicate(child))
            {
                matches.Add(child);
            }

            Traverse(child, getChildren, predicate, matches);
        }
    }
}
