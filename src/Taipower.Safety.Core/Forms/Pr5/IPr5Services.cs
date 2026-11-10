namespace Taipower.Safety.Core.Forms.Pr5;

public interface IPr5Repository
{
    Task<string> NextNumberAsync(DateTime date, CancellationToken ct = default);
    Task<long> CreateAsync(Pr5Penalty penalty, CancellationToken ct = default);
    Task<Pr5Penalty?> GetAsync(long penaltyId, CancellationToken ct = default);
    Task<IReadOnlyList<Pr5Row>> SearchAsync(Pr5SearchQuery query, CancellationToken ct = default);
    /// <summary>更新狀態（簽收 / 作廢）；回傳是否成功（樂觀並行以 RowVersion 比對）。</summary>
    Task<bool> SetStatusAsync(long penaltyId, Pr5Status status, byte[]? rowVersion, CancellationToken ct = default);
    /// <summary>把罰單號回寫到來源 Pr1 缺失項（Pr1Item.PenaltyTicketNo）。</summary>
    Task LinkPr1ItemAsync(long itemId, string penaltyNumber, CancellationToken ct = default);
    Task<DateTime?> SetNotifiedAsync(long penaltyId, CancellationToken ct = default);
}

public sealed class Pr5SearchQuery
{
    public int? ProjectId { get; set; }
    public int? ContractorId { get; set; }
    public Pr5Status? Status { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
}
