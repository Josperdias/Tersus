namespace Tersus.Tests.Framework;

/// <summary>Marks a public instance method as a test. Return type may be void or Task.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute
{
}

/// <summary>Free-form tag used with --category (e.g. "policy", "windows", "static-audit").</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class CategoryAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>Test only runs on Windows; elsewhere it is reported as skipped (never silently passed).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class WindowsOnlyAttribute : Attribute
{
}

/// <summary>Unconditionally skipped, with the reason shown in every report.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class SkipAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}

public sealed class AssertionException(string message) : Exception(message)
{
}

public sealed class SkipException(string reason) : Exception(reason)
{
}
