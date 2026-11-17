using Tasky.Helpers;
using Xunit;

namespace Tasky.Tests;

public sealed class DescriptionLinksTests
{
    [Fact]
    public void Finds_addresses_with_short_one_line_labels()
    {
        var links = DescriptionLinks.Find(
            "See https://github.com/owner/repo/issues/12?tab=1#c3, and www.example.com/docs/.\nAgain https://github.com/owner/repo/issues/12?tab=1#c3");

        Assert.Equal(2, links.Count);
        Assert.Equal("https://github.com/owner/repo/issues/12?tab=1#c3", links[0].Url);
        Assert.Equal("github.com/owner/repo/issues/12", links[0].Label);
        Assert.Equal("https://www.example.com/docs/", links[1].Url);
        Assert.Equal("example.com/docs", links[1].Label);
    }

    [Fact]
    public void Keeps_a_closing_bracket_that_belongs_to_the_address()
    {
        var links = DescriptionLinks.Find("(read https://en.wikipedia.org/wiki/Alien_(film))");
        Assert.Equal("https://en.wikipedia.org/wiki/Alien_(film)", Assert.Single(links).Url);
    }

    [Fact]
    public void Text_without_links_drops_the_addresses_and_lines_left_empty()
    {
        Assert.Equal("Fix the login page.\nNotes:",
            DescriptionLinks.WithoutLinks("Fix the login page.\nhttps://example.com/a/very/long/path\nNotes: (https://example.com)"));
        Assert.Equal("", DescriptionLinks.WithoutLinks("https://example.com/only-a-link"));
        Assert.Equal("Plain text stays", DescriptionLinks.WithoutLinks("Plain text stays"));
        Assert.Empty(DescriptionLinks.Find("no links, not even example.com"));
    }
}
