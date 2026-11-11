using System.ComponentModel.DataAnnotations;
using Taipower.Safety.Core.Forms.Pr4;
using Taipower.Safety.Core.Forms.Workflow;
using Taipower.Safety.Core.Org;
using Taipower.Safety.Core.Reference;

namespace Taipower.Safety.Web.Models;

public sealed class Pr4ItemInput
{
    public string? Description { get; set; }
    public int? HazardId { get; set; }
    public int? DeficiencyTypeId { get; set; }
    public int? DeficiencyItemId { get; set; }
    public string? HazardLabel { get; set; }
}

public sealed class Pr4CreateViewModel
{
    [DataType(DataType.Date)]
    public DateTime SpotCheckDate { get; set; } = DateTime.Today;

    [Range(1, int.MaxValue, ErrorMessage = "請選擇工程")]
    public int ProjectId { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "請選擇承攬商")]
    public int ContractorId { get; set; }

    public string? Location { get; set; }
    public string? ArriveTime { get; set; }
    public string? LeaveTime { get; set; }

    [Required(ErrorMessage = "請選擇經辦組別")]
    public string GroupId { get; set; } = "";

    [DataType(DataType.Date)] public DateTime? ImproveDeadline { get; set; }

    public bool NeedImprove { get; set; }

    public List<Pr4ItemInput> Pros { get; set; } = new();
    public List<Pr4ItemInput> Cons { get; set; } = new();
    public List<Pr4ItemInput> Recommends { get; set; } = new();

    public IReadOnlyList<Pr4Item> ToItems()
    {
        var items = new List<Pr4Item>();
        void Add(IEnumerable<Pr4ItemInput> src, Pr4ItemKind kind)
        {
            var ord = 0;
            foreach (var i in src.Where(x => !string.IsNullOrWhiteSpace(x.Description)))
                items.Add(new Pr4Item
                {
                    Kind = kind,
                    Ordinal = ++ord,
                    Description = i.Description!.Trim(),
                    HazardId = kind == Pr4ItemKind.Con ? i.HazardId : null,
                    DeficiencyTypeId = kind == Pr4ItemKind.Con ? i.DeficiencyTypeId : null,
                    DeficiencyItemId = kind == Pr4ItemKind.Con ? i.DeficiencyItemId : null,
                    HazardLabel = kind == Pr4ItemKind.Con ? i.HazardLabel : null,
                });
        }
        Add(Pros, Pr4ItemKind.Pro);
        Add(Cons, Pr4ItemKind.Con);
        Add(Recommends, Pr4ItemKind.Recommend);
        return items;
    }
}

public sealed class Pr4FormViewModel
{
    public required Pr4Form Form { get; init; }
    public EngineeringProject? Project { get; init; }
    public Contractor? Contractor { get; init; }
    public Employee? Inspector { get; init; }
    public required IReadOnlyList<WorkflowHistoryRow> History { get; init; }
    public bool CanAct { get; init; }
    public required IReadOnlyList<Pr4Action> Allowed { get; init; }

    public IEnumerable<Pr4Item> Pros => Form.Items.Where(i => i.Kind == Pr4ItemKind.Pro);
    public IEnumerable<Pr4Item> Cons => Form.Items.Where(i => i.Kind == Pr4ItemKind.Con);
    public IEnumerable<Pr4Item> Recommends => Form.Items.Where(i => i.Kind == Pr4ItemKind.Recommend);
}

public sealed class Pr4ActRequest
{
    public long FormId { get; set; }
    public Pr4Action Action { get; set; }
    public string? Comment { get; set; }
    public string? AssigneeId { get; set; }
    public bool IsJump { get; set; }
}

/// <summary>Pr4 專屬流程顯示字串。位置名稱共用 WorkflowDisplay.PositionName。</summary>
public static class Pr4Display
{
    public static string StageName(Pr4Stage s) => s switch
    {
        Pr4Stage.Inspection => "檢驗員簽核",
        Pr4Stage.Handling => "經辦組查證",
        Pr4Stage.Closed => "結案",
        _ => s.ToString(),
    };

    public static string StepName(Pr4Step s, FormStatus status) => status switch
    {
        FormStatus.Closed => "已結案",
        FormStatus.Voided => "已作廢",
        _ => $"{StageName(s.Stage)}（{WorkflowDisplay.PositionName(s.Position)}{(s.Transit == Transit.Submitted ? "·待處理" : "·處理中")}）",
    };

    public static string ActionName(Pr4Action a) => a switch
    {
        Pr4Action.Submit => "送出 / 上呈",
        Pr4Action.AssignChief => "派單（課長）",
        Pr4Action.AssignOfficer => "派單（經辦）",
        Pr4Action.Withdraw => "抽回",
        Pr4Action.ReturnToOfficer => "退回經辦",
        Pr4Action.ReturnToChief => "退回課長",
        Pr4Action.ReturnToDesk => "退回登記桌",
        Pr4Action.ReturnToCreator => "退回檢驗員",
        _ => a.ToString(),
    };
}
