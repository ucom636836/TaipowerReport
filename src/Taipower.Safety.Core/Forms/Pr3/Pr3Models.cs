using Taipower.Safety.Core.Forms.Workflow;

namespace Taipower.Safety.Core.Forms.Pr3;

/// <summary>項目種類 (legacy Improve.Imp_type)。</summary>
public enum Pr3ItemKind
{
    Pro = 0,        // 優點
    Con = 1,        // 缺點
    Recommend = 2,  // 建議改善事項
}

public sealed class Pr3Item
{
    public long ItemId { get; set; }
    public long FormId { get; set; }
    public Pr3ItemKind Kind { get; set; }
    public int Ordinal { get; set; }
    public string Description { get; set; } = "";

    public int? HazardId { get; set; }
    public int? DeficiencyTypeId { get; set; }
    public int? DeficiencyItemId { get; set; }
    public string? HazardLabel { get; set; }

    public bool? Improved { get; set; }
    public string? NotImprovedReason { get; set; }
    public DateTime? ImprovedDate { get; set; }
    public bool? Verified { get; set; }
    public string? VerifyReason { get; set; }
}

/// <summary>一張 Pr3（走動管理紀錄暨追蹤表）單的完整狀態。</summary>
public sealed class Pr3Form
{
    public long FormId { get; set; }
    public string FormNumber { get; set; } = "";
    public int Retimes { get; set; }     // 重會次數 (Pr3_retimes)
    public int Archive { get; set; }     // 持續追蹤迴圈次數 (Pr3_Archive)
    public Pr3Step Step { get; set; } = Pr3Step.Draft;
    public FormStatus Status { get; set; } = FormStatus.InProgress;

    public string? CurrentHolderId { get; set; }
    public string? PendingRecipientId { get; set; }
    public bool InTransit => CurrentHolderId != PendingRecipientId;

    // 表頭（走動管理）
    public string InspectorId { get; set; } = "";    // 巡查人 Pr3_Iman
    public DateTime InspectDate { get; set; }         // 巡查日期 Pr3_Sdate
    public string? InspectTime { get; set; }          // 巡查時間 Pr3_Stime
    public string? Attendants { get; set; }           // 會同人員 Pr3_attman
    public string? Location { get; set; }             // 巡查地點 Pr3_locus
    public string? Target { get; set; }               // 受巡人數/設備 Pr3_target
    public int ProjectId { get; set; }                // 受查工程 Pr3_EnPro_idx
    public int ContractorId { get; set; }             // 受巡承攬商 Pr3_Con_idx
    public string GroupId { get; set; } = "";         // 辦理組別 Pr3_Group_id（S2 動態路由）
    public DateTime? ImproveDeadline { get; set; }    // 限期改善日 Pr3_Edate

    // 流程旗標
    public bool IsJump { get; set; }

    // 已簽核人（供退回定位）：S2 經辦組 / S3 工安組
    public string? HandlingOfficerId { get; set; }
    public string? HandlingChiefId { get; set; }
    public string? HandlingManagerId { get; set; }
    public string? SafetyOfficerId { get; set; }
    public string? SafetyChiefId { get; set; }
    public string? SafetyManagerId { get; set; }

    public string CreatedBy { get; set; } = "";       // 填單人 Pr3_Eman
    public DateTime CreatedAt { get; set; }
    public byte[]? RowVersion { get; set; }

    public List<Pr3Item> Items { get; set; } = new();

    /// <summary>顯示單號：末碼換成重會次數。</summary>
    public string DisplayNumber => Retimes == 0 || FormNumber.Length == 0
        ? FormNumber
        : FormNumber[..^1] + Retimes;

    // 退回定位：依區塊取已簽核人。
    public string? OfficerOfRecord(Pr3Stage stage) => stage == Pr3Stage.HandlingReview ? HandlingOfficerId : SafetyOfficerId;
    public string? ChiefOfRecord(Pr3Stage stage) => stage == Pr3Stage.HandlingReview ? HandlingChiefId : SafetyChiefId;
    public string? ManagerOfRecord(Pr3Stage stage) => stage == Pr3Stage.HandlingReview ? HandlingManagerId : SafetyManagerId;
}

/// <summary>待辦/查詢清單一列。</summary>
public sealed record Pr3InboxRow(
    long FormId, string FormNumber, int Retimes, Pr3Step Step, FormStatus Status,
    DateTime InspectDate, string ProjectName, string ContractorName, string InspectorName,
    bool CanWithdraw, bool CanOpen);
