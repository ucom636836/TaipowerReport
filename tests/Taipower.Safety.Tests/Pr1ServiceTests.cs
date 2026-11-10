using Taipower.Safety.Core.Common;
using Taipower.Safety.Core.Forms.Pr1;
using Taipower.Safety.Core.Org;
using Taipower.Safety.Core.Reference;
using Xunit;

using Taipower.Safety.Core.Forms.Workflow;
namespace Taipower.Safety.Tests;

/// <summary>
/// Pr1Service 編排邏輯的單元測試（以 fake 取代持久化／指派解析／郵件，不需 DB）。
/// 聚焦於流程引擎以外、服務層自己的決策：建單驗證、授權、派單寫入、
/// 旗標記錄，以及 C6「下一關位置與人員一起算」的覆寫。
/// </summary>
public class Pr1ServiceTests
{
    // ---- 共用資料 ----
    private static readonly DateTime Today = new(2026, 11, 11);

    private static CurrentUser User(string id, string name = "測試員") =>
        new(id, name, "69", null, 5, 3, new HashSet<int>());

    private static Employee Emp(string id, string name = "某人") =>
        new() { EmployeeId = id, Name = name, Email = $"{id}@test.local" };

    private static Pr1CreateRequest ValidCreate(IReadOnlyList<Pr1Item>? items = null) => new(
        PatrolType: PatrolType.Safety, PatrolDate: Today, ProjectId: 1, ContractorId: 2,
        LeaderId: "LEAD", TeamMembers: null, ImproveGroupId: null,
        Items: items ?? [new Pr1Item { Kind = ItemKind.Con, Description = "未戴安全帽" }]);

    private static (Pr1Service svc, FakePr1Repository repo, FakeResolver resolver, FakeEmail email, FakeStorage storage) Build()
    {
        var repo = new FakePr1Repository();
        var resolver = new FakeResolver();
        var email = new FakeEmail();
        var storage = new FakeStorage();
        var composer = new Pr1EmailComposer(new FakeRefs()) { PublicBaseUrl = () => "http://test/" };
        var svc = new Pr1Service(repo, resolver, email, composer, storage, new StorageOptions { MaxFileBytes = 1_000_000 });
        return (svc, repo, resolver, email, storage);
    }

