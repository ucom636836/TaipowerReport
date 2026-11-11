using Taipower.Safety.Core.Common;
using Taipower.Safety.Core.Forms.Pr3;
using Taipower.Safety.Core.Forms.Workflow;
using Taipower.Safety.Core.Org;
using Taipower.Safety.Core.Reference;
using Xunit;

namespace Taipower.Safety.Tests;

/// <summary>Pr3Service 編排測試（以 fake 取代持久化／指派解析／郵件）。</summary>
public class Pr3ServiceTests
{
    private static readonly DateTime Today = new(2026, 11, 12);

    private static CurrentUser User(string id, string name = "測試員") =>
        new(id, name, "69", null, 5, 3, new HashSet<int>());

    private static Employee Emp(string id, string name = "某人") =>
        new() { EmployeeId = id, Name = name, Email = $"{id}@test.local" };

    private static Pr3CreateRequest ValidCreate(string inspectorId = "INSP", IReadOnlyList<Pr3Item>? items = null) => new(
        InspectorId: inspectorId, InspectDate: Today, InspectTime: "14:00", Attendants: null, Location: "工地", Target: "10 人",
        ProjectId: 1, ContractorId: 2, GroupId: "61", ImproveDeadline: Today.AddDays(7),
        Items: items ?? [new Pr3Item { Kind = Pr3ItemKind.Con, Description = "未戴安全帽" }]);

    private static (Pr3Service svc, FakePr3Repo repo, FakeResolver resolver, FakeEmail email) Build()
    {
        var repo = new FakePr3Repo();
        var resolver = new FakeResolver();
        var email = new FakeEmail();
        var composer = new Pr3EmailComposer(new FakeRefs()) { PublicBaseUrl = () => "http://test/" };
        return (new Pr3Service(repo, resolver, email, composer), repo, resolver, email);
    }

    [Fact]
    public async Task Create_by_inspector_routes_to_handling_desk_and_notifies()
    {
        var (svc, repo, resolver, email) = Build();
        resolver.Default = Emp("DESK", "辦理組登記桌");
        // 填單人即巡查人
        var r = await svc.CreateAsync(ValidCreate(inspectorId: "MAKER"), User("MAKER"));

        Assert.True(r.Ok, r.Error);
        var form = repo.Get(r.FormId);
        Assert.Equal(new Pr3Step(Pr3Stage.HandlingReview, Position.Assign, Transit.Submitted), form.Step);
        Assert.Equal("DESK", form.PendingRecipientId);
        Assert.Single(email.Sent);
    }

    [Fact]
    public async Task Create_by_other_filer_routes_to_inspector_first()
    {
        var (svc, repo, resolver, _) = Build();
        resolver.Inspector = Emp("INSP", "巡查人");
        var r = await svc.CreateAsync(ValidCreate(inspectorId: "INSP"), User("MAKER"));

        Assert.True(r.Ok, r.Error);
        var form = repo.Get(r.FormId);
        Assert.Equal(new Pr3Step(Pr3Stage.Filing, Position.Inspector, Transit.Submitted), form.Step);
        Assert.Equal("INSP", form.PendingRecipientId);
    }

    [Fact]
    public async Task Create_rejects_empty_items_and_missing_group()
    {
        var (svc, _, resolver, _) = Build();
        resolver.Default = Emp("DESK");
        Assert.False((await svc.CreateAsync(ValidCreate(items: []), User("MAKER"))).Ok);
        Assert.False((await svc.CreateAsync(ValidCreate() with { GroupId = "" }, User("MAKER"))).Ok);
    }

