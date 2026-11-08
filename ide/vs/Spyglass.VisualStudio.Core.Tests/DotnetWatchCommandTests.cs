using Xunit;

namespace Spyglass.VisualStudio.Core.Tests;

public sealed class DotnetWatchCommandTests
{
    [Theory]
    [InlineData(@"C:\src\App\App.csproj", @"/k dotnet watch run --project ""C:\src\App\App.csproj""")]
    [InlineData(@"C:\my src\A&B\App.csproj", @"/k dotnet watch run --project ""C:\my src\A&B\App.csproj""")]
    public void The_path_is_quoted_without_backslash_escapes(string path, string expected) =>
        Assert.Equal(expected, DotnetWatchCommand.CmdArguments(path));
}
