using System.Globalization;
using System.Text.RegularExpressions;

namespace TrackStash.Core.Storage;

public sealed record LabelGroup(string Id, string Kind, string Name, string? SortName, string? Description,
    string? Website, string Version, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc, int CurrentMembers = 0);
public sealed record GroupFields(string Name, string? SortName, string? Description, string? Website)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new GroupValidationException("A nonblank group name is required.");
        if (Description?.Length > 12000) throw new GroupValidationException("Description exceeds 12000 characters.");
        if (Website is not null) GroupRules.Url(Website);
    }
}
public sealed record MembershipFields(string Status, string? Since, string? Until, string Source, string? SourceUrl, string? Notes)
{
    public void Validate(DateOnly today)
    {
        if (Status is not ("current" or "former" or "unknown")) throw new GroupValidationException("Choose current, former or unknown membership status.");
        if (string.IsNullOrWhiteSpace(Source) || Source.Length > 200) throw new GroupValidationException("Source is required, at most 200 characters.");
        if (Notes?.Length > 2000) throw new GroupValidationException("Notes exceed 2000 characters.");
        if (SourceUrl is not null) GroupRules.Url(SourceUrl);
        var start = GroupRules.DateRange(Since);
        var end = GroupRules.DateRange(Until);
        if (Status == "current" && Until is not null) throw new GroupValidationException("Current membership cannot have an end date.");
        if (start?.First > today || end?.First > today) throw new GroupValidationException("Future membership dates are not supported.");
        if (start is not null && end is not null && start.Value.First > end.Value.Last)
            throw new GroupValidationException("The membership end cannot precede its start.");
    }
}
public sealed record GroupMembership(string Id, string LabelId, string GroupId, MembershipFields Fields,
    string Version, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc);
public sealed record MembershipView(GroupMembership Membership, RelatedLabel Label, LabelGroup Group);
public sealed record RelatedLabelReason(string Kind, string Id, string Name);
public sealed record RelatedLabelItem(RelatedLabel Label, IReadOnlyList<RelatedLabelReason> Reasons);
public sealed record GroupPage<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize, IReadOnlyDictionary<string, int>? Counts = null);
public sealed record GroupQuery(int Page = 1, int PageSize = 25, string? Search = null, string? Kind = null,
    string? Status = null, string Sort = "name", bool Descending = false)
{
    public void Validate()
    {
        if (Page < 1 || PageSize is < 1 or > 100 || (long)(Page - 1) * PageSize > int.MaxValue)
            throw new GroupValidationException("Use a positive page and page size from 1 to 100.");
        if (Kind is not (null or "family" or "organisation" or "parent")) throw new GroupValidationException("Invalid group/reason kind.");
        if (Status is not (null or "current" or "former" or "unknown" or "all")) throw new GroupValidationException("Invalid membership status filter.");
        if (Sort is not ("name" or "sortName" or "id")) throw new GroupValidationException("Invalid sort field.");
        if (Search is not null && Normalization.EntityNameNormalizer.NormalizeStrict(Search).Length == 0)
            throw new GroupValidationException("Search must contain letters or digits.");
    }
}
public enum GroupWriteStatus { Updated, NotFound, Stale, Conflict }
public sealed record GroupWriteResult(GroupWriteStatus Status, LabelGroup? Group = null, MembershipView? Membership = null);
public sealed class GroupValidationException(string message) : ArgumentException(message);

public static partial class GroupRules
{
    public static void Url(string value)
    {
        try { LabelProfile.ValidateUrl(value, "url"); }
        catch (ArgumentException) { throw new GroupValidationException("Use an absolute HTTP(S) URL of at most 2048 characters without credentials."); }
    }
    public static (DateOnly First, DateOnly Last)? DateRange(string? value)
    {
        if (value is null) return null;
        if (!PartialDate().IsMatch(value)) throw new GroupValidationException("Dates must be YYYY, YYYY-MM or YYYY-MM-DD.");
        var parts = value.Split('-').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        try
        {
            var first = new DateOnly(parts[0], parts.Length > 1 ? parts[1] : 1, parts.Length > 2 ? parts[2] : 1);
            var last = parts.Length == 1 ? new DateOnly(parts[0], 12, 31)
                : parts.Length == 2 ? new DateOnly(parts[0], parts[1], DateTime.DaysInMonth(parts[0], parts[1])) : first;
            return (first, last);
        }
        catch (ArgumentOutOfRangeException) { throw new GroupValidationException("Use a valid Gregorian date in years 0001 to 9999."); }
    }
    [GeneratedRegex(@"\A[0-9]{4}(-[0-9]{2}(-[0-9]{2})?)?\z")]
    private static partial Regex PartialDate();
}

public interface ILabelGroupRepository
{
    ValueTask<GroupPage<LabelGroup>> QueryAsync(GroupQuery query, CancellationToken ct = default);
    ValueTask<LabelGroup?> GetAsync(string id, CancellationToken ct = default);
    ValueTask<GroupWriteResult> CreateAsync(string kind, GroupFields fields, CancellationToken ct = default);
    ValueTask<GroupWriteResult> UpdateAsync(string id, string version, GroupFields fields, CancellationToken ct = default);
    ValueTask<GroupPage<MembershipView>?> MembershipsAsync(string? labelId, string? groupId, GroupQuery query, CancellationToken ct = default);
    ValueTask<MembershipView?> MembershipAsync(string id, CancellationToken ct = default);
    ValueTask<GroupWriteResult> SaveMembershipAsync(string? id, string? version, string labelId, string groupId,
        string labelVersion, string groupVersion, MembershipFields fields, bool confirmRejoin, CancellationToken ct = default);
    ValueTask<GroupWriteResult> RemoveMembershipAsync(string id, string version, CancellationToken ct = default);
    ValueTask<GroupPage<RelatedLabelItem>?> RelatedAsync(string labelId, GroupQuery query, CancellationToken ct = default);
}
