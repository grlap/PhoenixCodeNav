namespace CodeNav.Core.Semantic;

/// <summary>How project inputs are constructed; compiler adapters remain language-specific.
/// Evaluated construction currently applies only to F#.</summary>
public enum ProjectModelMode
{
    Evaluated,
    Simple,
}
