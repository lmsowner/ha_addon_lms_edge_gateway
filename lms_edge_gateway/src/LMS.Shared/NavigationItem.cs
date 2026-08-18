namespace LMS.Shared;

public sealed record NavigationItem(
    string Label,
    string Href,
    string Icon,
    string Description);

public enum LmsStatusTone
{
    Neutral,
    Ready,
    Warning,
    Critical
}
