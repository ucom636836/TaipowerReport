using Taipower.Safety.Core.Forms.Workflow;

namespace Taipower.Safety.Core.Forms.Pr4;

/// <summary>項目種類。</summary>
public enum Pr4ItemKind
{
    Pro = 0,        // 優點
    Con = 1,        // 缺失
    Recommend = 2,  // 建議改善事項 / 其他
}

public sealed class Pr4Item
{
    public long ItemId { get; set; }
    public long FormId { get; set; }
    public Pr4ItemKind Kind { get; set; }
    public int Ordinal { get; set; }
    public string Description { get; set; } = "";

    public int? HazardId { get; set; }
    public int? DeficiencyTypeId { get; set; }
    public int? DeficiencyItemId { get; set; }
    public string? HazardLabel { get; set; }

    // 框線二 改善事項查證
    public bool? Improved { get; set; }
    public string? NotImprovedReason { get; set; }
    public DateTime? ImprovedDate { get; set; }
    public bool? Verified { get; set; }
    public string? VerifyReason { get; set; }

    // 罰單連結 (Pr5)：逾期/罰款時登記（連動留後續 issue）
    public string? PenaltyTicketNumber { get; set; }
}

/// <summary>一張 Pr4（承攬商工作場所工安抽查紀錄表）單的完整狀態。</summary>
public sealed class Pr4Form
{
    public long FormId { get; set; }
    public string FormNumber { get; set; } = "";
    public int Retimes { get; set; }
    public Pr4Step Step { get; set; } = Pr4Step.Draft;
    public FormStatus Status { get; set; } = FormStatus.InProgress;

    public string? CurrentHolderId { get; set; }
    public string? PendingRecipientId { get; set; }
    public bool InTransit => CurrentHolderId != PendingRecipientId;

    // 表頭
    public DateTime SpotCheckDate { get; set; }       // 抽查日期
    public int ProjectId { get; set; }                // 工程名稱
    public int ContractorId { get; set; }             // 承攬商名稱
    public string? Location { get; set; }             // 抽查地點
    public string? ArriveTime { get; set; }           // 檢驗員到場時間
    public string? LeaveTime { get; set; }            // 檢驗員離場時間
    public string GroupId { get; set; } = "";         // 經辦組別（框線一課長/經理 + 框線二經辦組）
    public DateTime? ImproveDeadline { get; set; }    // 限期改善日期
    public bool NeedImprove { get; set; }             // 紀錄簽發：是否需進行改善

    // 流程旗標
    public bool IsJump { get; set; }

    // 已簽核人（供退回定位）
    public string? InspectionChiefId { get; set; }    // 框線一 課長
    public string? InspectionManagerId { get; set; }  // 框線一 經理
    public string? HandlingOfficerId { get; set; }    // 框線二 經辦
    public string? HandlingChiefId { get; set; }      // 框線二 課長
    public string? HandlingManagerId { get; set; }    // 框線二 經理

    public string CreatedBy { get; set; } = "";       // 檢驗員（起單人）
    public DateTime CreatedAt { get; set; }
    public byte[]? RowVersion { get; set; }

    public List<Pr4Item> Items { get; set; } = new();

    public string DisplayNumber => Retimes == 0 || FormNumber.Length == 0
        ? FormNumber
        : FormNumber[..^1] + Retimes;

    public string? OfficerOfRecord(Pr4Stage stage) => stage == Pr4Stage.Handling ? HandlingOfficerId : null;
    public string? ChiefOfRecord(Pr4Stage stage) => stage == Pr4Stage.Handling ? HandlingChiefId : InspectionChiefId;
    public string? ManagerOfRecord(Pr4Stage stage) => stage == Pr4Stage.Handling ? HandlingManagerId : InspectionManagerId;
}

/// <summary>待辦/查詢清單一列。</summary>
public sealed record Pr4InboxRow(
    long FormId, string FormNumber, int Retimes, Pr4Step Step, FormStatus Status,
    DateTime SpotCheckDate, string ProjectName, string ContractorName, string InspectorName,
    bool CanWithdraw, bool CanOpen);
