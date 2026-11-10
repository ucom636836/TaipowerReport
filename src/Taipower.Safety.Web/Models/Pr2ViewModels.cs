using System.ComponentModel.DataAnnotations;
using Taipower.Safety.Core.Forms.Pr2;
using Taipower.Safety.Core.Forms.Workflow;
using Taipower.Safety.Core.Org;
using Taipower.Safety.Core.Reference;

namespace Taipower.Safety.Web.Models;

public sealed class Pr2ItemInput
{
    public string? Description { get; set; }
    public int? HazardId { get; set; }
    public int? DeficiencyTypeId { get; set; }
    public int? DeficiencyItemId { get; set; }
    public string? HazardLabel { get; set; }
}

public sealed class Pr2CreateViewModel
{
    [Required(ErrorMessage = "請選擇查核類別")]
    public string CheckType { get; set; } = Pr2CheckType.Safety;

    [DataType(DataType.Date)]
    public DateTime CheckDate { get; set; } = DateTime.Today;

    [Range(1, int.MaxValue, ErrorMessage = "請選擇工程")]
    public int ProjectId { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "請選擇承攬商")]
    public int ContractorId { get; set; }

    [Required(ErrorMessage = "請選擇查核領隊")]
    public string LeaderId { get; set; } = "";

    public string? TeamMembers { get; set; }
    public string? ImproveGroupId { get; set; }

    public List<Pr2ItemInput> Pros { get; set; } = new();
    public List<Pr2ItemInput> Cons { get; set; } = new();
    public List<Pr2ItemInput> Recommends { get; set; } = new();

    public IReadOnlyList<Pr2Item> ToItems()
    {
        var items = new List<Pr2Item>();
        void Add(IEnumerable<Pr2ItemInput> src, Pr2ItemKind kind)
        {
            var ord = 0;
            foreach (var i in src.Where(x => !string.IsNullOrWhiteSpace(x.Description)))
                items.Add(new Pr2Item
                {
                    Kind = kind,
                    Ordinal = ++ord,
                    Description = i.Description!.Trim(),
                    HazardId = kind == Pr2ItemKind.Con ? i.HazardId : null,
                    DeficiencyTypeId = kind == Pr2ItemKind.Con ? i.DeficiencyTypeId : null,
                    DeficiencyItemId = kind == Pr2ItemKind.Con ? i.DeficiencyItemId : null,
                    HazardLabel = kind == Pr2ItemKind.Con ? i.HazardLabel : null,
                });
        }
        Add(Pros, Pr2ItemKind.Pro);
        Add(Cons, Pr2ItemKind.Con);
        Add(Recommends, Pr2ItemKind.Recommend);
        return items;
    }
}

public sealed class Pr2FormViewModel
{
    public required Pr2Form Form { get; init; }
    public EngineeringProject? Project { get; init; }
    public Contractor? Contractor { get; init; }
    public required IReadOnlyList<WorkflowHistoryRow> History { get; init; }
    public bool CanAct { get; init; }
    public required IReadOnlyList<WorkflowAction> Allowed { get; init; }
    public required IReadOnlyList<OrgGroup> Groups { get; init; }

    public IEnumerable<Pr2Item> Pros => Form.Items.Where(i => i.Kind == Pr2ItemKind.Pro);
    public IEnumerable<Pr2Item> Cons => Form.Items.Where(i => i.Kind == Pr2ItemKind.Con);
    public IEnumerable<Pr2Item> Recommends => Form.Items.Where(i => i.Kind == Pr2ItemKind.Recommend);
}

public sealed class Pr2ActRequest
{
    public long FormId { get; set; }
    public WorkflowAction Action { get; set; }
    public string? Comment { get; set; }
    public string? AssigneeId { get; set; }
    public string? InformGroupId { get; set; }
    [DataType(DataType.Date)] public DateTime? ImproveDeadline { get; set; }
    public bool IsJump { get; set; }
    public bool IsProxy { get; set; }
    public bool AgreeClose { get; set; } = true;
}
