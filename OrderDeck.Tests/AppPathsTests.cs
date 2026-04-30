using FluentAssertions;
using OrderDeck.Core;
using Xunit;

namespace OrderDeck.Tests;

public class AppPathsTests
{
    [Fact]
    public void DocumentsRoot_ends_with_OrderDeck()
    {
        AppPaths.DocumentsRoot.Should().EndWith("OrderDeck");
    }

    [Fact]
    public void DatabaseFile_lives_under_documents_data_folder()
    {
        AppPaths.DatabaseFile
            .Should().Contain("OrderDeck")
            .And.Contain("data")
            .And.EndWith("orderdeck.db");
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
