namespace LMS.Shared;

public sealed record LmsMultiSelectOption(
    string Value,
    string Label,
    string SearchText = "");
