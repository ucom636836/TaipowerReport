using Taipower.Safety.Core.Common;
using Taipower.Safety.Core.Forms.Pr5;
using Taipower.Safety.Core.Reference;
using Xunit;

namespace Taipower.Safety.Tests;

public class Pr5ServiceTests
{
    private static readonly DateTime Today = new(2026, 11, 11);

    private static CurrentUser User(string id = "AUD001") =>
        new(id, "稽核員", "69", null, 5, 3, new HashSet<int>());

    private static Pr5CreateRequest ValidReq(bool notify = true, Pr5Source source = Pr5Source.Pr1, decimal? amount = 30000) => new(
        source, SourceFormId: source == Pr5Source.Pr1 ? 5 : null, SourceItemId: source == Pr5Source.Pr1 ? 10 : null,
        ProjectId: 1, ContractorId: 2, AuditGroupId: "69",
        HazardId: null, DeficiencyTypeId: null, DeficiencyItemId: null,
        ViolationDate: Today, Place: "施工現場", UnitPeriod: null,
        PenaltyItemCode: "68", PenaltyAmount: amount, ImproveDeadline: Today.AddDays(14), Remark: "未戴安全帽", Notify: notify);

    private static (Pr5Service svc, FakePr5Repo repo, FakeRefs refs, FakeEmailSink email) Build(bool withContact = true)
    {
        var repo = new FakePr5Repo();
        var refs = new FakeRefs(withContact);
        var email = new FakeEmailSink();
        return (new Pr5Service(repo, refs, email), repo, refs, email);
    }

    [Fact]
    public async Task Create_from_pr1_persists_links_item_and_notifies_contractor()
    {
        var (svc, repo, _, email) = Build();

        var r = await svc.CreateAsync(ValidReq(), User());

        Assert.True(r.Ok, r.Error);
        Assert.Single(repo.Items);
        var p = repo.Items[0];
        Assert.Equal(Pr5Source.Pr1, p.Source);
        Assert.Equal(30000, p.PenaltyAmount);
        Assert.NotNull(p.NotifiedAt);
        Assert.Single(email.Sent);
        Assert.Contains(repo.Links, l => l.ItemId == 10 && l.Number == p.PenaltyNumber);
    }

    [Fact]
    public async Task Create_standalone_without_notify_sends_no_email_and_no_link()
    {
        var (svc, repo, _, email) = Build();
        var r = await svc.CreateAsync(ValidReq(notify: false, source: Pr5Source.Standalone), User());
        Assert.True(r.Ok, r.Error);
        Assert.Empty(email.Sent);
        Assert.Empty(repo.Links);
        Assert.Null(repo.Items[0].NotifiedAt);
    }

    [Fact]
    public async Task Create_rejects_missing_project_or_contractor()
    {
        var (svc, _, _, _) = Build();
        var bad = ValidReq() with { ProjectId = 0 };
        Assert.False((await svc.CreateAsync(bad, User())).Ok);
    }

    [Fact]
    public async Task Create_rejects_negative_amount()
    {
        var (svc, _, _, _) = Build();
        Assert.False((await svc.CreateAsync(ValidReq(amount: -1), User())).Ok);
    }

    [Fact]
    public async Task Create_with_notify_but_no_contacts_still_succeeds_without_email()
    {
        var (svc, repo, _, email) = Build(withContact: false);
        var r = await svc.CreateAsync(ValidReq(), User());
        Assert.True(r.Ok, r.Error);
        Assert.Empty(email.Sent);
        Assert.Null(repo.Items[0].NotifiedAt);
    }

    [Fact]
    public async Task Void_sets_status_and_blocks_double_void()
    {
        var (svc, repo, _, _) = Build();
        var p = repo.Seed(Pr5Status.Issued);

        var r = await svc.VoidAsync(p.PenaltyId, User());
        Assert.True(r.Ok, r.Error);
        Assert.Equal(Pr5Status.Void, repo.Get(p.PenaltyId).Status);

        var again = await svc.VoidAsync(p.PenaltyId, User());
        Assert.False(again.Ok);
    }

