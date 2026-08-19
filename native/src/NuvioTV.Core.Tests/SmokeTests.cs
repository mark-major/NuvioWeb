using Xunit;

namespace NuvioTV.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void CoreInfo_Name_IsNuvio()
    {
        Assert.Equal("nuvio", CoreInfo.Name);
    }
}
