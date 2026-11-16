namespace CdBurner.App.Mvvm;

/// <summary>
/// A check box the app sets by itself until the user sets it: it turns the box on when the situation calls for it and
/// back off only if it was the one that turned it on. Once the user ticks or unticks it, their choice stands.
/// </summary>
public sealed class AutomaticChoice
{
    private bool _userChose;
    private bool _setAutomatically;

    /// <summary>True when the current value was set by the app, not the user.</summary>
    public bool SetAutomatically => _setAutomatically;

    /// <summary>Call from the property setter the user's check box binds to.</summary>
    public void UserChose()
    {
        _userChose = true;
        _setAutomatically = false;
    }

    /// <summary>The value to apply now, or null for no change.</summary>
    /// <param name="current">The box's current value.</param>
    /// <param name="wanted">Whether the situation calls for the box to be on.</param>
    public bool? Decide(bool current, bool wanted)
    {
        if (_userChose)
        {
            return null;
        }
        if (wanted && !current)
        {
            _setAutomatically = true;
            return true;
        }
        if (!wanted && current && _setAutomatically)
        {
            _setAutomatically = false;
            return false;
        }
        return null;
    }
}
