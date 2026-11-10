using Taipower.Safety.Core.Forms.Workflow;
namespace Taipower.Safety.Core.Forms.Pr1;

/// <summary>項目種類 (legacy Improve.Imp_type)。</summary>
public enum ItemKind
{
    Pro = 0,        // 優點
    Con = 1,        // 缺點
    Recommend = 2,  // 建議改善事項
}

/// <summary>巡查類別 (legacy ddlPatrolGroup 前兩碼)。</summary>
public static class PatrolType
{
    public const string Safety = "SI";      // 工安組巡查
    public const string Team = "SM";        // 巡查小組
    public const string Leaders = "SL";     // 正副主管巡查

    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
    {
        [Safety] = "工安組巡查",
        [Team] = "巡查小組",
        [Leaders] = "正副主管巡查",
    };
}

public sealed class Pr1Item
{
    public long ItemId { get; set; }
    public long FormId { get; set; }
    public ItemKind Kind { get; set; }
    public int Ordinal { get; set; }
    public string Description { get; set; } = "";

    // 缺點分類 (優點/建議改善 不填)
    public int? HazardId { get; set; }
    public int? DeficiencyTypeId { get; set; }
    public int? DeficiencyItemId { get; set; }
    public string? HazardLabel { get; set; }

    // C4 處理改善結果
    public bool? Improved { get; set; }          // T 改善完成 / F 未改善
    public string? NotImprovedReason { get; set; }
    public DateTime? ImprovedDate { get; set; }

    // C5 查證結果
    public bool? Verified { get; set; }
    public string? VerifyReason { get; set; }

    // 罰單連結 (Pr5)
    public string? PenaltyTicketNumber { get; set; }

    public List<Attachment> BeforePhotos { get; set; } = new();
    public List<Attachment> AfterPhotos { get; set; } = new();
}

public sealed class Attachment
{
    public long AttachmentId { get; set; }
    public long ItemId { get; set; }
    public string Category { get; set; } = "Before";   // Before 缺失現況 / After 改善後
    public string OriginalName { get; set; } = "";
    public string StoredName { get; set; } = "";       // 相對於儲存根的路徑
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string? UploadedBy { get; set; }
    public DateTime UploadedAt { get; set; }

    public bool IsAfter => string.Equals(Category, "After", StringComparison.OrdinalIgnoreCase);
}

/// <summary>一張 Pr1 單的完整狀態 (持久化模型)。</summary>
public sealed class Pr1Form : IWorkflowForm
{
    public long FormId { get; set; }
    public string FormNumber { get; set; } = "";
    public int Revision { get; set; }
    public WorkflowStep Step { get; set; } = WorkflowStep.Draft;
    public FormStatus Status { get; set; } = FormStatus.InProgress;

    // 持有/流轉
    public string? CurrentHolderId { get; set; }   // legacy canedit
    public string? PendingRecipientId { get; set; }// legacy mailedit
    public bool InTransit => CurrentHolderId != PendingRecipientId;

    // 表頭
    public string PatrolType { get; set; } = "";
    public DateTime PatrolDate { get; set; }
    public int ProjectId { get; set; }
    public int ContractorId { get; set; }
    public string LeaderId { get; set; } = "";       // 巡查領隊
    public string? TeamMembers { get; set; }
    public string? ImproveGroupId { get; set; }      // 建單時建議的督促組別

    // 流程狀態旗標
    public string? InformGroupId { get; set; }       // C3 決定的督促組別
    public DateTime? ImproveDeadline { get; set; }    // 改善期限
    public DateTime? ImproveCompletedDate { get; set; }
    public bool IsJump { get; set; }
    public bool IsProxy { get; set; }

    // 已簽核人 (供退回定位)
    public string? SafetyOfficerId { get; set; }
    public string? SafetyChiefId { get; set; }
    public string? SafetyManagerId { get; set; }
    public string? ImproveOfficerId { get; set; }
    public string? ImproveChiefId { get; set; }
    public string? ImproveManagerId { get; set; }
    public string? VerifyOfficerId { get; set; }
    public string? VerifyChiefId { get; set; }
    public string? VerifyManagerId { get; set; }

    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public byte[]? RowVersion { get; set; }

    public List<Pr1Item> Items { get; set; } = new();

    /// <summary>顯示單號：原始單號末碼換成重會次數 (legacy 行為)。</summary>
    public string DisplayNumber => Revision == 0 || FormNumber.Length == 0
        ? FormNumber
        : FormNumber[..^1] + Revision;

    // IWorkflowForm：把各階段已簽核人暴露給共用的 AssigneeResolver（供退回定位）。
    public string? OfficerOfRecord(Stage stage) => stage switch
    {
        Stage.SafetyReview => SafetyOfficerId,
        Stage.Improvement => ImproveOfficerId,
        Stage.Verification => VerifyOfficerId,
        _ => null,
    };

    public string? ChiefOfRecord(Stage stage) => stage switch
    {
        Stage.SafetyReview => SafetyChiefId,
        Stage.Improvement => ImproveChiefId,
        Stage.Verification => VerifyChiefId,
        _ => null,
    };

    public string? ManagerOfRecord(Stage stage) => stage switch
    {
        Stage.SafetyReview => SafetyManagerId,
        Stage.Improvement => ImproveManagerId,
        Stage.Verification => VerifyManagerId,
        _ => null,
    };
}

/// <summary>待辦清單一列。</summary>
public sealed record Pr1InboxRow(
    long FormId, string FormNumber, int Revision, WorkflowStep Step, FormStatus Status,
    DateTime PatrolDate, string ProjectName, string ContractorName, string LeaderName,
    bool CanWithdraw, bool CanOpen);
