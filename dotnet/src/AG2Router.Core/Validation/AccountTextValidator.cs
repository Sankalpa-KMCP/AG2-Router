namespace AG2Router.Core.Validation;

/// <summary>Bounds new account text inputs in UTF-16 code units, before normalization.
/// Persisted reads and trusted compensation snapshots are deliberately not validated here.</summary>
public static class AccountTextValidator
{
    public const int MaxAliasLength = 64;
    public const int MaxNameLength = 256;
    public const int MaxNotesLength = 2048;

    public static void Validate(string? name = null, string? alias = null, string? notes = null)
    {
        Check(name, "name", MaxNameLength);
        Check(alias, "alias", MaxAliasLength);
        Check(notes, "notes", MaxNotesLength);
    }

    private static void Check(string? value, string field, int maximum)
    {
        if (value?.Length > maximum)
            throw new ArgumentException($"Account {field} must not exceed {maximum} UTF-16 code units.");
    }
}
