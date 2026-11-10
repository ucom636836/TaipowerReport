namespace Taipower.Safety.Core.Reference;

public sealed class Contractor
{
    public int ContractorId { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public List<ContractorContact> Contacts { get; set; } = new();
}

public sealed class ContractorContact
{
    public int ContactId { get; set; }
    public int ContractorId { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
}

public sealed class EngineeringProject
{
    public int ProjectId { get; set; }
    public string ProjectNumber { get; set; } = "";
    public string Name { get; set; } = "";
    public string? UnitPeriod { get; set; }
    public int? PenaltyVersionId { get; set; }
    public bool IsActive { get; set; } = true;
    public List<int> ContractorIds { get; set; } = new();
}

public sealed record Hazard(int HazardId, string Name);
public sealed record DeficiencyType(int DeficiencyTypeId, int HazardId, string Name);
public sealed record DeficiencyItem(int DeficiencyItemId, int DeficiencyTypeId, string Name);
public sealed record PenaltyVersion(int PenaltyVersionId, string Name);

public interface IReferenceRepository
{
    Task<IReadOnlyList<EngineeringProject>> GetProjectsAsync(bool activeOnly = true, CancellationToken ct = default);
    Task<EngineeringProject?> GetProjectAsync(int projectId, CancellationToken ct = default);
    Task<int> SaveProjectAsync(EngineeringProject project, CancellationToken ct = default);

    Task<IReadOnlyList<Contractor>> GetContractorsAsync(int? projectId = null, bool activeOnly = true, CancellationToken ct = default);
    Task<Contractor?> GetContractorAsync(int contractorId, CancellationToken ct = default);
    Task<int> SaveContractorAsync(Contractor contractor, CancellationToken ct = default);

    Task<IReadOnlyList<Hazard>> GetHazardsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DeficiencyType>> GetDeficiencyTypesAsync(int? hazardId = null, CancellationToken ct = default);
    Task<IReadOnlyList<DeficiencyItem>> GetDeficiencyItemsAsync(int? typeId = null, CancellationToken ct = default);
    Task<int> SaveHazardAsync(Hazard hazard, CancellationToken ct = default);
    Task<int> SaveDeficiencyTypeAsync(DeficiencyType type, CancellationToken ct = default);
    Task<int> SaveDeficiencyItemAsync(DeficiencyItem item, CancellationToken ct = default);

    Task<IReadOnlyList<PenaltyVersion>> GetPenaltyVersionsAsync(CancellationToken ct = default);
}