    // 最小有效 PNG 檔頭（magic bytes），供照片檢核通過。
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0, 0, 0];

    // ---- 建單 ----

    [Fact]
    public async Task Create_valid_stores_form_at_desk_and_notifies()
    {
        var (svc, repo, resolver, email, _) = Build();
        resolver.Default = Emp("DESK", "登記桌");

        var result = await svc.CreateAsync(ValidCreate(), User("MAKER"));

        Assert.True(result.Ok, result.Error);
        var form = repo.Get(result.FormId);
        Assert.Equal(new WorkflowStep(Stage.SafetyReview, Position.Assign, Transit.Submitted), form.Step);
        Assert.Equal("MAKER", form.CreatedBy);
        Assert.Equal("DESK", form.PendingRecipientId);
        Assert.False(string.IsNullOrEmpty(form.FormNumber));
        Assert.Single(email.Sent);
    }

    [Fact]
    public async Task Create_rejects_empty_items()
    {
        var (svc, _, resolver, _, _) = Build();
        resolver.Default = Emp("DESK");
        var result = await svc.CreateAsync(ValidCreate(items: []), User("MAKER"));
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task Create_rejects_unknown_patrol_type()
    {
        var (svc, _, resolver, _, _) = Build();
        resolver.Default = Emp("DESK");
        var bad = ValidCreate() with { PatrolType = "ZZ" };
        var result = await svc.CreateAsync(bad, User("MAKER"));
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task Create_fails_when_no_safety_desk_found()
    {
        var (svc, _, resolver, _, _) = Build();
        resolver.Default = null; // 工安組查無登記桌
        var result = await svc.CreateAsync(ValidCreate(), User("MAKER"));
        Assert.False(result.Ok);
        Assert.Contains("登記桌", result.Error);
    }

    // ---- 授權 ----

    [Fact]
    public async Task Act_rejects_user_who_is_not_holder_or_recipient()
    {
        var (svc, repo, _, _, _) = Build();
        repo.Seed(FormAt(new WorkflowStep(Stage.SafetyReview, Position.Assign, Transit.Submitted),
            holder: "DESK", pending: "DESK"));
        var result = await svc.ActAsync(new Pr1ActionRequest(1, WorkflowAction.AssignOfficer, null, SpecificPersonId: "OFF"),
            User("OUTSIDER"));
        Assert.False(result.Ok);
        Assert.Contains("處理人", result.Error);
    }

    [Fact]
    public async Task Act_rejects_action_not_allowed_in_current_step()
    {
        var (svc, repo, _, _, _) = Build();
        repo.Seed(FormAt(new WorkflowStep(Stage.SafetyReview, Position.Officer, Transit.Working),
            holder: "OFF", pending: "OFF"));
        // 經辦關不允許「核可」
        var result = await svc.ActAsync(new Pr1ActionRequest(1, WorkflowAction.Approve, null), User("OFF"));
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task Act_rejects_already_closed_form()
    {
        var (svc, repo, _, _, _) = Build();
        var form = FormAt(new WorkflowStep(Stage.Approval, Position.Director, Transit.Submitted), holder: "DIR", pending: "DIR");
        form.Status = FormStatus.Closed;
        repo.Seed(form);
        var result = await svc.ActAsync(new Pr1ActionRequest(1, WorkflowAction.Approve, null), User("DIR"));
        Assert.False(result.Ok);
        Assert.Contains("結案或作廢", result.Error);
    }

    // ---- 派單 / 旗標寫入 ----

    [Fact]
    public async Task Act_assign_officer_records_officer_and_advances_step()
    {
        var (svc, repo, _, _, _) = Build();
        repo.Seed(FormAt(new WorkflowStep(Stage.SafetyReview, Position.Assign, Transit.Submitted),
            holder: "DESK", pending: "DESK"));

        var result = await svc.ActAsync(
            new Pr1ActionRequest(1, WorkflowAction.AssignOfficer, null, SpecificPersonId: "OFF1"), User("DESK"));

        Assert.True(result.Ok, result.Error);
        var form = repo.Get(1);
        Assert.Equal(new WorkflowStep(Stage.SafetyReview, Position.Officer, Transit.Submitted), form.Step);
        Assert.Equal("OFF1", form.SafetyOfficerId);
        Assert.Equal("OFF1", form.PendingRecipientId);
    }

    [Fact]
    public async Task Act_officer_submit_with_jump_goes_to_manager_and_sets_jump_flag()
    {
        var (svc, repo, resolver, _, _) = Build();
        resolver.Default = Emp("MGR", "經理");
        repo.Seed(FormAt(new WorkflowStep(Stage.SafetyReview, Position.Officer, Transit.Working),
            holder: "OFF1", pending: "OFF1"));

        var result = await svc.ActAsync(new Pr1ActionRequest(1, WorkflowAction.Submit, "意見", IsJump: true), User("OFF1"));

        Assert.True(result.Ok, result.Error);
        var form = repo.Get(1);
        Assert.Equal(Position.Manager, form.Step.Position);
        Assert.True(form.IsJump);
        Assert.Equal("OFF1", form.SafetyOfficerId);   // 經辦本人被記為簽核經辦
        Assert.Equal("MGR", form.PendingRecipientId);
    }

    // ---- C6 長官鏈：位置與人員須一起推進 ----

    [Fact]
    public async Task Act_leader_close_resolves_next_approver_and_overrides_position()
    {
        var (svc, repo, resolver, _, _) = Build();
        repo.Seed(FormAt(new WorkflowStep(Stage.Approval, Position.Leader, Transit.Working),
            holder: "LEAD", pending: "LEAD"));
        // 去重後下一關是「工程副處長」
        resolver.ApprovalNext = (Position.DeputyEng, Emp("DENG", "工程副處長"));

        var result = await svc.ActAsync(
            new Pr1ActionRequest(1, WorkflowAction.CloseDecision, null, AgreeClose: true), User("LEAD"));

        Assert.True(result.Ok, result.Error);
        var form = repo.Get(1);
        Assert.Equal(Position.DeputyEng, form.Step.Position);   // To.Position 被 resolver 覆寫
        Assert.Equal("DENG", form.PendingRecipientId);
    }

    [Fact]
    public async Task Act_next_approver_missing_person_fails()
    {
        var (svc, repo, resolver, _, _) = Build();
        repo.Seed(FormAt(new WorkflowStep(Stage.Approval, Position.Leader, Transit.Working),
            holder: "LEAD", pending: "LEAD"));
        resolver.ApprovalNext = (Position.DeputyEng, null); // 查無副處長／處長

        var result = await svc.ActAsync(
            new Pr1ActionRequest(1, WorkflowAction.CloseDecision, null, AgreeClose: true), User("LEAD"));

        Assert.False(result.Ok);
        Assert.Contains("長官", result.Error);
    }

    [Fact]
    public async Task Act_director_approve_closes_form_without_email()
    {
        var (svc, repo, resolver, email, _) = Build();
        resolver.Default = Emp("DIR", "處長");
        repo.Seed(FormAt(new WorkflowStep(Stage.Approval, Position.Director, Transit.Working),
            holder: "DIR", pending: "DIR"));

        var result = await svc.ActAsync(new Pr1ActionRequest(1, WorkflowAction.Approve, "准"), User("DIR"));

        Assert.True(result.Ok, result.Error);
        Assert.Equal(FormStatus.Closed, repo.Get(1).Status);
        Assert.Empty(email.Sent);   // 核可結案不再寄信
    }

    // ---- 承接 ----

    [Fact]
    public async Task Open_takes_over_as_holder_without_changing_stage()
    {
        var (svc, repo, _, _, _) = Build();
        repo.Seed(FormAt(new WorkflowStep(Stage.SafetyReview, Position.Officer, Transit.Submitted),
            holder: "DESK", pending: "OFF1"));

        await svc.OpenAsync(1, User("OFF1"));

        var form = repo.Get(1);
        Assert.Equal("OFF1", form.CurrentHolderId);
        Assert.Equal(Stage.SafetyReview, form.Step.Stage);
        Assert.Equal(Transit.Working, form.Step.Transit);
    }

    // ---- 項目照片 ----

    private static Pr1Form HolderFormWithItem() =>
        FormAt(new WorkflowStep(Stage.Improvement, Position.Officer, Transit.Working), holder: "OFF", pending: "OFF");

    [Fact]
    public async Task AddPhoto_valid_stores_file_and_records_attachment()
    {
        var (svc, repo, _, _, storage) = Build();
        repo.Seed(HolderFormWithItem());

        var r = await svc.AddPhotoAsync(1, 10, "Before", "現況.png", PngBytes.Length, new MemoryStream(PngBytes), User("OFF"));

        Assert.True(r.Ok, r.Error);
        Assert.Single(repo.Attachments);
        Assert.Single(storage.Saved);
        Assert.Equal("Before", repo.Attachments[0].Category);
        Assert.Equal("image/png", repo.Attachments[0].ContentType);
        Assert.Equal("OFF", repo.Attachments[0].UploadedBy);
    }

    [Fact]
    public async Task AddPhoto_rejects_non_holder()
    {
        var (svc, repo, _, _, storage) = Build();
        repo.Seed(HolderFormWithItem());
        var r = await svc.AddPhotoAsync(1, 10, "Before", "a.png", PngBytes.Length, new MemoryStream(PngBytes), User("OUTSIDER"));
        Assert.False(r.Ok);
        Assert.Empty(storage.Saved);
    }

    [Fact]
    public async Task AddPhoto_rejects_closed_form()
    {
        var (svc, repo, _, _, _) = Build();
        var form = HolderFormWithItem();
        form.Status = FormStatus.Closed;
        repo.Seed(form);
        var r = await svc.AddPhotoAsync(1, 10, "Before", "a.png", PngBytes.Length, new MemoryStream(PngBytes), User("OFF"));
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task AddPhoto_rejects_oversize_file()
    {
        var (svc, repo, _, _, _) = Build(); // MaxFileBytes = 1,000,000
        repo.Seed(HolderFormWithItem());
        var r = await svc.AddPhotoAsync(1, 10, "Before", "a.png", 2_000_000, new MemoryStream(PngBytes), User("OFF"));
        Assert.False(r.Ok);
        Assert.Contains("上限", r.Error);
    }

    [Fact]
    public async Task AddPhoto_rejects_non_image_extension()
    {
        var (svc, repo, _, _, _) = Build();
        repo.Seed(HolderFormWithItem());
        var r = await svc.AddPhotoAsync(1, 10, "Before", "evil.exe", PngBytes.Length, new MemoryStream(PngBytes), User("OFF"));
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task AddPhoto_rejects_unknown_item()
    {
        var (svc, repo, _, _, _) = Build();
        repo.Seed(HolderFormWithItem());
        var r = await svc.AddPhotoAsync(1, 999, "Before", "a.png", PngBytes.Length, new MemoryStream(PngBytes), User("OFF"));
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task AddPhoto_rejects_disguised_non_image_content()
    {
        var (svc, repo, _, _, storage) = Build();
        repo.Seed(HolderFormWithItem());
        var notImage = new MemoryStream([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11]); // 正確副檔名但內容非圖片
        var r = await svc.AddPhotoAsync(1, 10, "Before", "fake.png", 12, notImage, User("OFF"));
        Assert.False(r.Ok);
        Assert.Empty(storage.Saved);
    }

    [Fact]
    public async Task DeletePhoto_removes_attachment_and_file_for_holder()
    {
        var (svc, repo, _, _, storage) = Build();
        repo.Seed(HolderFormWithItem());
        await svc.AddPhotoAsync(1, 10, "After", "ok.png", PngBytes.Length, new MemoryStream(PngBytes), User("OFF"));
        var attId = repo.Attachments[0].AttachmentId;

        var r = await svc.DeletePhotoAsync(attId, User("OFF"));

        Assert.True(r.Ok, r.Error);
        Assert.Empty(repo.Attachments);
        Assert.Empty(storage.Saved);
    }

    [Fact]
    public async Task DeletePhoto_rejects_non_holder()
    {
        var (svc, repo, _, _, _) = Build();
        repo.Seed(HolderFormWithItem());
        await svc.AddPhotoAsync(1, 10, "After", "ok.png", PngBytes.Length, new MemoryStream(PngBytes), User("OFF"));
        var attId = repo.Attachments[0].AttachmentId;

        var r = await svc.DeletePhotoAsync(attId, User("OUTSIDER"));

        Assert.False(r.Ok);
        Assert.Single(repo.Attachments);
    }

    // ---- 逐項 C4 改善 / C5 查證結果 ----

    [Fact]
    public async Task SaveItemResults_at_improvement_records_improve_fields()
    {
        var (svc, repo, _, _, _) = Build();
        repo.Seed(HolderFormWithItem()); // Improvement / Officer

        var r = await svc.SaveItemResultsAsync(1,
            [new Pr1ItemResult(10, Improved: true, NotImprovedReason: null, ImprovedDate: new DateTime(2026, 11, 11), Verified: null, VerifyReason: null)],
            User("OFF"));

        Assert.True(r.Ok, r.Error);
        var it = repo.Get(1).Items.Single();
        Assert.True(it.Improved);
        Assert.Equal(new DateTime(2026, 11, 11), it.ImprovedDate);
    }

    [Fact]
    public async Task SaveItemResults_at_verification_records_verify_fields()
    {
        var (svc, repo, _, _, _) = Build();
        repo.Seed(FormAt(new WorkflowStep(Stage.Verification, Position.Officer, Transit.Working), holder: "OFF", pending: "OFF"));

        var r = await svc.SaveItemResultsAsync(1,
            [new Pr1ItemResult(10, Improved: null, NotImprovedReason: null, ImprovedDate: null, Verified: false, VerifyReason: "尚未完成")],
            User("OFF"));

        Assert.True(r.Ok, r.Error);
        var it = repo.Get(1).Items.Single();
        Assert.False(it.Verified);
        Assert.Equal("尚未完成", it.VerifyReason);
    }

    [Fact]
    public async Task SaveItemResults_rejects_non_holder()
    {
        var (svc, repo, _, _, _) = Build();
        repo.Seed(HolderFormWithItem());
        var r = await svc.SaveItemResultsAsync(1, [new Pr1ItemResult(10, true, null, null, null, null)], User("OUTSIDER"));
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task SaveItemResults_rejects_wrong_stage()
    {
        var (svc, repo, _, _, _) = Build();
        // 工安組簽註階段 (C3) 的經辦不可填改善 / 查證結果
        repo.Seed(FormAt(new WorkflowStep(Stage.SafetyReview, Position.Officer, Transit.Working), holder: "OFF", pending: "OFF"));
        var r = await svc.SaveItemResultsAsync(1, [new Pr1ItemResult(10, true, null, null, null, null)], User("OFF"));
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task SaveItemResults_rejects_when_no_known_item()
    {
        var (svc, repo, _, _, _) = Build();
        repo.Seed(HolderFormWithItem());
        var r = await svc.SaveItemResultsAsync(1, [new Pr1ItemResult(999, true, null, null, null, null)], User("OFF"));
        Assert.False(r.Ok);
    }

    // ---- helpers ----

    private static Pr1Form FormAt(WorkflowStep step, string holder, string pending) => new()
    {
        FormId = 1,
        FormNumber = "SI-1151111-01-0",
        Step = step,
        Status = FormStatus.InProgress,
        CurrentHolderId = holder,
        PendingRecipientId = pending,
        PatrolType = PatrolType.Safety,
        PatrolDate = Today,
        ProjectId = 1,
        ContractorId = 2,
        LeaderId = "LEAD",
        CreatedBy = "MAKER",
        Items = [new Pr1Item { ItemId = 10, FormId = 1, Kind = ItemKind.Con, Ordinal = 1, Description = "未戴安全帽" }],
    };

    // ================= fakes =================

    private sealed class FakePr1Repository : IPr1Repository
    {
        private readonly Dictionary<long, Pr1Form> _forms = new();
        private long _seq;
        private int _num;

        public Pr1Form Get(long id) => _forms[id];
        public void Seed(Pr1Form form) => _forms[form.FormId] = form;

        public Task<string> NextFormNumberAsync(string patrolType, DateTime date, CancellationToken ct = default)
            => Task.FromResult($"{patrolType}-{date:yyMMdd}-{++_num:00}-0");

        public Task<long> CreateAsync(Pr1Form form, string actorTitle, CancellationToken ct = default)
        {
            form.FormId = ++_seq;
            _forms[form.FormId] = form;
            return Task.FromResult(form.FormId);
        }

        public Task<Pr1Form?> GetAsync(long formId, CancellationToken ct = default)
            => Task.FromResult(_forms.TryGetValue(formId, out var f) ? f : null);

        public Task<Pr1Form?> GetByNumberAsync(string formNumber, CancellationToken ct = default)
            => Task.FromResult<Pr1Form?>(null);

        // 仿真實 repo：套用轉移後更新步驟 / 狀態 / 重會次數。
        public Task ApplyTransitionAsync(Pr1Form form, WorkflowTransition transition, string actorId, string actorTitle,
            string? comment, CancellationToken ct = default)
        {
            form.Step = transition.To;
            form.Status = transition.Status;
            if (transition.IncrementRevision) form.Revision++;
            _forms[form.FormId] = form;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Pr1InboxRow>> GetInboxAsync(string employeeId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Pr1InboxRow>>([]);
        public Task<IReadOnlyList<Pr1InboxRow>> SearchAsync(Pr1SearchQuery query, string employeeId, bool allScopes, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Pr1InboxRow>>([]);
        public Task<IReadOnlyList<WorkflowHistoryRow>> GetHistoryAsync(long formId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkflowHistoryRow>>([]);

        public List<Attachment> Attachments { get; } = new();
        private long _attSeq;

        public Task<long> AddAttachmentAsync(Attachment a, CancellationToken ct = default)
        {
            a.AttachmentId = ++_attSeq;
            Attachments.Add(a);
            return Task.FromResult(a.AttachmentId);
        }

        public Task<Pr1AttachmentInfo?> GetAttachmentAsync(long attachmentId, CancellationToken ct = default)
        {
            var a = Attachments.FirstOrDefault(x => x.AttachmentId == attachmentId);
            if (a is null) return Task.FromResult<Pr1AttachmentInfo?>(null);
            var formId = _forms.Values.FirstOrDefault(f => f.Items.Any(i => i.ItemId == a.ItemId))?.FormId ?? 0;
            return Task.FromResult<Pr1AttachmentInfo?>(
                new Pr1AttachmentInfo(a.AttachmentId, a.ItemId, formId, a.Category, a.OriginalName, a.StoredName, a.ContentType));
        }

        public Task DeleteAttachmentAsync(long attachmentId, CancellationToken ct = default)
        {
            Attachments.RemoveAll(x => x.AttachmentId == attachmentId);
            return Task.CompletedTask;
        }

        public Task UpdateItemResultsAsync(long formId, Stage stage, IReadOnlyList<Pr1ItemResult> results, CancellationToken ct = default)
        {
            if (_forms.TryGetValue(formId, out var form))
                foreach (var r in results)
                {
                    var it = form.Items.FirstOrDefault(i => i.ItemId == r.ItemId);
                    if (it is null) continue;
                    if (stage == Stage.Improvement)
                    {
                        it.Improved = r.Improved;
                        it.NotImprovedReason = r.NotImprovedReason;
                        it.ImprovedDate = r.ImprovedDate;
                    }
                    else
                    {
                        it.Verified = r.Verified;
                        it.VerifyReason = r.VerifyReason;
                    }
                }
            return Task.CompletedTask;
        }
    }

    private sealed class FakeStorage : IFileStorage
    {
        public List<string> Saved { get; } = new();
        public Task<string> SaveAsync(string relativeDir, string extension, Stream content, CancellationToken ct = default)
        {
            var path = $"{relativeDir}/{Guid.NewGuid():N}{extension}";
            Saved.Add(path);
            return Task.FromResult(path);
        }
        public Task<Stream?> OpenReadAsync(string storedPath, CancellationToken ct = default)
            => Task.FromResult<Stream?>(Saved.Contains(storedPath) ? new MemoryStream(PngBytes) : null);
        public void Delete(string storedPath) => Saved.Remove(storedPath);
    }

    private sealed class FakeResolver : IAssigneeResolver
    {
        /// <summary>一般規則解析出的員工（null = 查無）。</summary>
        public Employee? Default { get; set; } = Emp("NEXT", "下一關");
        /// <summary>C6 下一位批示者（位置 + 人員）。</summary>
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
        public Task SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
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
