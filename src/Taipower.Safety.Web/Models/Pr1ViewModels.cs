using System.ComponentModel.DataAnnotations;
using Taipower.Safety.Core.Forms.Pr1;
using Taipower.Safety.Core.Org;
using Taipower.Safety.Core.Reference;

using Taipower.Safety.Core.Forms.Workflow;
namespace Taipower.Safety.Web.Models;

public sealed class Pr1ItemInput
{
    public string? Description { get; set; }
    public int? HazardId { get; set; }
    public int? DeficiencyTypeId { get; set; }
    public int? DeficiencyItemId { get; set; }
    public string? HazardLabel { get; set; }
}

public sealed class Pr1CreateViewModel
{
    [Required(ErrorMessage = "請選擇巡查類別")]
    public string PatrolType { get; set; } = Core.Forms.Pr1.PatrolType.Safety;

    [DataType(DataType.Date)]
    public DateTime PatrolDate { get; set; } = DateTime.Today;

    [Range(1, int.MaxValue, ErrorMessage = "請選擇工程")]
    public int ProjectId { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "請選擇承攬商")]
    public int ContractorId { get; set; }

    [Required(ErrorMessage = "請選擇巡查領隊")]
    public string LeaderId { get; set; } = "";

    public string? TeamMembers { get; set; }
    public string? ImproveGroupId { get; set; }

    public List<Pr1ItemInput> Pros { get; set; } = new();
    public List<Pr1ItemInput> Cons { get; set; } = new();
    public List<Pr1ItemInput> Recommends { get; set; } = new();

    public IReadOnlyList<Pr1Item> ToItems()
    {
        var items = new List<Pr1Item>();
        void Add(IEnumerable<Pr1ItemInput> src, ItemKind kind)
        {
            var ord = 0;
            foreach (var i in src.Where(x => !string.IsNullOrWhiteSpace(x.Description)))
                items.Add(new Pr1Item
                {
                    Kind = kind,
                    Ordinal = ++ord,
                    Description = i.Description!.Trim(),
                    HazardId = kind == ItemKind.Con ? i.HazardId : null,
                    DeficiencyTypeId = kind == ItemKind.Con ? i.DeficiencyTypeId : null,
                    DeficiencyItemId = kind == ItemKind.Con ? i.DeficiencyItemId : null,
                    HazardLabel = kind == ItemKind.Con ? i.HazardLabel : null,
                });
        }
        Add(Pros, ItemKind.Pro);
        Add(Cons, ItemKind.Con);
        Add(Recommends, ItemKind.Recommend);
        return items;
    }
}

public sealed class Pr1FormViewModel
{
    public required Pr1Form Form { get; init; }
    public EngineeringProject? Project { get; init; }
    public Contractor? Contractor { get; init; }
    public required IReadOnlyList<WorkflowHistoryRow> History { get; init; }
    public bool CanAct { get; init; }
    public required IReadOnlyList<WorkflowAction> Allowed { get; init; }
    public required IReadOnlyList<OrgGroup> Groups { get; init; }

    public IEnumerable<Pr1Item> Pros => Form.Items.Where(i => i.Kind == ItemKind.Pro);
    public IEnumerable<Pr1Item> Cons => Form.Items.Where(i => i.Kind == ItemKind.Con);
    public IEnumerable<Pr1Item> Recommends => Form.Items.Where(i => i.Kind == ItemKind.Recommend);
}

public sealed class Pr1ItemResultInput
{
    public long ItemId { get; set; }
    public bool? Improved { get; set; }
    public string? NotImprovedReason { get; set; }
    [DataType(DataType.Date)] public DateTime? ImprovedDate { get; set; }
    public bool? Verified { get; set; }
    public string? VerifyReason { get; set; }
}

public sealed class Pr1ActRequest
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
