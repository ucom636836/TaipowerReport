using Taipower.Safety.Core.Forms.Workflow;

namespace Taipower.Safety.Core.Forms.Pr3;

// Pr3 = 各級主管走動管理紀錄暨追蹤表。與 Pr1/Pr2 的 C1→C6 不同：
// 巡查人起單 → (填單人≠巡查人時先經巡查人確認) → 經辦組(S2) → 工安組(S3) → 結案 / 持續追蹤迴圈。
// 重用共用的 Position / Transit / FormStatus 與底盤(FormInstance/WorkflowHistory)、AssigneeResolver 的組織導航，
// 但關卡圖與步碼為 Pr3 專屬（legacy 以數字步碼 101/201/221/301…，前向流程在 legacy 未實作，此處依
// schema + 退回對照 + Pr1 類比忠實重建）。

/// <summary>Pr3 的簽核階段。</summary>
public enum Pr3Stage
{
    Filing = 1,          // 起單 / 巡查人確認
    HandlingReview = 2,  // 經辦組簽註 (S2)：辦理組別 登記桌→經辦→課長→經理
    SafetyReview = 3,    // 工安組查證/管考 (S3)：工安組 登記桌→經辦→課長→經理
    Closed = 9,          // 結案
}

/// <summary>Pr3 一張單在流程中的位置，序列化進 FormInstance.CurrentStep。</summary>
public readonly record struct Pr3Step(Pr3Stage Stage, Position Position, Transit Transit)
{
    public static readonly Pr3Step Draft = new(Pr3Stage.Filing, Position.Creator, Transit.Working);
    public override string ToString() => $"{Stage}/{Position}/{Transit}";
}

/// <summary>使用者可對 Pr3 單執行的動作。</summary>
public enum Pr3Action
{
    Submit,           // 送出 / 上呈 / 建單 / 巡查人確認
    AssignChief,      // 登記桌派課長
    AssignOfficer,    // 登記桌派經辦
    Withdraw,         // 抽回
    ReturnToOfficer,  // 退回簽註經辦
    ReturnToChief,    // 退回課長
    ReturnToDesk,     // 退回登記桌
    ReturnToFiler,    // 退回填單人/巡查人 (legacy 202)
    ReturnToHandling, // 工安組重回經辦組 (legacy 352)
    ContinueTracking, // 持續追蹤 (check8=1，迴圈回經辦組，Archive+1)
    Close,            // 結案 (check8=2)
}

/// <summary>由誰領下一關。流程只決定角色，實際人員由 Pr3 resolver 查出。</summary>
public enum Pr3AssigneeRule
{
    None,
    Inspector,       // 巡查人 (Pr3_Iman)
    Filer,           // 填單人 (Pr3_Eman / CreatedBy)
    HandlingDesk,    // 辦理組別 (Pr3_Group_id) 登記桌
    SafetyDesk,      // 工安組(69) 登記桌
    SpecificPerson,  // 指派對象
    AssignedChief,   // 本案本區塊已指派課長
    Manager,         // 本區塊組別經理
    OfficerOfRecord, // 本區塊已簽核經辦
    Self,            // 維持現持有人
}

public sealed record Pr3Transition(
    Pr3Step To,
    Pr3AssigneeRule Assignee,
    bool SendEmail,
    bool IncrementRetimes = false,   // 退回：重會次數 +1 (Pr3_retimes)
    bool IncrementArchive = false,   // 持續追蹤：迴圈次數 +1 (Pr3_Archive)
    FormStatus Status = FormStatus.InProgress,
    string? HistoryLabel = null);

/// <summary>執行動作時的輸入脈絡。</summary>
public sealed record Pr3ActionContext
{
    /// <summary>填單人是否即巡查人 (決定起單後是否需巡查人確認)。</summary>
    public bool FilerIsInspector { get; init; }
    /// <summary>跳關：經辦直送經理，略過課長。</summary>
    public bool IsJump { get; init; }
}

