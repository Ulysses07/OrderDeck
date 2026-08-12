using FluentAssertions;
using OrderDeck.LicenseServer.Services.Catalog;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Catalog;

public class CategoryPathServiceTests
{
    private static readonly Guid Erkek = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UstGiyim = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Tisort = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Kadin = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static string P(params Guid[] ids)
        => "/" + string.Concat(ids.Select(i => i.ToString("N") + "/"));

    [Fact]
    public void Root_path_is_slash_id_slash()
        => CategoryPathService.BuildPath(null, Erkek).Should().Be(P(Erkek));

    [Fact]
    public void Child_path_appends_to_parent_path()
        => CategoryPathService.BuildPath(P(Erkek), UstGiyim).Should().Be(P(Erkek, UstGiyim));

    [Fact]
    public void Path_is_lowercase_hex_without_dashes()
        => CategoryPathService.BuildPath(null, Erkek)
            .Should().Be("/11111111111111111111111111111111/");

    // Kendi alt ağacına taşıma = döngü. "Erkek"i "Tişört"ün altına taşıyamazsın.
    [Fact]
    public void Moving_into_own_subtree_is_a_cycle()
        => CategoryPathService.WouldCreateCycle(
                movedPath: P(Erkek),
                newParentPath: P(Erkek, UstGiyim, Tisort))
            .Should().BeTrue();

    [Fact]
    public void Moving_onto_itself_is_a_cycle()
        => CategoryPathService.WouldCreateCycle(P(Erkek), P(Erkek)).Should().BeTrue();

    [Fact]
    public void Moving_to_a_sibling_branch_is_not_a_cycle()
        => CategoryPathService.WouldCreateCycle(P(Erkek), P(Kadin)).Should().BeFalse();

    [Fact]
    public void Moving_to_root_is_not_a_cycle()
        => CategoryPathService.WouldCreateCycle(P(Erkek), null).Should().BeFalse();

    // Alt ağaç taşınınca torunların yolu da yeniden yazılmalı.
    [Fact]
    public void Rebase_rewrites_the_prefix_and_keeps_the_tail()
    {
        var oldPath = P(Erkek, UstGiyim);
        var newPath = P(Kadin, UstGiyim);
        var grandChild = P(Erkek, UstGiyim, Tisort);

        CategoryPathService.Rebase(grandChild, oldPath, newPath)
            .Should().Be(P(Kadin, UstGiyim, Tisort));
    }

    [Fact]
    public void Rebase_of_the_moved_node_itself_returns_the_new_path()
    {
        var oldPath = P(Erkek, UstGiyim);
        var newPath = P(Kadin, UstGiyim);

        CategoryPathService.Rebase(oldPath, oldPath, newPath).Should().Be(newPath);
    }
}
