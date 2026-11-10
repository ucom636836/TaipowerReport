namespace Taipower.Safety.Core.Forms.Pr1;

/// <summary>簽核階段 (legacy C1..C6)。</summary>
public enum Stage
{
    Create = 1,        // C1 開單
    SafetyReview = 3,  // C3 工安組簽註意見
    Improvement = 4,   // C4 經辦組處理改善
    Verification = 5,  // C5 工安組查證結果
    Approval = 6,      // C6 長官覆核批示
}

/// <summary>階段內的位置 (legacy sub-step)。</summary>
public enum Position
{
    // Create
    Creator = 0,
    // Review stages (C3/C4/C5)
    Assign = 1,   // 登記桌/課長 派單中 (legacy x0)
    Officer = 2,  // 經辦 (legacy x1)
    Chief = 3,    // 課長 (legacy x2)
    Manager = 4,  // 經理 / 代理經理 (legacy x3)
    // Approval (C6)
    Leader = 5,         // 巡查領隊
    DeputyEng = 6,      // 工程副處長
    DeputySafety = 7,   // 工安副處長
    Director = 8,       // 處長
}

/// <summary>流轉狀態。Working = 持有人開啟中；Submitted = 已送出待收件人。</summary>
public enum Transit
{
    Working = 0,
    Submitted = 1,
}

/// <summary>整張單的結案狀態。</summary>
public enum FormStatus
{
    InProgress,
    Closed,     // 核可結案 (legacy C699)
    Voided,     // 不核可作廢 (legacy C698)
}

/// <summary>一個 Pr1 單在流程中的位置，取代 legacy 的 Pr1_step 字串。</summary>
public readonly record struct Pr1Step(Stage Stage, Position Position, Transit Transit)
{
    public static readonly Pr1Step Draft = new(Stage.Create, Position.Creator, Transit.Working);

    public override string ToString() => $"{Stage}/{Position}/{Transit}";
}

/// <summary>使用者可對單執行的動作。</summary>
public enum Pr1Action
{
    Submit,          // 送出 / 傳送 / 建單
    AssignChief,     // 登記桌派課長 (SC)
    AssignOfficer,   // 課長派經辦 (C)
    Open,            // 收件人開啟，承接
    Withdraw,        // 抽回
    ReturnToOfficer, // 退回經辦
    ReturnToChief,   // 退回課長
    ReturnToDesk,    // 退回登記桌 (上一階段)
    ReturnToCreator, // 退回開單人
    NotifyContractor,// 通知承攬商 (C4，無狀態改變)
    CloseDecision,   // 巡查領隊 是否結案
    Approve,         // 處長 核可結案
    Reject,          // 處長 不核可作廢
    ReturnToLeader,  // 處長 退回巡查領隊
}

/// <summary>由誰領下一關。流程引擎只決定「角色」，實際人員由 resolver 查出。</summary>
public enum AssigneeRule
{
    None,
    SafetyDesk,         // 工安組(69) 登記桌
    ImprovementDesk,    // 督促組別 登記桌
    SpecificPerson,     // 指定人員 (派單)
    AssignedChief,      // 本案已指派的課長
    Manager,            // 本組經理
    ProxyManager,       // 代理經理 (他課課長)
    OfficerOfRecord,    // 簽過名的經辦 (audit_id1)
    ChiefOfRecord,      // 簽過名的課長 (audit_id2)
    ManagerOfRecord,    // 簽過名的經理 (audit_id3)
    Creator,            // 開單人
    Leader,             // 巡查領隊
    NextApprover,       // C6 下一位長官 (由 C6 chain resolver 決定)
    Self,               // 維持現持有人
}

public sealed record Pr1Transition(
    Pr1Step To,
    AssigneeRule Assignee,
    bool SendEmail,
    bool IncrementRevision = false,
    FormStatus Status = FormStatus.InProgress,
    string? HistoryLabel = null);

/// <summary>執行動作時的輸入脈絡。</summary>
public sealed record Pr1ActionContext
{
    /// <summary>C3 跳關：經辦直接送經理，略過課長。</summary>
    public bool IsJump { get; init; }
    /// <summary>C3/C4/C5 代理：課長送他課課長代理經理。</summary>
    public bool IsProxy { get; init; }
    /// <summary>巡查領隊是否同意結案 (CloseDecision 用)。</summary>
    public bool AgreeClose { get; init; }
}

