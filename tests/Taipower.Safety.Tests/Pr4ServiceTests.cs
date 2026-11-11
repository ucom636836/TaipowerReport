using Taipower.Safety.Core.Common;
using Taipower.Safety.Core.Forms.Pr4;
using Taipower.Safety.Core.Forms.Workflow;
using Taipower.Safety.Core.Org;
using Taipower.Safety.Core.Reference;
using Xunit;

namespace Taipower.Safety.Tests;

/// <summary>Pr4Service 編排測試（以 fake 取代持久化／指派解析／郵件）。</summary>
public class Pr4ServiceTests
{
    private static readonly DateTime Today = new(2026, 11, 12);

    private static CurrentUser User(string id, string name = "檢驗員") =>
        new(id, name, "69", null, 5, 3, new HashSet<int>());

    private static Employee Emp(string id, string name = "某人") =>
        new() { EmployeeId = id, Name = name, Email = $"{id}@test.local" };

    private static Pr4CreateRequest ValidCreate(bool need = true, IReadOnlyList<Pr4Item>? items = null) => new(
        SpotCheckDate: Today, ProjectId: 1, ContractorId: 2, Location: "工地", ArriveTime: "09:00", LeaveTime: "11:00",
        GroupId: "61", ImproveDeadline: Today.AddDays(7), NeedImprove: need,
        Items: items ?? [new Pr4Item { Kind = Pr4ItemKind.Con, Description = "未戴安全帽" }]);

    private static (Pr4Service svc, FakePr4Repo repo, FakeResolver resolver, FakeEmail email) Build()
    {
        var repo = new FakePr4Repo();
        var resolver = new FakeResolver();
        var email = new FakeEmail();
        var composer = new Pr4EmailComposer(new FakeRefs()) { PublicBaseUrl = () => "http://test/" };
        return (new Pr4Service(repo, resolver, email, composer), repo, resolver, email);
    }

    [Fact]
    public async Task Create_routes_to_group_chief_and_notifies()
    {
        var (svc, repo, resolver, email) = Build();
        resolver.Default = Emp("CHIEF", "課長");
        var r = await svc.CreateAsync(ValidCreate(), User("INSP"));

        Assert.True(r.Ok, r.Error);
        var form = repo.Get(r.FormId);
        Assert.Equal(new Pr4Step(Pr4Stage.Inspection, Position.Chief, Transit.Submitted), form.Step);
        Assert.Equal("INSP", form.CreatedBy);
        Assert.Equal("CHIEF", form.PendingRecipientId);
        Assert.Single(email.Sent);
    }

    [Fact]
    public async Task Create_rejects_empty_items_and_missing_group()
    {
        var (svc, _, resolver, _) = Build();
        resolver.Default = Emp("CHIEF");
        Assert.False((await svc.CreateAsync(ValidCreate(items: []), User("INSP"))).Ok);
        Assert.False((await svc.CreateAsync(ValidCreate() with { GroupId = "" }, User("INSP"))).Ok);
    }

