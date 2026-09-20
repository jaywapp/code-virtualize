namespace Fixture.Diff;

public static class ReviewTarget
{
    public static string Existing()
    {
        return "dirty-before-session";
    }

    public static string Removed()
    {
        return "remove-me";
    }
}
