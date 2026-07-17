using HeadsUp.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HeadsUp.Tests;

public class DirectoryUserTests
{
    [Theory]
    [InlineData("Sarah", "Sarah Whitfield", "Sarah (Sarah Whitfield)")] // differ -> both shown
    [InlineData("Sarah", "Sarah", "Sarah")]                             // same -> once
    [InlineData("Sarah", "sarah", "Sarah")]                             // case-insensitive same
    [InlineData("Sarah", null, "Sarah")]                                // no real name
    [InlineData("Sarah", "  ", "Sarah")]                                // blank real name
    public void Label_shows_real_name_only_when_it_differs(string display, string? real, string expected)
    {
        var user = new DirectoryUser("U1", display, real, null);
        Assert.Equal(expected, user.Label);
    }
}

public class SlackWebUserDirectoryTests
{
    private sealed class NullCredentials : ISlackCredentialProvider
    {
        public string SourceDescription => "none";
        public Task<SlackCredentials?> GetCredentialsAsync(bool forceRefresh, CancellationToken ct) =>
            Task.FromResult<SlackCredentials?>(null);
    }

    [Fact]
    public void Directory_reports_available()
    {
        var directory = new SlackWebUserDirectory(new NullCredentials(), NullLogger<SlackWebUserDirectory>.Instance);
        Assert.True(directory.IsAvailable);
    }

    [Fact]
    public async Task Without_credentials_the_directory_is_empty()
    {
        var directory = new SlackWebUserDirectory(new NullCredentials(), NullLogger<SlackWebUserDirectory>.Instance);

        var users = await directory.GetUsersAsync();

        Assert.Empty(users);
        Assert.Null(await directory.FindByIdAsync("U1"));
    }
}