    [Fact]
    public async Task Act_rejects_non_holder()
    {
        var (svc, repo, _, _) = Build();
        repo.Seed(FormAt(new Pr3Step(Pr3Stage.HandlingReview, Position.Assign, Transit.Submitted), "DESK", "DESK"));
        var r = await svc.ActAsync(new Pr3ActionRequest(1, Pr3Action.AssignOfficer, null, SpecificPersonId: "OFF"), User("OUTSIDER"));
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task Act_assign_officer_records_and_advances()
    {
        var (svc, repo, _, _) = Build();
        repo.Seed(FormAt(new Pr3Step(Pr3Stage.HandlingReview, Position.Assign, Transit.Submitted), "DESK", "DESK"));
        var r = await svc.ActAsync(new Pr3ActionRequest(1, Pr3Action.AssignOfficer, null, SpecificPersonId: "OFF1"), User("DESK"));

        Assert.True(r.Ok, r.Error);
        var form = repo.Get(1);
        Assert.Equal(new Pr3Step(Pr3Stage.HandlingReview, Position.Officer, Transit.Submitted), form.Step);
        Assert.Equal("OFF1", form.HandlingOfficerId);
        Assert.Equal("OFF1", form.PendingRecipientId);
    }

    [Fact]
    public async Task Act_handling_manager_submit_advances_to_safety()
    {
        var (svc, repo, resolver, _) = Build();
        resolver.Default = Emp("SDESK", "工安登記桌");
        repo.Seed(FormAt(new Pr3Step(Pr3Stage.HandlingReview, Position.Manager, Transit.Working), "MGR", "MGR"));
        var r = await svc.ActAsync(new Pr3ActionRequest(1, Pr3Action.Submit, "核定"), User("MGR"));

        Assert.True(r.Ok, r.Error);
        var form = repo.Get(1);
        Assert.Equal(new Pr3Step(Pr3Stage.SafetyReview, Position.Assign, Transit.Submitted), form.Step);
        Assert.Equal("MGR", form.HandlingManagerId);
    }

    [Fact]
    public async Task Act_safety_manager_close_closes_without_email()
    {
        var (svc, repo, resolver, email) = Build();
        resolver.Default = Emp("SMGR");
        repo.Seed(FormAt(new Pr3Step(Pr3Stage.SafetyReview, Position.Manager, Transit.Working), "SMGR", "SMGR"));
        var r = await svc.ActAsync(new Pr3ActionRequest(1, Pr3Action.Close, "結案"), User("SMGR"));

        Assert.True(r.Ok, r.Error);
        Assert.Equal(FormStatus.Closed, repo.Get(1).Status);
        Assert.Empty(email.Sent);
    }

    [Fact]
    public async Task Act_safety_manager_continue_tracking_loops_and_increments_archive()
    {
        var (svc, repo, resolver, _) = Build();
        resolver.Default = Emp("HDESK", "辦理組登記桌");
        repo.Seed(FormAt(new Pr3Step(Pr3Stage.SafetyReview, Position.Manager, Transit.Working), "SMGR", "SMGR"));
        var r = await svc.ActAsync(new Pr3ActionRequest(1, Pr3Action.ContinueTracking, "持續追蹤"), User("SMGR"));

        Assert.True(r.Ok, r.Error);
        var form = repo.Get(1);
        Assert.Equal(new Pr3Step(Pr3Stage.HandlingReview, Position.Assign, Transit.Submitted), form.Step);
        Assert.Equal(FormStatus.InProgress, form.Status);
        Assert.Equal(1, form.Archive);
    }

    [Fact]
    public async Task Open_takes_over_as_holder()
    {
        var (svc, repo, _, _) = Build();
        repo.Seed(FormAt(new Pr3Step(Pr3Stage.HandlingReview, Position.Officer, Transit.Submitted), "DESK", "OFF1"));
        await svc.OpenAsync(1, User("OFF1"));
        var form = repo.Get(1);
        Assert.Equal("OFF1", form.CurrentHolderId);
        Assert.Equal(Transit.Working, form.Step.Transit);
    }

    private static Pr3Form FormAt(Pr3Step step, string holder, string pending) => new()
    {
        FormId = 1,
        FormNumber = "SR-1151112-01-0",
        Step = step,
        Status = FormStatus.InProgress,
        CurrentHolderId = holder,
        PendingRecipientId = pending,
        InspectorId = "INSP",
        InspectDate = Today,
        ProjectId = 1,
        ContractorId = 2,
        GroupId = "61",
        CreatedBy = "MAKER",
        Items = [new Pr3Item { ItemId = 10, FormId = 1, Kind = Pr3ItemKind.Con, Ordinal = 1, Description = "未戴安全帽" }],
    };

    // ===== fakes =====

    private sealed class FakePr3Repo : IPr3Repository
    {
        private readonly Dictionary<long, Pr3Form> _forms = new();
        private long _seq;
        private int _num;

        public Pr3Form Get(long id) => _forms[id];
        public void Seed(Pr3Form form) => _forms[form.FormId] = form;

        public Task<string> NextFormNumberAsync(DateTime date, CancellationToken ct = default)
            => Task.FromResult($"SR-{date:yyMMdd}-{++_num:00}-0");

        public Task<long> CreateAsync(Pr3Form form, string actorTitle, CancellationToken ct = default)
        {
            form.FormId = ++_seq;
            _forms[form.FormId] = form;
            return Task.FromResult(form.FormId);
        }

        public Task<Pr3Form?> GetAsync(long formId, CancellationToken ct = default)
            => Task.FromResult(_forms.TryGetValue(formId, out var f) ? f : null);

        public Task<Pr3Form?> GetByNumberAsync(string formNumber, CancellationToken ct = default)
            => Task.FromResult<Pr3Form?>(null);

        public Task ApplyTransitionAsync(Pr3Form form, Pr3Transition transition, string actorId, string actorTitle,
            string? comment, CancellationToken ct = default)
        {
            form.Step = transition.To;
            form.Status = transition.Status;
            if (transition.IncrementRetimes) form.Retimes++;
            if (transition.IncrementArchive) form.Archive++;
            _forms[form.FormId] = form;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Pr3InboxRow>> GetInboxAsync(string employeeId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Pr3InboxRow>>([]);
        public Task<IReadOnlyList<Pr3InboxRow>> SearchAsync(Pr3SearchQuery query, string employeeId, bool allScopes, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Pr3InboxRow>>([]);
        public Task<IReadOnlyList<WorkflowHistoryRow>> GetHistoryAsync(long formId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkflowHistoryRow>>([]);
    }

    private sealed class FakeResolver : IPr3AssigneeResolver
    {
        public Employee? Default { get; set; } = Emp("NEXT", "下一關");
        public Employee? Inspector { get; set; }

        public Task<Employee?> ResolveAsync(Pr3Form form, Pr3AssigneeRule rule, string actorId, string? specificPersonId, CancellationToken ct = default)
            => Task.FromResult(rule switch
            {
                Pr3AssigneeRule.None => null,
                Pr3AssigneeRule.Inspector => Inspector ?? Emp(form.InspectorId),
                Pr3AssigneeRule.SpecificPerson => specificPersonId is null ? null : Emp(specificPersonId),
                Pr3AssigneeRule.Self => form.CurrentHolderId is null ? null : Emp(form.CurrentHolderId),
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