/// <summary>Pr3 簽核流程狀態機 (純邏輯，可單元測試)。</summary>
public static class Pr3Workflow
{
    public static IReadOnlyList<Pr3Action> AllowedActions(Pr3Step step) => step.Stage switch
    {
        Pr3Stage.Filing => step.Position switch
        {
            Position.Creator => [Pr3Action.Submit],                       // 填單人建單/送出
            Position.Inspector => [Pr3Action.Submit, Pr3Action.ReturnToFiler], // 巡查人確認/退回
            _ => [],
        },
        Pr3Stage.HandlingReview => step.Position switch
        {
            Position.Assign => [Pr3Action.AssignChief, Pr3Action.AssignOfficer, Pr3Action.ReturnToFiler],
            Position.Officer => [Pr3Action.Submit, Pr3Action.Withdraw, Pr3Action.ReturnToDesk],
            Position.Chief => [Pr3Action.Submit, Pr3Action.Withdraw, Pr3Action.ReturnToOfficer],
            Position.Manager => [Pr3Action.Submit, Pr3Action.Withdraw, Pr3Action.ReturnToOfficer, Pr3Action.ReturnToChief],
            _ => [],
        },
        Pr3Stage.SafetyReview => step.Position switch
        {
            Position.Assign => [Pr3Action.AssignChief, Pr3Action.AssignOfficer, Pr3Action.ReturnToHandling],
            Position.Officer => [Pr3Action.Submit, Pr3Action.Withdraw, Pr3Action.ReturnToDesk],
            Position.Chief => [Pr3Action.Submit, Pr3Action.Withdraw, Pr3Action.ReturnToOfficer],
            Position.Manager => [Pr3Action.ContinueTracking, Pr3Action.Close, Pr3Action.ReturnToOfficer, Pr3Action.ReturnToChief],
            _ => [],
        },
        _ => [],
    };

    /// <summary>計算一次轉移；回傳 null 代表不允許。Withdraw 為不改步驟的抽回，另行處理。</summary>
    public static Pr3Transition? Next(Pr3Step step, Pr3Action action, Pr3ActionContext ctx)
    {
        if (!AllowedActions(step).Contains(action)) return null;
        return step.Stage switch
        {
            Pr3Stage.Filing => FilingStage(step, action, ctx),
            Pr3Stage.HandlingReview => ReviewStage(step, action, ctx, Pr3Stage.HandlingReview),
            Pr3Stage.SafetyReview => ReviewStage(step, action, ctx, Pr3Stage.SafetyReview),
            _ => null,
        };
    }

    private static Pr3Transition? FilingStage(Pr3Step step, Pr3Action action, Pr3ActionContext ctx) => (step.Position, action) switch
    {
        // 填單人送出：填單人即巡查人→直送經辦組登記桌；否則先送巡查人確認。
        (Position.Creator, Pr3Action.Submit) => ctx.FilerIsInspector
            ? new Pr3Transition(new Pr3Step(Pr3Stage.HandlingReview, Position.Assign, Transit.Submitted),
                Pr3AssigneeRule.HandlingDesk, SendEmail: true, HistoryLabel: "建單")
            : new Pr3Transition(new Pr3Step(Pr3Stage.Filing, Position.Inspector, Transit.Submitted),
                Pr3AssigneeRule.Inspector, SendEmail: true, HistoryLabel: "建單（送巡查人確認）"),

        // 巡查人確認→送經辦組登記桌。
        (Position.Inspector, Pr3Action.Submit) => new Pr3Transition(
            new Pr3Step(Pr3Stage.HandlingReview, Position.Assign, Transit.Submitted),
            Pr3AssigneeRule.HandlingDesk, SendEmail: true, HistoryLabel: "巡查人確認"),

        // 巡查人退回填單人。
        (Position.Inspector, Pr3Action.ReturnToFiler) => new Pr3Transition(
            Pr3Step.Draft with { Transit = Transit.Submitted },
            Pr3AssigneeRule.Filer, SendEmail: true, IncrementRetimes: true, HistoryLabel: "退回填單人"),

        _ => null,
    };

