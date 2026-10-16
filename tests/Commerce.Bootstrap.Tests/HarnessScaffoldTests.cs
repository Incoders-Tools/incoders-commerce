namespace Commerce.Bootstrap.Tests;

/// <summary>
/// RED -> GREEN proof that `dotnet test Commerce.sln` is a real, runnable command
/// for the commerce-foundation walking skeleton (Unit 1: Decisions and Harness).
/// This is a scaffold test only; it carries no product behavior.
/// </summary>
public class HarnessScaffoldTests
{
    [Fact]
    public void Harness_is_scaffolded_and_runnable()
    {
        Assert.True(HarnessMarker.IsScaffolded);
    }
}
