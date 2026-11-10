using Taipower.Safety.Core.Common;
using Taipower.Safety.Core.Forms.Pr2;
using Taipower.Safety.Core.Forms.Workflow;
using Taipower.Safety.Core.Org;
using Taipower.Safety.Core.Reference;
using Xunit;

namespace Taipower.Safety.Tests;

/// <summary>
/// Pr2Service 編排測試。重點在驗證 Pr2 確實重用共用的 SafetyWorkflow 狀態機
/// 與 IAssigneeResolver（C1→C6 同一條鏈），而非自帶一份流程邏輯。
/// </summary>
public class Pr2ServiceTests
{
    private static readonly DateTime Today = new(2026, 11, 11);

    private static CurrentUser User(string id, string name = "測試員") =>
        new(id, name, "69", null, 5, 3, new HashSet<int>());

    private static Employee Emp(string id, string name = "某人") =>
        new() { EmployeeId = id, Name = name, Email = $"{id}@test.local" };

    private static Pr2CreateRequest ValidCreate(IReadOnlyList<Pr2Item>? items = null) => new(
        CheckType: Pr2CheckType.Safety, CheckDate: Today, ProjectId: 1, ContractorId: 2,
        LeaderId: "LEAD", TeamMembers: null, ImproveGroupId: null,
        Items: items ?? [new Pr2Item { Kind = Pr2ItemKind.Con, Description = "未戴安全帽" }]);

    private static (Pr2Service svc, FakePr2Repo repo, FakeResolver resolver, FakeEmail email) Build()
    {
        var repo = new FakePr2Repo();
        var resolver = new FakeResolver();
        var email = new FakeEmail();
        var composer = new Pr2EmailComposer(new FakeRefs()) { PublicBaseUrl = () => "http://test/" };
        return (new Pr2Service(repo, resolver, email, composer), repo, resolver, email);
    }

    [Fact]
    public async Task Create_valid_stores_form_at_desk_and_notifies()
    {
        var (svc, repo, resolver, email) = Build();
        resolver.Default = Emp("DESK", "登記桌");

        var r = await svc.CreateAsync(ValidCreate(), User("MAKER"));

        Assert.True(r.Ok, r.Error);
        var form = repo.Get(r.FormId);
        Assert.Equal(new WorkflowStep(Stage.SafetyReview, Position.Assign, Transit.Submitted), form.Step);
        Assert.Equal("MAKER", form.CreatedBy);
        Assert.Equal("DESK", form.PendingRecipientId);
        Assert.False(string.IsNullOrEmpty(form.FormNumber));
        Assert.Single(email.Sent);
    }

    [Fact]
    public async Task Create_rejects_empty_items_and_unknown_type()
    {
        var (svc, _, resolver, _) = Build();
        resolver.Default = Emp("DESK");
        Assert.False((await svc.CreateAsync(ValidCreate(items: []), User("MAKER"))).Ok);
        Assert.False((await svc.CreateAsync(ValidCreate() with { CheckType = "ZZ" }, User("MAKER"))).Ok);
    }

    [Fact]
    public async Task Create_fails_when_no_safety_desk_found()
    {
        var (svc, _, resolver, _) = Build();
        resolver.Default = null;
        var r = await svc.CreateAsync(ValidCreate(), User("MAKER"));
        Assert.False(r.Ok);
        Assert.Contains("登記桌", r.Error);
    }

    [Fact]
    public async Task Act_rejects_user_who_is_not_holder_or_recipient()
    {
        var (svc, repo, _, _) = Build();
        repo.Seed(FormAt(new WorkflowStep(Stage.SafetyReview, Position.Assign, Transit.Submitted), "DESK", "DESK"));
        var r = await svc.ActAsync(new Pr2ActionRequest(1, WorkflowAction.AssignOfficer, null, SpecificPersonId: "OFF"), User("OUTSIDER"));
        Assert.False(r.Ok);
        Assert.Contains("處理人", r.Error);
    }

    [Fact]
    public async Task Act_assign_officer_records_officer_and_advances_step()
    {
        var (svc, repo, _, _) = Build();
        repo.Seed(FormAt(new WorkflowStep(Stage.SafetyReview, Position.Assign, Transit.Submitted), "DESK", "DESK"));

        var r = await svc.ActAsync(new Pr2ActionRequest(1, WorkflowAction.AssignOfficer, null, SpecificPersonId: "OFF1"), User("DESK"));

        Assert.True(r.Ok, r.Error);
        var form = repo.Get(1);
        Assert.Equal(new WorkflowStep(Stage.SafetyReview, Position.Officer, Transit.Submitted), form.Step);
        Assert.Equal("OFF1", form.SafetyOfficerId);
        Assert.Equal("OFF1", form.PendingRecipientId);
    }

    [Fact]
    public async Task Act_officer_submit_with_jump_goes_to_manager_and_sets_jump_flag()
    {
        var (svc, repo, resolver, _) = Build();
        resolver.Default = Emp("MGR", "經理");
        repo.Seed(FormAt(new WorkflowStep(Stage.SafetyReview, Position.Officer, Transit.Working), "OFF1", "OFF1"));

        var r = await svc.ActAsync(new Pr2ActionRequest(1, WorkflowAction.Submit, "意見", IsJump: true), User("OFF1"));

        Assert.True(r.Ok, r.Error);
        var form = repo.Get(1);
        Assert.Equal(Position.Manager, form.Step.Position);
        Assert.True(form.IsJump);
        Assert.Equal("OFF1", form.SafetyOfficerId);
        Assert.Equal("MGR", form.PendingRecipientId);
    }

