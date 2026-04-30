using FluentAssertions;
using LiveDeck.Core.Shortcuts;
using Xunit;

namespace LiveDeck.Tests.Shortcuts;

public class KeyChordTests
{
    [Theory]
    [InlineData("F1",                          KeyModifiers.None,                                          "F1")]
    [InlineData("Delete",                      KeyModifiers.None,                                          "Delete")]
    [InlineData("Ctrl+P",                      KeyModifiers.Ctrl,                                          "P")]
    [InlineData("Ctrl+Shift+Delete",           KeyModifiers.Ctrl | KeyModifiers.Shift,                     "Delete")]
    [InlineData("Ctrl+Shift+Alt+Win+P",        KeyModifiers.Ctrl | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Win, "P")]
    public void Parse_returns_chord_with_correct_modifiers_and_key(
        string input, KeyModifiers expectedMods, string expectedKey)
    {
        var chord = KeyChord.Parse(input);
        chord.Modifiers.Should().Be(expectedMods);
        chord.Key.Should().Be(expectedKey);
    }

    [Theory]
    [InlineData("ctrl+p",         "Ctrl+P")]
    [InlineData("CTRL+SHIFT+s",   "Ctrl+Shift+S")]
    [InlineData("  Ctrl + P  ",   "Ctrl+P")]
    [InlineData("Shift+Ctrl+P",   "Ctrl+Shift+P")]
    public void Parse_then_ToString_normalizes_to_canonical_form(string input, string canonical)
    {
        KeyChord.Parse(input).ToString().Should().Be(canonical);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+Shift")]
    [InlineData("+P")]
    public void Parse_throws_on_invalid_or_modifier_only(string input)
    {
        var act = () => KeyChord.Parse(input);
        act.Should().Throw<System.FormatException>();
    }

    [Fact]
    public void TryParse_returns_false_for_invalid_input()
    {
        KeyChord.TryParse("Ctrl+", out _).Should().BeFalse();
        KeyChord.TryParse("",      out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_returns_true_and_sets_chord_for_valid_input()
    {
        KeyChord.TryParse("Ctrl+G", out var chord).Should().BeTrue();
        chord.Should().NotBeNull();
        chord!.ToString().Should().Be("Ctrl+G");
    }

    [Fact]
    public void ToString_uses_canonical_modifier_order_ctrl_shift_alt_win()
    {
        var chord = new KeyChord(
            KeyModifiers.Win | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Ctrl, "P");
        chord.ToString().Should().Be("Ctrl+Shift+Alt+Win+P");
    }
}