/// <summary>
/// Pr1 簽核流程狀態機 (純邏輯，可單元測試)。
/// 將 legacy Pr1Controller/Pr1Dao 中散落的字串比對集中成一張宣告式轉移表。
/// </summary>
public static class Pr1Workflow
{
    /// <summary>某步驟允許哪些動作 (用於產生按鈕 / 驗證請求)。</summary>
    public static IReadOnlyList<Pr1Action> AllowedActions(Pr1Step step)
    {
        switch (step.Stage)
        {
            case Stage.Create:
                return [Pr1Action.Submit];

            case Stage.SafetyReview or Stage.Improvement or Stage.Verification:
                return step.Position switch
                {
                    Position.Assign => [Pr1Action.AssignChief, Pr1Action.AssignOfficer, Pr1Action.ReturnToDesk, Pr1Action.ReturnToCreator],
                    Position.Officer => [Pr1Action.Submit, Pr1Action.Withdraw, Pr1Action.ReturnToDesk],
                    Position.Chief => [Pr1Action.Submit, Pr1Action.Withdraw, Pr1Action.ReturnToOfficer],
                    Position.Manager => [Pr1Action.Submit, Pr1Action.Withdraw, Pr1Action.ReturnToOfficer, Pr1Action.ReturnToChief],
                    _ => [],
                };

            case Stage.Approval:
                return step.Position switch
                {
                    Position.Leader => [Pr1Action.CloseDecision, Pr1Action.Withdraw],
                    Position.DeputyEng or Position.DeputySafety => [Pr1Action.Submit, Pr1Action.Withdraw],
                    Position.Director => [Pr1Action.Approve, Pr1Action.Reject, Pr1Action.ReturnToLeader],
                    _ => [],
                };

            default:
                return [];
        }
    }

    /// <summary>
    /// 計算一次轉移。回傳 null 代表該步驟不允許此動作。
    /// 不處理「收件人開啟承接 (Open)」與「通知承攬商 (NotifyContractor)」這兩個不改變步驟的動作。
    /// </summary>
    public static Pr1Transition? Next(Pr1Step step, Pr1Action action, Pr1ActionContext ctx)
    {
        if (!AllowedActions(step).Contains(action)) return null;

        return step.Stage switch
        {
            Stage.Create => CreateStage(step, action),
            Stage.SafetyReview => ReviewStage(step, action, ctx, Stage.SafetyReview),
            Stage.Improvement => ReviewStage(step, action, ctx, Stage.Improvement),
            Stage.Verification => ReviewStage(step, action, ctx, Stage.Verification),
            Stage.Approval => ApprovalStage(step, action, ctx),
            _ => null,
        };
    }

    private static Pr1Transition? CreateStage(Pr1Step step, Pr1Action action) => action switch
    {
        // 建單送出 → 進工安組，等待工安組登記桌。
        Pr1Action.Submit => new Pr1Transition(
            new Pr1Step(Stage.SafetyReview, Position.Assign, Transit.Submitted),
            AssigneeRule.SafetyDesk, SendEmail: true, HistoryLabel: "建單"),
        _ => null,
    };

