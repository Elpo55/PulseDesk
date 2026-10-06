using Sysora.Core;
using Sysora.Core.Results;

namespace Sysora.Tests.Core;

public sealed class AppInfoTests
{
    [Fact]
    public void Identity_ComesFromBuildProperties()
    {
        Assert.Equal("Sysora", AppInfo.Name);
        Assert.Equal("Your PC, Explained.", AppInfo.Tagline);
        Assert.Equal("A lightweight, local-first Windows system dashboard.", AppInfo.Description);
        Assert.Matches(@"^\d+\.\d+\.\d+", AppInfo.Version);
        Assert.DoesNotContain("+", AppInfo.Version);
        Assert.StartsWith("https://github.com/", AppInfo.ProjectUrl);
        Assert.Equal("MIT", AppInfo.License);
        Assert.False(string.IsNullOrWhiteSpace(AppInfo.Authors));
    }

    [Fact]
    public void OperationResult_DistinguishesSuccessAndFailure()
    {
        Assert.True(OperationResult.Success.Succeeded);

        var failure = OperationResult.Failure(OperationError.AccessDenied, "Denied");
        Assert.False(failure.Succeeded);
        Assert.Equal("Denied", failure.Message);
        Assert.Throws<ArgumentException>(() => OperationResult.Failure(OperationError.None, "x"));
    }
}
