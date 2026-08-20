using KadrStudio.AiServer.Infrastructure;

namespace KadrStudio.AiServer.Tests;

public sealed class ApiKeyValidatorTests
{
    [Fact]
    public void AcceptsMatchingBearerToken()
    {
        Assert.True(ApiKeyValidator.IsValidBearerHeader("Bearer secret-value", "secret-value"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Basic secret-value")]
    [InlineData("Bearer wrong")]
    [InlineData("Bearer")]
    public void RejectsInvalidAuthorization(string? header)
    {
        Assert.False(ApiKeyValidator.IsValidBearerHeader(header, "secret-value"));
    }
}
