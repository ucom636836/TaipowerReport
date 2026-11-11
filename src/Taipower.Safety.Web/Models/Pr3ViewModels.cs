using System.ComponentModel.DataAnnotations;
using Taipower.Safety.Core.Forms.Pr3;
using Taipower.Safety.Core.Forms.Workflow;
using Taipower.Safety.Core.Org;
using Taipower.Safety.Core.Reference;

namespace Taipower.Safety.Web.Models;

public sealed class Pr3ItemInput
{
    public string? Description { get; set; }
    public int? HazardId { get; set; }
    public int? DeficiencyTypeId { get; set; }
    public int? DeficiencyItemId { get; set; }
    public string? HazardLabel { get; set; }
}

public sealed class Pr3CreateViewModel
{
    [Required(ErrorMessage = "請選擇巡查人")]
    public string InspectorId { get; set; } = "";

    [DataType(DataType.Date)]
    public DateTime InspectDate { get; set; } = DateTime.Today;

    public string? InspectTime { get; set; }
    public string? Attendants { get; set; }
    public string? Location { get; set; }
    public string? Target { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "請選擇工程")]
    public int ProjectId { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "請選擇承攬商")]
    public int ContractorId { get; set; }

    [Required(ErrorMessage = "請選擇辦理組別")]
    public string GroupId { get; set; } = "";

    [DataType(DataType.Date)] public DateTime? ImproveDeadline { get; set; }

    public List<Pr3ItemInput> Pros { get; set; } = new();
    public List<Pr3ItemInput> Cons { get; set; } = new();
    public List<Pr3ItemInput> Recommends { get; set; } = new();

    public IReadOnlyList<Pr3Item> ToItems()
    {
        var items = new List<Pr3Item>();
        void Add(IEnumerable<Pr3ItemInput> src, Pr3ItemKind kind)
        {
            var ord = 0;
            foreach (var i in src.Where(x => !string.IsNullOrWhiteSpace(x.Description)))
                items.Add(new Pr3Item
                {
                    Kind = kind,
                    Ordinal = ++ord,
                    Description = i.Description!.Trim(),
                    HazardId = kind == Pr3ItemKind.Con ? i.HazardId : null,
                    DeficiencyTypeId = kind == Pr3ItemKind.Con ? i.DeficiencyTypeId : null,
                    DeficiencyItemId = kind == Pr3ItemKind.Con ? i.DeficiencyItemId : null,
                    HazardLabel = kind == Pr3ItemKind.Con ? i.HazardLabel : null,
                });
        }
        Add(Pros, Pr3ItemKind.Pro);
        Add(Cons, Pr3ItemKind.Con);
        Add(Recommends, Pr3ItemKind.Recommend);
        return items;
    }
}

public sealed class Pr3FormViewModel
{
    public required Pr3Form Form { get; init; }
    public EngineeringProject? Project { get; init; }
    public Contractor? Contractor { get; init; }
    public Employee? Inspector { get; init; }
    public required IReadOnlyList<WorkflowHistoryRow> History { get; init; }
    public bool CanAct { get; init; }
    public required IReadOnlyList<Pr3Action> Allowed { get; init; }

    public IEnumerable<Pr3Item> Pros => Form.Items.Where(i => i.Kind == Pr3ItemKind.Pro);
    public IEnumerable<Pr3Item> Cons => Form.Items.Where(i => i.Kind == Pr3ItemKind.Con);
    public IEnumerable<Pr3Item> Recommends => Form.Items.Where(i => i.Kind == Pr3ItemKind.Recommend);
}

public sealed class Pr3ActRequest
{
    public long FormId { get; set; }
    public Pr3Action Action { get; set; }
    public string? Comment { get; set; }
    public string? AssigneeId { get; set; }
    public bool IsJump { get; set; }
}

/// <summary>Pr3 專屬的流程顯示字串（階段/步驟/動作）。位置名稱共用 WorkflowDisplay.PositionName。</summary>
public static class Pr3Display
{
    public static string StageName(Pr3Stage s) => s switch
    {
        Pr3Stage.Filing => "起單 / 巡查人確認",
        Pr3Stage.HandlingReview => "經辦組簽註",
        Pr3Stage.SafetyReview => "工安組查證/追蹤",
        Pr3Stage.Closed => "結案",
        _ => s.ToString(),
    };

    public static string StepName(Pr3Step s, FormStatus status) => status switch
    {
        FormStatus.Closed => "已結案",
        FormStatus.Voided => "已作廢",
        _ => $"{StageName(s.Stage)}（{WorkflowDisplay.PositionName(s.Position)}{(s.Transit == Transit.Submitted ? "·待處理" : "·處理中")}）",
    };

    public static string ActionName(Pr3Action a) => a switch
    {
        Pr3Action.Submit => "送出 / 上呈",
        Pr3Action.AssignChief => "派單（課長）",
        Pr3Action.AssignOfficer => "派單（經辦）",
        Pr3Action.Withdraw => "抽回",
        Pr3Action.ReturnToOfficer => "退回經辦",
        Pr3Action.ReturnToChief => "退回課長",
        Pr3Action.ReturnToDesk => "退回登記桌",
        Pr3Action.ReturnToFiler => "退回填單人",
        Pr3Action.ReturnToHandling => "重回經辦組",
        Pr3Action.ContinueTracking => "持續追蹤",
        Pr3Action.Close => "結案",
        _ => a.ToString(),
    };
}
