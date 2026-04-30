using FluentAssertions;
using LiveDeck.Core;
using Xunit;

namespace LiveDeck.Tests;

public class AppPathsTests
{
    [Fact]
    public void DocumentsRoot_ends_with_LiveDeck()
    {
        AppPaths.DocumentsRoot.Should().EndWith("LiveDeck");
    }

    [Fact]
    public void DatabaseFile_lives_under_documents_data_folder()
    {
        AppPaths.DatabaseFile
            .Should().Contain("LiveDeck")
            .And.Contain("data")
            .And.EndWith("livedeck.db");
    }

    [Fact]
    public void LogsFolder_is_under_documents_root()
    {
        AppPaths.LogsFolder.Should().StartWith(AppPaths.DocumentsRoot);
        AppPaths.LogsFolder.Should().EndWith("Logs");
    }

    [Fact]
    public void EnsureDirectoriesExist_creates_data_and_logs()
    {
        AppPaths.EnsureDirectoriesExist();

        System.IO.Directory.Exists(AppPaths.DataFolder).Should().BeTrue();
        System.IO.Directory.Exists(AppPaths.LogsFolder).Should().BeTrue();
    }
}