    [Fact]
    public async Task Act_leader_close_resolves_next_approver_and_overrides_position()
    {
        var (svc, repo, resolver, _) = Build();
        repo.Seed(FormAt(new WorkflowStep(Stage.Approval, Position.Leader, Transit.Working), "LEAD", "LEAD"));
        resolver.ApprovalNext = (Position.DeputyEng, Emp("DENG", "工程副處長"));

        var r = await svc.ActAsync(new Pr2ActionRequest(1, WorkflowAction.CloseDecision, null, AgreeClose: true), User("LEAD"));

        Assert.True(r.Ok, r.Error);
        var form = repo.Get(1);
        Assert.Equal(Position.DeputyEng, form.Step.Position);
        Assert.Equal("DENG", form.PendingRecipientId);
    }

    [Fact]
    public async Task Act_director_approve_closes_form_without_email()
    {
        var (svc, repo, resolver, email) = Build();
        resolver.Default = Emp("DIR", "處長");
        repo.Seed(FormAt(new WorkflowStep(Stage.Approval, Position.Director, Transit.Working), "DIR", "DIR"));

        var r = await svc.ActAsync(new Pr2ActionRequest(1, WorkflowAction.Approve, "准"), User("DIR"));

        Assert.True(r.Ok, r.Error);
        Assert.Equal(FormStatus.Closed, repo.Get(1).Status);
        Assert.Empty(email.Sent);
    }

    [Fact]
    public async Task Open_takes_over_as_holder_without_changing_stage()
    {
        var (svc, repo, _, _) = Build();
        repo.Seed(FormAt(new WorkflowStep(Stage.SafetyReview, Position.Officer, Transit.Submitted), "DESK", "OFF1"));

        await svc.OpenAsync(1, User("OFF1"));

        var form = repo.Get(1);
        Assert.Equal("OFF1", form.CurrentHolderId);
        Assert.Equal(Stage.SafetyReview, form.Step.Stage);
        Assert.Equal(Transit.Working, form.Step.Transit);
    }

    private static Pr2Form FormAt(WorkflowStep step, string holder, string pending) => new()
    {
        FormId = 1,
        FormNumber = "SI-1151111-01-0",
        Step = step,
        Status = FormStatus.InProgress,
        CurrentHolderId = holder,
        PendingRecipientId = pending,
        CheckType = Pr2CheckType.Safety,
        CheckDate = Today,
        ProjectId = 1,
        ContractorId = 2,
        LeaderId = "LEAD",
        CreatedBy = "MAKER",
        Items = [new Pr2Item { ItemId = 10, FormId = 1, Kind = Pr2ItemKind.Con, Ordinal = 1, Description = "未戴安全帽" }],
    };

    // ===== fakes =====

    private sealed class FakePr2Repo : IPr2Repository
    {
        private readonly Dictionary<long, Pr2Form> _forms = new();
        private long _seq;
        private int _num;

        public Pr2Form Get(long id) => _forms[id];
        public void Seed(Pr2Form form) => _forms[form.FormId] = form;

        public Task<string> NextFormNumberAsync(string checkType, DateTime date, CancellationToken ct = default)
            => Task.FromResult($"{checkType}-{date:yyMMdd}-{++_num:00}-0");

        public Task<long> CreateAsync(Pr2Form form, string actorTitle, CancellationToken ct = default)
        {
            form.FormId = ++_seq;
            _forms[form.FormId] = form;
            return Task.FromResult(form.FormId);
        }

        public Task<Pr2Form?> GetAsync(long formId, CancellationToken ct = default)
            => Task.FromResult(_forms.TryGetValue(formId, out var f) ? f : null);

        public Task<Pr2Form?> GetByNumberAsync(string formNumber, CancellationToken ct = default)
            => Task.FromResult<Pr2Form?>(null);

        public Task ApplyTransitionAsync(Pr2Form form, WorkflowTransition transition, string actorId, string actorTitle,
            string? comment, CancellationToken ct = default)
        {
            form.Step = transition.To;
            form.Status = transition.Status;
            if (transition.IncrementRevision) form.Revision++;
            _forms[form.FormId] = form;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Pr2InboxRow>> GetInboxAsync(string employeeId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Pr2InboxRow>>([]);
        public Task<IReadOnlyList<Pr2InboxRow>> SearchAsync(Pr2SearchQuery query, string employeeId, bool allScopes, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Pr2InboxRow>>([]);
        public Task<IReadOnlyList<WorkflowHistoryRow>> GetHistoryAsync(long formId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkflowHistoryRow>>([]);
    }

    private sealed class FakeResolver : IAssigneeResolver
    {
        public Employee? Default { get; set; } = Emp("NEXT", "下一關");
        public (Position Position, Employee? Approver)? ApprovalNext { get; set; }

        public Task<Employee?> ResolveAsync(IWorkflowForm form, AssigneeRule rule, string actorId, string? specificPersonId, CancellationToken ct = default)
            => Task.FromResult(rule switch
            {
                AssigneeRule.None => null,
                AssigneeRule.SpecificPerson => specificPersonId is null ? null : Emp(specificPersonId),
                AssigneeRule.Self => form.CurrentHolderId is null ? null : Emp(form.CurrentHolderId),
                AssigneeRule.NextApprover => (ApprovalNext ?? (Position.Director, Default)).Item2,
                _ => Default,
            });

        public Task<(Position Position, Employee? Approver)> ResolveApprovalNextAsync(IWorkflowForm form, CancellationToken ct = default)
            => Task.FromResult(ApprovalNext ?? (Position.Director, Default));
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
