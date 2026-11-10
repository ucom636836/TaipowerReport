using System.ComponentModel.DataAnnotations;
using Taipower.Safety.Core.Forms.Pr5;
using Taipower.Safety.Core.Reference;

namespace Taipower.Safety.Web.Models;

public sealed class Pr5CreateViewModel
{
    public Pr5Source Source { get; set; } = Pr5Source.Standalone;
    public long? SourceFormId { get; set; }
    public long? SourceItemId { get; set; }
    public string? SourceFormNumber { get; set; } // 顯示用

    [Range(1, int.MaxValue, ErrorMessage = "請選擇受罰工程")]
    public int ProjectId { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "請選擇承攬商")]
    public int ContractorId { get; set; }

    public string? AuditGroupId { get; set; }
    public int? HazardId { get; set; }
    public int? DeficiencyTypeId { get; set; }
    public int? DeficiencyItemId { get; set; }

    [DataType(DataType.Date)] public DateTime? ViolationDate { get; set; } = DateTime.Today;
    public string? Place { get; set; }
    public string? UnitPeriod { get; set; }
    public string? PenaltyItemCode { get; set; }

    [Range(0, 999999999, ErrorMessage = "罰款金額需為非負數")]
    public decimal? PenaltyAmount { get; set; }

    [DataType(DataType.Date)] public DateTime? ImproveDeadline { get; set; }
    public string? Remark { get; set; }
    public bool Notify { get; set; } = true;
}

public sealed class Pr5DetailViewModel
{
    public required Pr5Penalty Penalty { get; init; }
    public EngineeringProject? Project { get; init; }
    public Contractor? Contractor { get; init; }
    public bool CanManage { get; init; }
}

public static class Pr5Display
{
    public static string StatusName(Pr5Status s) => s switch
    {
        Pr5Status.Issued => "已開立",
        Pr5Status.Received => "已簽收",
        Pr5Status.Void => "已作廢",
        _ => s.ToString(),
    };

    public static string StatusCss(Pr5Status s) => s switch
    {
        Pr5Status.Issued => "bg-warning text-dark",
        Pr5Status.Received => "bg-success",
        Pr5Status.Void => "bg-secondary",
        _ => "bg-light text-dark",
    };

    public static string SourceName(Pr5Source s) => s switch
    {
        Pr5Source.Standalone => "獨立開單",
        Pr5Source.Pr1 => "巡查連動",
        _ => s.ToString(),
    };
}
