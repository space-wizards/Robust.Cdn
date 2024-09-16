namespace Robust.Cdn.Helpers;

public static class EnumerableHelpers
{
    public static T? SingleOrNull<T>(this IEnumerable<T> enumerable, Func<T, bool> predicate) where T : struct
    {
        foreach (var item in enumerable)
        {
            if (predicate(item))
                return item;
        }

        return null;
    }
}