    private static Pr3Transition? ReviewStage(Pr3Step step, Pr3Action action, Pr3ActionContext ctx, Pr3Stage stage)
    {
        var isHandling = stage == Pr3Stage.HandlingReview;
        switch (action)
        {
            case Pr3Action.AssignChief:
                return new Pr3Transition(step with { Position = Position.Assign, Transit = Transit.Submitted },
                    Pr3AssigneeRule.SpecificPerson, SendEmail: true, HistoryLabel: "派單（課長）");

            case Pr3Action.AssignOfficer:
                return new Pr3Transition(new Pr3Step(stage, Position.Officer, Transit.Submitted),
                    Pr3AssigneeRule.SpecificPerson, SendEmail: true, HistoryLabel: "派單（經辦）");

            case Pr3Action.Submit when step.Position == Position.Officer:
                return ctx.IsJump
                    ? new Pr3Transition(new Pr3Step(stage, Position.Manager, Transit.Submitted), Pr3AssigneeRule.Manager, true, HistoryLabel: "簽核")
                    : new Pr3Transition(new Pr3Step(stage, Position.Chief, Transit.Submitted), Pr3AssigneeRule.AssignedChief, true, HistoryLabel: "簽核");

            case Pr3Action.Submit when step.Position == Position.Chief:
                return new Pr3Transition(new Pr3Step(stage, Position.Manager, Transit.Submitted), Pr3AssigneeRule.Manager, true, HistoryLabel: "簽核");

            // 經辦組經理核定 → 轉工安組登記桌 (S2→S3)。
            case Pr3Action.Submit when step.Position == Position.Manager && isHandling:
                return new Pr3Transition(new Pr3Step(Pr3Stage.SafetyReview, Position.Assign, Transit.Submitted),
                    Pr3AssigneeRule.SafetyDesk, true, HistoryLabel: "經辦組核定");

            // 工安組經理：持續追蹤（迴圈回經辦組）或結案。
            case Pr3Action.ContinueTracking when step.Position == Position.Manager && !isHandling:
                return new Pr3Transition(new Pr3Step(Pr3Stage.HandlingReview, Position.Assign, Transit.Submitted),
                    Pr3AssigneeRule.HandlingDesk, true, IncrementArchive: true, HistoryLabel: "持續追蹤");

            case Pr3Action.Close when step.Position == Position.Manager && !isHandling:
                return new Pr3Transition(step with { Transit = Transit.Submitted }, Pr3AssigneeRule.Self, false,
                    Status: FormStatus.Closed, HistoryLabel: "結案");

            case Pr3Action.ReturnToOfficer:
                return new Pr3Transition(new Pr3Step(stage, Position.Officer, Transit.Submitted),
                    Pr3AssigneeRule.OfficerOfRecord, true, HistoryLabel: "退回經辦");

            case Pr3Action.ReturnToChief:
                return new Pr3Transition(new Pr3Step(stage, Position.Chief, Transit.Submitted),
                    Pr3AssigneeRule.AssignedChief, true, HistoryLabel: "退回課長");

            case Pr3Action.ReturnToDesk:
                return new Pr3Transition(new Pr3Step(stage, Position.Assign, Transit.Submitted),
                    isHandling ? Pr3AssigneeRule.HandlingDesk : Pr3AssigneeRule.SafetyDesk, true, HistoryLabel: "退回登記桌");

            // 經辦組登記桌退回填單人。
            case Pr3Action.ReturnToFiler when isHandling && step.Position == Position.Assign:
                return new Pr3Transition(Pr3Step.Draft with { Transit = Transit.Submitted },
                    Pr3AssigneeRule.Filer, true, IncrementRetimes: true, HistoryLabel: "退回填單人");

            // 工安組登記桌重回經辦組 (legacy 352)。
            case Pr3Action.ReturnToHandling when !isHandling && step.Position == Position.Assign:
                return new Pr3Transition(new Pr3Step(Pr3Stage.HandlingReview, Position.Assign, Transit.Submitted),
                    Pr3AssigneeRule.HandlingDesk, true, IncrementRetimes: true, HistoryLabel: "重回經辦組");

            default:
                return null;
        }
    }
}
