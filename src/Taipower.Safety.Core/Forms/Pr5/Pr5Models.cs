namespace Taipower.Safety.Core.Forms.Pr5;

/// <summary>罰單狀態（取代 legacy Pr5_TRANSTS：NULL / '1' / '2'）。</summary>
public enum Pr5Status
{
    Issued = 0,    // 已開立（legacy NULL）
    Received = 1,  // 已簽收（legacy '1'，上傳簽收掃描檔）
    Void = 2,      // 作廢（legacy '2'）
}

/// <summary>罰單來源。</summary>
public enum Pr5Source
{
    Standalone = 0, // 獨立開單（legacy Pr5_type='Pr5'）
    Pr1 = 1,        // 由 Pr1 巡查缺失連動（legacy Pr5_type='Pr1'）
}

/// <summary>工程罰款管理單（Pr5）。一次性開立的罰單，不走 Pr1 的多關卡簽核。</summary>
public sealed class Pr5Penalty
{
    public long PenaltyId { get; set; }
    public string PenaltyNumber { get; set; } = "";
    public Pr5Source Source { get; set; }

    // Pr1 連動來源（獨立開單為 null）
    public long? SourceFormId { get; set; }
    public long? SourceItemId { get; set; }

    public int ProjectId { get; set; }
    public int ContractorId { get; set; }
    public string? AuditGroupId { get; set; }     // 稽核組別

    // 缺失分類
    public int? HazardId { get; set; }
    public int? DeficiencyTypeId { get; set; }
    public int? DeficiencyItemId { get; set; }

    public DateTime? ViolationDate { get; set; }  // 違規日期
    public string? Place { get; set; }            // 違規地點
    public string? UnitPeriod { get; set; }       // 違規機組/期別

    public string? PenaltyItemCode { get; set; }  // 罰款項目代碼（如 67-1 / 68 / 69）
    public decimal? PenaltyAmount { get; set; }   // 罰款金額（正規化為數值）
    public DateTime? ImproveDeadline { get; set; }// 改善期限
    public string? Remark { get; set; }           // 備註（違規當事人等）

    public Pr5Status Status { get; set; } = Pr5Status.Issued;
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? NotifiedAt { get; set; }     // 承攬商通知信寄送時間
    public byte[]? RowVersion { get; set; }
}

/// <summary>查詢一列（含關聯名稱）。</summary>
public sealed record Pr5Row(
    long PenaltyId, string PenaltyNumber, Pr5Source Source, Pr5Status Status,
    string ProjectName, string ContractorName, DateTime? ViolationDate,
    decimal? PenaltyAmount, DateTime? ImproveDeadline, string? PenaltyItemCode);
