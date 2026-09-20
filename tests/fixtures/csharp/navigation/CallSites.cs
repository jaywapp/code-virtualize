namespace Fixture.Navigation;

public static class CallSites
{
    public static string Use(Catalog catalog)
    {
        catalog.Load("one");
        catalog.Load("two", 2);
        return catalog.Name;
    }

    public static string Run()
    {
        return Use(new Catalog());
    }

    public static void UseThroughInterface(ILoader loader)
    {
        loader.Load("interface");
    }
}