    [Fact]
    public async Task Act_rejects_non_holder()
    {
        var (svc, repo, _, _) = Build();
        repo.Seed(FormAt(new Pr4Step(Pr4Stage.Inspection, Position.Chief, Transit.Submitted), "CHIEF", "CHIEF", need: true));
        var r = await svc.ActAsync(new Pr4ActionRequest(1, Pr4Action.Submit, null), User("OUTSIDER"));
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task Act_inspection_chief_submit_records_chief_and_advances()
    {
        var (svc, repo, resolver, _) = Build();
        resolver.Default = Emp("MGR", "經理");
        repo.Seed(FormAt(new Pr4Step(Pr4Stage.Inspection, Position.Chief, Transit.Working), "CHIEF", "CHIEF", need: true));
        var r = await svc.ActAsync(new Pr4ActionRequest(1, Pr4Action.Submit, "核"), User("CHIEF"));

        Assert.True(r.Ok, r.Error);
        var form = repo.Get(1);
        Assert.Equal(Position.Manager, form.Step.Position);
        Assert.Equal("CHIEF", form.InspectionChiefId);
    }

    [Fact]
    public async Task Act_inspection_manager_submit_need_improve_goes_to_handling()
    {
        var (svc, repo, resolver, _) = Build();
        resolver.Default = Emp("HDESK", "經辦組登記桌");
        repo.Seed(FormAt(new Pr4Step(Pr4Stage.Inspection, Position.Manager, Transit.Working), "MGR", "MGR", need: true));
        var r = await svc.ActAsync(new Pr4ActionRequest(1, Pr4Action.Submit, "核定"), User("MGR"));

        Assert.True(r.Ok, r.Error);
        var form = repo.Get(1);
        Assert.Equal(new Pr4Step(Pr4Stage.Handling, Position.Assign, Transit.Submitted), form.Step);
        Assert.Equal("MGR", form.InspectionManagerId);
    }

    [Fact]
    public async Task Act_inspection_manager_submit_no_improve_closes_without_email()
    {
        var (svc, repo, resolver, email) = Build();
        resolver.Default = Emp("MGR");
        repo.Seed(FormAt(new Pr4Step(Pr4Stage.Inspection, Position.Manager, Transit.Working), "MGR", "MGR", need: false));
        var r = await svc.ActAsync(new Pr4ActionRequest(1, Pr4Action.Submit, "免改善"), User("MGR"));

        Assert.True(r.Ok, r.Error);
        Assert.Equal(FormStatus.Closed, repo.Get(1).Status);
        Assert.Empty(email.Sent);
    }

    [Fact]
    public async Task Act_handling_manager_submit_closes()
    {
        var (svc, repo, resolver, _) = Build();
        resolver.Default = Emp("HMGR");
        repo.Seed(FormAt(new Pr4Step(Pr4Stage.Handling, Position.Manager, Transit.Working), "HMGR", "HMGR", need: true));
        var r = await svc.ActAsync(new Pr4ActionRequest(1, Pr4Action.Submit, "結案"), User("HMGR"));

        Assert.True(r.Ok, r.Error);
        Assert.Equal(FormStatus.Closed, repo.Get(1).Status);
    }

    [Fact]
    public async Task Open_takes_over_as_holder()
    {
        var (svc, repo, _, _) = Build();
        repo.Seed(FormAt(new Pr4Step(Pr4Stage.Handling, Position.Officer, Transit.Submitted), "DESK", "OFF1", need: true));
        await svc.OpenAsync(1, User("OFF1"));
        var form = repo.Get(1);
        Assert.Equal("OFF1", form.CurrentHolderId);
        Assert.Equal(Transit.Working, form.Step.Transit);
    }

    private static Pr4Form FormAt(Pr4Step step, string holder, string pending, bool need) => new()
    {
        FormId = 1,
        FormNumber = "SC-1151112-01-0",
        Step = step,
        Status = FormStatus.InProgress,
        CurrentHolderId = holder,
        PendingRecipientId = pending,
        SpotCheckDate = Today,
        ProjectId = 1,
        ContractorId = 2,
        GroupId = "61",
        NeedImprove = need,
        CreatedBy = "INSP",
        Items = [new Pr4Item { ItemId = 10, FormId = 1, Kind = Pr4ItemKind.Con, Ordinal = 1, Description = "未戴安全帽" }],
    };

    // ===== fakes =====

    private sealed class FakePr4Repo : IPr4Repository
    {
        private readonly Dictionary<long, Pr4Form> _forms = new();
        private long _seq;
        private int _num;

        public Pr4Form Get(long id) => _forms[id];
        public void Seed(Pr4Form form) => _forms[form.FormId] = form;

        public Task<string> NextFormNumberAsync(DateTime date, CancellationToken ct = default)
            => Task.FromResult($"SC-{date:yyMMdd}-{++_num:00}-0");

        public Task<long> CreateAsync(Pr4Form form, string actorTitle, CancellationToken ct = default)
        {
            form.FormId = ++_seq;
            _forms[form.FormId] = form;
            return Task.FromResult(form.FormId);
        }

        public Task<Pr4Form?> GetAsync(long formId, CancellationToken ct = default)
            => Task.FromResult(_forms.TryGetValue(formId, out var f) ? f : null);

        public Task<Pr4Form?> GetByNumberAsync(string formNumber, CancellationToken ct = default)
            => Task.FromResult<Pr4Form?>(null);

        public Task ApplyTransitionAsync(Pr4Form form, Pr4Transition transition, string actorId, string actorTitle,
            string? comment, CancellationToken ct = default)
        {
            form.Step = transition.To;
            form.Status = transition.Status;
            if (transition.IncrementRetimes) form.Retimes++;
            _forms[form.FormId] = form;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Pr4InboxRow>> GetInboxAsync(string employeeId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Pr4InboxRow>>([]);
        public Task<IReadOnlyList<Pr4InboxRow>> SearchAsync(Pr4SearchQuery query, string employeeId, bool allScopes, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Pr4InboxRow>>([]);
        public Task<IReadOnlyList<WorkflowHistoryRow>> GetHistoryAsync(long formId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkflowHistoryRow>>([]);
    }

    private sealed class FakeResolver : IPr4AssigneeResolver
    {
        public Employee? Default { get; set; } = Emp("NEXT", "下一關");

        public Task<Employee?> ResolveAsync(Pr4Form form, Pr4AssigneeRule rule, string actorId, string? specificPersonId, CancellationToken ct = default)
            => Task.FromResult(rule switch
            {
                Pr4AssigneeRule.None => null,
                Pr4AssigneeRule.SpecificPerson => specificPersonId is null ? null : Emp(specificPersonId),
                Pr4AssigneeRule.Creator => Emp(form.CreatedBy),
                Pr4AssigneeRule.Self => form.CurrentHolderId is null ? null : Emp(form.CurrentHolderId),
                _ => Default,
            });
    }

    private sealed class FakeEmail : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = new();
        public Task SendAsync(EmailMessage message, CancellationToken ct = default) { Sent.Add(message); return Task.CompletedTask; }
    }

    private sealed class FakeRefs : IReferenceRepository
    {
        public Task<EngineeringProject?> GetProjectAsync(int projectId, CancellationToken ct = default) => Task.FromResult<EngineeringProject?>(null);
        public Task<Contractor?> GetContractorAsync(int contractorId, CancellationToken ct = default) => Task.FromResult<Contractor?>(null);
        public Task<IReadOnlyList<EngineeringProject>> GetProjectsAsync(bool activeOnly = true, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<EngineeringProject>>([]);
        public Task<int> SaveProjectAsync(EngineeringProject project, CancellationToken ct = default) => Task.FromResult(0);
        public Task<IReadOnlyList<Contractor>> GetContractorsAsync(int? projectId = null, bool activeOnly = true, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Contractor>>([]);
        public Task<int> SaveContractorAsync(Contractor contractor, CancellationToken ct = default) => Task.FromResult(0);
        public Task<IReadOnlyList<Hazard>> GetHazardsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Hazard>>([]);
        public Task<IReadOnlyList<DeficiencyType>> GetDeficiencyTypesAsync(int? hazardId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DeficiencyType>>([]);
        public Task<IReadOnlyList<DeficiencyItem>> GetDeficiencyItemsAsync(int? typeId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DeficiencyItem>>([]);
        public Task<int> SaveHazardAsync(Hazard hazard, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> SaveDeficiencyTypeAsync(DeficiencyType type, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> SaveDeficiencyItemAsync(DeficiencyItem item, CancellationToken ct = default) => Task.FromResult(0);
        public Task<IReadOnlyList<PenaltyVersion>> GetPenaltyVersionsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PenaltyVersion>>([]);
    }
}
