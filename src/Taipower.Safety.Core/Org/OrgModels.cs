namespace Taipower.Safety.Core.Org;

/// <summary>Rank ids carried over from the legacy ORG_Rank table; the workflow relies on them.</summary>
public static class Ranks
{
    public const int Director = 1;        // 處長
    public const int DeputyDirector = 2;  // 副處長
    public const int Manager = 3;         // 經理
    public const int SectionChief = 4;    // 課長
    public const int Officer = 5;         // 經辦
}

/// <summary>Role ids carried over from the legacy ORG_Role table.</summary>
public static class Roles
{
    public const int Head = 1;            // 主管
    public const int DeputyHead = 2;      // 副主管
    public const int RegistrationDesk = 3;// 登記桌
    public const int HandlerA = 4;        // 承辦人A
    public const int HandlerB = 5;        // 承辦人B
    public const int PatrolLeader = 6;    // 巡查領隊
    public const int SafetyAdmin = 8;     // 工安管理員
    public const int SystemAdmin = 9;     // 系統管理員
}

/// <summary>Well-known group ids.</summary>
public static class Groups
{
    public const string DirectorOffice = "00";  // 處長室
    public const string Safety = "69";          // 工業安全衛生組
}

public sealed record OrgGroup(string GroupId, string Name, string? ParentGroupId, int SortOrder);

public sealed record OrgSection(string GroupId, string SectionCode, string Name);

public sealed record OrgRank(int RankId, string Name, int Level);

public sealed record OrgRole(int RoleId, string Name);

public sealed class Employee
{
    public string EmployeeId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Email { get; set; }
    public string? GroupId { get; set; }
    public string? GroupName { get; set; }
    public string? SectionCode { get; set; }
    public string? SectionName { get; set; }
    public int? RankId { get; set; }
    public string? RankName { get; set; }
    public int? RoleId { get; set; }
    public string? RoleName { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Account { get; set; }

    /// <summary>e.g. "工業安全衛生組 課長 王小明"</summary>
    public string Title => string.Join(' ', new[] { GroupName, RankName, Name }.Where(s => !string.IsNullOrEmpty(s)));
}

public sealed class EmployeeQuery
{
    public string? Keyword { get; set; }
    public string? GroupId { get; set; }
    public int? RankId { get; set; }
    public int? RoleId { get; set; }
    public bool IncludeInactive { get; set; }
}