    [Fact]
    public async Task MarkReceived_sets_status()
    {
        var (svc, repo, _, _) = Build();
        var p = repo.Seed(Pr5Status.Issued);
        var r = await svc.MarkReceivedAsync(p.PenaltyId, User());
        Assert.True(r.Ok, r.Error);
        Assert.Equal(Pr5Status.Received, repo.Get(p.PenaltyId).Status);
    }

    [Fact]
    public async Task Status_change_rejects_unknown_id()
    {
        var (svc, _, _, _) = Build();
        Assert.False((await svc.VoidAsync(999, User())).Ok);
    }

    [Fact]
    public async Task Notify_resend_fails_without_contacts()
    {
        var (svc, repo, _, _) = Build(withContact: false);
        var p = repo.Seed(Pr5Status.Issued);
        var r = await svc.NotifyAsync(p.PenaltyId, User());
        Assert.False(r.Ok);
    }

    // ===== fakes =====

    private sealed class FakePr5Repo : IPr5Repository
    {
        public List<Pr5Penalty> Items { get; } = new();
        public List<(long ItemId, string Number)> Links { get; } = new();
        private long _seq;
        private int _num;

        public Pr5Penalty Get(long id) => Items.First(x => x.PenaltyId == id);
        public Pr5Penalty Seed(Pr5Status status)
        {
            var p = new Pr5Penalty { PenaltyId = ++_seq, PenaltyNumber = $"P5-SEED-{_seq:D2}", ProjectId = 1, ContractorId = 2, Status = status, RowVersion = [1, 2, 3, 4] };
            Items.Add(p);
            return p;
        }

        public Task<string> NextNumberAsync(DateTime date, CancellationToken ct = default) => Task.FromResult($"P5-T-{++_num:D2}");

        public Task<long> CreateAsync(Pr5Penalty p, CancellationToken ct = default)
        {
            p.PenaltyId = ++_seq;
            p.RowVersion = [1, 2, 3, 4];
            Items.Add(p);
            return Task.FromResult(p.PenaltyId);
        }

        public Task<Pr5Penalty?> GetAsync(long id, CancellationToken ct = default)
            => Task.FromResult(Items.FirstOrDefault(x => x.PenaltyId == id));

        public Task<IReadOnlyList<Pr5Row>> SearchAsync(Pr5SearchQuery q, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Pr5Row>>([]);

        public Task<bool> SetStatusAsync(long id, Pr5Status status, byte[]? rowVersion, CancellationToken ct = default)
        {
            var p = Items.FirstOrDefault(x => x.PenaltyId == id);
            if (p is null) return Task.FromResult(false);
            p.Status = status;
            return Task.FromResult(true);
        }

        public Task LinkPr1ItemAsync(long itemId, string penaltyNumber, CancellationToken ct = default)
        {
            Links.Add((itemId, penaltyNumber));
            return Task.CompletedTask;
        }

        public Task<DateTime?> SetNotifiedAsync(long id, CancellationToken ct = default)
        {
            var p = Items.FirstOrDefault(x => x.PenaltyId == id);
            if (p is not null) p.NotifiedAt = DateTime.Now;
            return Task.FromResult<DateTime?>(p?.NotifiedAt);
        }
    }

    private sealed class FakeRefs : IReferenceRepository
    {
        private readonly bool _withContact;
        public FakeRefs(bool withContact) => _withContact = withContact;

        public Task<Contractor?> GetContractorAsync(int contractorId, CancellationToken ct = default)
        {
            var c = new Contractor { ContractorId = contractorId, Name = "示範承攬商" };
            if (_withContact) c.Contacts.Add(new ContractorContact { Name = "窗口", Email = "contact@example.com" });
            return Task.FromResult<Contractor?>(c);
        }

        public Task<EngineeringProject?> GetProjectAsync(int projectId, CancellationToken ct = default)
            => Task.FromResult<EngineeringProject?>(new EngineeringProject { ProjectId = projectId, Name = "示範工程", ProjectNumber = "E-001" });

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

    private sealed class FakeEmailSink : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = new();
        public Task SendAsync(EmailMessage message, CancellationToken ct = default) { Sent.Add(message); return Task.CompletedTask; }
    }
}