    // 下一個簽核階段：C3→C4→C5→C6。
    private static Stage NextReviewStage(Stage s) => s switch
    {
        Stage.SafetyReview => Stage.Improvement,
        Stage.Improvement => Stage.Verification,
        Stage.Verification => Stage.Approval,
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    private static Pr1Transition? ReviewStage(Pr1Step step, Pr1Action action, Pr1ActionContext ctx, Stage stage)
    {
        switch (action)
        {
            case Pr1Action.AssignChief:
                // 指派課長後仍停在 Assign 關，由課長再指派經辦。
                return new Pr1Transition(step with { Position = Position.Assign, Transit = Transit.Submitted },
                    AssigneeRule.SpecificPerson, SendEmail: true, HistoryLabel: "派單（課長）");

            case Pr1Action.AssignOfficer:
                // 指派經辦後進入經辦關，經辦開啟後即可簽註上呈。
                return new Pr1Transition(new Pr1Step(stage, Position.Officer, Transit.Submitted),
                    AssigneeRule.SpecificPerson, SendEmail: true, HistoryLabel: "派單（經辦）");

            case Pr1Action.Submit when step.Position == Position.Officer:
                // 經辦上呈：跳關→經理，否則→課長。
                return ctx.IsJump
                    ? new Pr1Transition(new Pr1Step(stage, Position.Manager, Transit.Submitted), AssigneeRule.Manager, true, HistoryLabel: "簽核")
                    : new Pr1Transition(new Pr1Step(stage, Position.Chief, Transit.Submitted), AssigneeRule.AssignedChief, true, HistoryLabel: "簽核");

            case Pr1Action.Submit when step.Position == Position.Chief:
                // 課長上呈：代理→他課課長，否則→經理。
                return ctx.IsProxy
                    ? new Pr1Transition(new Pr1Step(stage, Position.Manager, Transit.Submitted), AssigneeRule.ProxyManager, true, HistoryLabel: "簽核")
                    : new Pr1Transition(new Pr1Step(stage, Position.Manager, Transit.Submitted), AssigneeRule.Manager, true, HistoryLabel: "簽核");

            case Pr1Action.Submit when step.Position == Position.Manager:
                // 經理核定，進入下一階段的登記桌。
                return stage switch
                {
                    Stage.SafetyReview => new Pr1Transition(
                        new Pr1Step(Stage.Improvement, Position.Assign, Transit.Submitted),
                        AssigneeRule.ImprovementDesk, true, HistoryLabel: "簽核"),
                    Stage.Improvement => new Pr1Transition(
                        new Pr1Step(Stage.Verification, Position.Assign, Transit.Submitted),
                        AssigneeRule.SafetyDesk, true, HistoryLabel: "簽核"),
                    Stage.Verification => new Pr1Transition(
                        new Pr1Step(Stage.Approval, Position.Leader, Transit.Submitted),
                        AssigneeRule.Leader, true, HistoryLabel: "簽核"),
                    _ => null,
                };

            case Pr1Action.ReturnToOfficer:
                return new Pr1Transition(new Pr1Step(stage, Position.Officer, Transit.Submitted),
                    AssigneeRule.OfficerOfRecord, true, HistoryLabel: "退回經辦");

            case Pr1Action.ReturnToChief:
                return new Pr1Transition(new Pr1Step(stage, Position.Chief, Transit.Submitted),
                    AssigneeRule.ChiefOfRecord, true, HistoryLabel: "退回課長");

            case Pr1Action.ReturnToDesk:
                return new Pr1Transition(new Pr1Step(stage, Position.Assign, Transit.Submitted),
                    AssigneeRule.SafetyDesk, true, HistoryLabel: "退回登記桌");

            case Pr1Action.ReturnToCreator:
                // 工安組登記桌把單退回開單人。
                return new Pr1Transition(Pr1Step.Draft with { Transit = Transit.Submitted },
                    AssigneeRule.Creator, true, IncrementRevision: true, HistoryLabel: "退回開單人");

            default:
                return null;
        }
    }

    private static Pr1Transition? ApprovalStage(Pr1Step step, Pr1Action action, Pr1ActionContext ctx)
    {
        switch (step.Position)
        {
            case Position.Leader when action == Pr1Action.CloseDecision:
                return ctx.AgreeClose
                    // 同意結案：上呈下一位長官 (實際人選由 C6 chain resolver 定)。
                    ? new Pr1Transition(step with { Transit = Transit.Submitted }, AssigneeRule.NextApprover, true, HistoryLabel: "巡查領隊覆核")
                    // 不同意結案：退回經辦組重新處理改善。
                    : new Pr1Transition(new Pr1Step(Stage.Improvement, Position.Assign, Transit.Submitted),
                        AssigneeRule.ImprovementDesk, true, IncrementRevision: true, HistoryLabel: "巡查領隊不同意結案");

            case Position.DeputyEng when action == Pr1Action.Submit:
            case Position.DeputySafety when action == Pr1Action.Submit:
                return new Pr1Transition(step with { Transit = Transit.Submitted }, AssigneeRule.NextApprover, true, HistoryLabel: "批示");

            case Position.Director when action == Pr1Action.Approve:
                return new Pr1Transition(step with { Transit = Transit.Submitted }, AssigneeRule.Self, false,
                    Status: FormStatus.Closed, HistoryLabel: "核可結案");

            case Position.Director when action == Pr1Action.Reject:
                return new Pr1Transition(step with { Transit = Transit.Submitted }, AssigneeRule.Creator, true,
                    Status: FormStatus.Voided, HistoryLabel: "不核可作廢");

            case Position.Director when action == Pr1Action.ReturnToLeader:
                return new Pr1Transition(new Pr1Step(Stage.Approval, Position.Leader, Transit.Submitted),
                    AssigneeRule.Leader, true, HistoryLabel: "退回巡查領隊");

            default:
                return null;
        }
    }
}
