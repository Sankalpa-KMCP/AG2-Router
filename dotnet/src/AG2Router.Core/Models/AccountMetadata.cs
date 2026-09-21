using System.Text.Json.Serialization;

namespace AG2Router.Core.Models;

public record AccountMetadata(
    string Id,
    string Email,
    string? Name,
    int Priority,
    bool IsReserve,
    string ValidationStatus,
    bool HasVaultedSession,
    string CreatedAt,
    string UpdatedAt,
    string? LastActiveAt = null,
    string? Notes = null,
    string? Alias = null,
    bool IsActive = false
);

public record AccountsListDto(
    IReadOnlyList<AccountMetadata> Accounts,
    int TotalCount = 0,
    string? ActiveAccountId = null
);

public record UpdateAccountAliasRequest(
    [property: JsonPropertyName("alias")] string? Alias
);

public record SwitchingStatusDto(
    string? ActiveTransactionId,
    string CurrentState,
    object? LastResult = null
);

public record SwitchStatusContainerDto(
    SwitchingStatusDto Status
);
