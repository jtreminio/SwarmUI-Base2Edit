using Xunit;

namespace Base2Edit.Tests;

[Collection("Base2EditTests")]
public class EditPromptParserTests
{
    [Theory]
    [InlineData("1girl, looking at viewer <edit>hello, world", "hello, world")]
    [InlineData("global stuff <edit>hello, world <base>base is ignored", "hello, world")]
    [InlineData("global stuff <edit>hello, world <refiner>refiner is ignored", "hello, world")]
    [InlineData("global stuff <edit>hello, world <region>region is ignored", "hello, world")]
    [InlineData("global stuff <edit//cid=100>hello, world", "hello, world")]
    [InlineData("global stuff <edit:foo>hello, world", "hello, world")]
    [InlineData("global stuff <edit> hello, world   ", "hello, world")]
    [InlineData("global stuff <edit>hello, <edit>world", "hello, world")]
    [InlineData("global stuff <edit> hello, world<broken", "hello, world<broken")]
    [InlineData("global stuff <edit>hello, world<base>ignored<broken", "hello, world")]
    [InlineData("global stuff <edit>hello <unknown>world", "hello <unknown>world")]
    [InlineData("global stuff <edit>hello\nworld", "hello\nworld")]
    [InlineData("global stuff <edit>hello\n<base>ignored", "hello")]
    [InlineData("global stuff <edit>hello <BASE>not a terminator", "hello <BASE>not a terminator")]
    [InlineData("global stuff <EDIT>hello, world", "")]
    [InlineData("global stuff <Edit>hello, world", "")]
    [InlineData("global stuff <edit>one <edit>two <base>ignored", "one two")]
    [InlineData("global stuff <edit>one <base>ignored <edit>two", "one")]
    [InlineData("global stuff <edit>,,,", ",,,")]
    [InlineData("global stuff", "")]
    public void Extract_returns_edit_text(string prompt, string expected)
    {
        Assert.Equal(expected, EditPromptParser.Extract(prompt));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("no edit", false)]
    [InlineData("<edit> ", false)]
    [InlineData("<edit>\n \n", false)]
    [InlineData("<edit>,,,", true)]
    [InlineData("<edit>ok", true)]
    [InlineData("<EDIT>ok", false)]
    [InlineData("<Edit>ok", false)]
    public void HasEditSection_matches_extract_semantics(string prompt, bool expected)
    {
        Assert.Equal(expected, EditPromptParser.HasEditSection(prompt));
    }
}
