using System;

namespace Fixture.Navigation;

public interface IWorker
{
    string Run();
}

public sealed class Worker : IWorker
{
    public string Run()
    {
        return "run";
    }
}

public static class DynamicCandidates
{
    public static object CreateByReflection()
    {
        return Activator.CreateInstance(typeof(Worker))!;
    }

    public static string ServiceKey => "Fixture.Navigation.IWorker";

    public static string MemberKey => "Run";

    public static Type? ResolveByReflectionString()
    {
        return Type.GetType("Fixture.Navigation.Worker");
    }
}
