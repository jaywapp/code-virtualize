namespace Fixture.Navigation;

public interface ILoader
{
    void Load(string value);
}

public partial class Catalog : ILoader
{
    /// <summary>Loads one value.</summary>
    // This text is resolved lazily from the verified current source.
    public void Load(string value)
    {
    }

    public void Load(string value, int retryCount)
    {
    }

    private T Load<T>(T value)
    {
        return value;
    }

    void ILoader.Load(string value)
    {
        Load(value);
    }
}
