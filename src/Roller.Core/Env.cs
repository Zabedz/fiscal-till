namespace Roller.Core;

public static class Env
{
    public static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} is not set");
}
