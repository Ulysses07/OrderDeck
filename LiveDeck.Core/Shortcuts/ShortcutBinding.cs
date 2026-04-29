namespace LiveDeck.Core.Shortcuts;

/// <summary>Tek bir komut ↔ tuş kombinasyonu eşleşmesi.</summary>
public sealed record ShortcutBinding(string CommandId, KeyChord Chord);
